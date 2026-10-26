using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using System.Text.Json;

namespace Tedd.DotNetLab.Application;

public record DiagnosticRunEvidence(FrozenRecordReference Run,string MeasurementArtifactId,string MeasurementArtifactHash,MetricValue Value,RunWindow? Window);
public record DiagnosticObjectiveManifest(string ExecutionId,string PlanHash,DiagnosticWindowPolicy Policy,FrozenRecordReference Execution,
    FrozenRecordReference? Analysis,FrozenRecordReference? Capture,Artifact? AnalysisArtifact,Artifact? TraceArtifact,bool RawVerified,
    IReadOnlyList<DiagnosticRunEvidence> Runs,IReadOnlyList<string> DependenceBlockers,string Algorithm="execution-gc-objectives/v1");

public sealed partial class LabService
{
    private const string DiagnosticObjectiveVersion="execution-gc-objectives/v1";
    private sealed record DiagnosticMetricSet(DiagnosticEvidenceLink Link,IReadOnlyDictionary<string,MetricValue> Values,IReadOnlyList<string> Blockers);
    private static bool UsesGcObjective(ExperimentPlan plan)=>plan.PrimaryMetric=="gc.suspension_overlap_ns"||
        (plan.GuardrailPolicies??[]).Any(p=>p.Metric=="gc.suspension_overlap_ns");
    private static ExperimentPlan FreezeDiagnosticWindows(ExperimentPlan plan)=>UsesGcObjective(plan)&&plan.DiagnosticWindows is null?
        plan with { DiagnosticWindows=new() }:plan;
    private FrozenRecordReference DiagnosticReference(string id)
    { var record=Catalog.Get(id); return new(record.Id,record.Revision,record.Hash); }
    private async Task<DiagnosticMetricSet?> DiagnosticMetricsAsync(ExecutionRecord execution,ExperimentPlan plan,IReadOnlyList<RunRecord> runs)
    {
        if(!UsesGcObjective(plan)) return null;
        var policy=plan.DiagnosticWindows??new(); var definition=Metrics.Define("gc.suspension_overlap_ns");
        var values=new Dictionary<string,MetricValue>(StringComparer.Ordinal); var evidence=new List<DiagnosticRunEvidence>();
        var episodeBlocks=new Dictionary<int,HashSet<string>>(); var blockers=new List<string>();
        FrozenRecordReference? analysisReference=null,captureReference=null; Artifact? analysisArtifact=null,traceArtifact=null;
        TraceSummary? summary=null; Capture? capture=null; bool rawVerified=false;
        Availability missing=Availability.NotCollected; string reason="Execution has no original retained diagnostic analysis; later or unrelated captures cannot substitute.";
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
        try
        {
            if(execution.CaptureId is not null&&execution.AnalysisId is not null)
            {
                try
                {
                    var index=Record<AnalysisIndex>(execution.AnalysisId,"analysis"); analysisReference=DiagnosticReference(index.Id);
                    capture=Record<Capture>(execution.CaptureId,"capture"); captureReference=DiagnosticReference(capture.Id);
                    analysisArtifact=Record<Artifact>(index.ArtifactId,"artifact");
                    if(analysisArtifact.Length>16777216) throw new LabException("resource_limit","Diagnostic analysis exceeds its read budget.");
                    summary=Wire.Deserialize<TraceSummary>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(analysisArtifact,deadline.Token),deadline.Token));
                    if(capture.Target!=execution.Target||index.CaptureId!=capture.Id||summary.Id!=index.Id||summary.CaptureId!=capture.Id||summary.ArtifactId!=capture.ArtifactId||
                        summary.ProfileHash!=capture.Profile.Hash||summary.ParserVersion!=policy.ParserVersion||index.ParserVersion!=policy.ParserVersion||
                        policy.WindowAlgorithm!=WindowAnalyzer.Version||capture.Profile.Hash!=Profiles.Resolve(plan.Profile).Hash)
                        throw new LabException("not_comparable","Original analysis, profile, or algorithm differs from the frozen execution/window policy.");
                    traceArtifact=Record<Artifact>(summary.ArtifactId,"artifact"); await Artifacts.VerifiedPathAsync(traceArtifact,deadline.Token); rawVerified=true;
                }
                catch(LabException error) when(error.Code is "artifact_expired" or "artifact_corrupt" or "record_not_found" or "not_comparable")
                { missing=error.Code=="artifact_expired"?Availability.ArtifactExpired:Availability.Invalid; reason=error.Message; summary=null; }
            }
            var suspensionEvidence=summary is null?[]:summary.SuspensionEvidence??summary.Suspensions.Select(p=>new SuspensionInterval(-1,p.Start,p.End,p.Reason,"complete")).ToArray();
            var pauses=suspensionEvidence.Select((pause,index)=>(Pause:pause,Index:index)).Where(item=>item.Pause.IsGc&&
                item.Pause.Completeness=="complete"&&item.Pause.StartMilliseconds.HasValue&&item.Pause.EndMilliseconds.HasValue).ToArray();
            foreach(var run in runs)
            {
                deadline.Token.ThrowIfCancellationRequested(); RunWindow? window=null;
                var value=new MetricValue(definition.Name,missing,null,definition.Unit,definition.ObservationKind,definition.Scope,reason);
                if(summary is not null&&capture is not null)
                {
                    if(run.Target!=capture.Target)
                        value=value with { Status=Availability.Invalid,Reason="Run and capture process/runtime/epoch identities differ." };
                    else
                    {
                        window=WindowAnalyzer.Analyze(summary,capture,run.Measurement,policy.ResidualToleranceMilliseconds);
                        value=new(definition.Name,window.Availability,window.GcSuspensionOverlap.Value,definition.Unit,definition.ObservationKind,definition.Scope,
                            string.Join("; ",window.Warnings.Prepend(window.GcSuspensionOverlap.Reason??"")));
                        if(window.StartMilliseconds.HasValue&&window.EndMilliseconds.HasValue)
                            foreach(var item in pauses.Where(item=>item.Pause.StartMilliseconds<window.EndMilliseconds&&item.Pause.EndMilliseconds>window.StartMilliseconds))
                            {
                                if(!episodeBlocks.TryGetValue(item.Index,out var blocks)) episodeBlocks[item.Index]=blocks=new(StringComparer.Ordinal);
                                blocks.Add(run.Measurement.BlockId);
                            }
                    }
                }
                var measurementReference=Record<JsonElement>(run.Id+"-measurement","measurement_artifact");
                var measurementArtifact=Record<Artifact>(measurementReference.GetProperty("artifact_id").GetString()!,"artifact");
                values.Add(run.Id,value); evidence.Add(new(DiagnosticReference(run.Id),measurementArtifact.Id,measurementArtifact.Hash,value,window));
            }
            if(episodeBlocks.Values.Any(blocks=>blocks.Count>1)) blockers.Add("gc_episode_spans_multiple_declared_blocks");
            var manifest=new DiagnosticObjectiveManifest(execution.Id,execution.PlanHash,policy,DiagnosticReference(execution.Id),analysisReference,captureReference,
                analysisArtifact,traceArtifact,rawVerified,evidence,blockers);
            var artifact=await Artifacts.WriteJsonAsync(Wire.Id("diagnostic-objectives"),manifest,DiagnosticObjectiveVersion,67108864,deadline.Token);
            lock(gate)
            {
                Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"diagnostic_objective_manifest");
                if(rawVerified)
                { Catalog.Pin(traceArtifact!.Id,"diagnostic_objective_trace"); Catalog.Pin(analysisArtifact!.Id,"diagnostic_objective_analysis"); }
            }
            return new(new(artifact.Id,artifact.Hash,DiagnosticObjectiveVersion,runs.Count),values,blockers);
        }
        catch(OperationCanceledException) when(deadline.IsCancellationRequested)
        { throw new LabException("resource_limit","Diagnostic objective derivation exceeded its frozen duration budget."); }
    }
    private static MetricValue ObjectiveValue(string metric,string runId,Measurement measurement,DiagnosticMetricSet? diagnostics)=>
        metric=="gc.suspension_overlap_ns"&&diagnostics is not null?diagnostics.Values[runId]:Metrics.Read(metric,measurement);
}
