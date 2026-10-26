using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using System.Text.Json;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class DiagnosticObjectiveTests
{
    private record Fixture(string ExecutionId,string TraceId,string AnalysisId,string PlanId);
    private static async Task<Fixture> Create(LabService service,string defect="",bool factorial=false,bool gcPrimary=false)
    {
        string executionId=Wire.Id("execution"); string[] variants=factorial?["00","10","01","11"]:["baseline","candidate"];
        FactorialDesign? design=null;
        if(factorial)
        {
            var a=service.Candidate("A","a",[]); var b=service.Candidate("B","b",[]); var both=service.Candidate("A+B","ab",[a.Id,b.Id]);
            design=new(variants[0],variants[1],variants[2],variants[3],a.Id,b.Id,both.Id,
                new Dictionary<string,ContrastScale> { ["scenario.ns_per_operation"]=ContrastScale.Log,["gc.suspension_overlap_ns"]=ContrastScale.Absolute });
        }
        var gc=ObjectiveTests.Gc();
        var plan=service.Plan(new("","window-fixture","1","input",variants,10,10,1,42,"baseline-context",true,"Independent synthetic matched blocks.",false,60,
            new(FamilySize:factorial?12:gcPrimary?1:2),PrimaryMetric:gcPrimary?gc.Metric:"scenario.ns_per_operation",
            GuardrailMetrics:gcPrimary?[]:[gc.Metric],GuardrailPolicies:gcPrimary?[]:[gc],Factorial:design,PrimaryPolicy:gcPrimary?gc:null));
        var target=new TargetIdentity("fixture",1,1,"fixture-runtime","10.0.12","X64");
        var scenario=new ScenarioDescriptor(plan.ScenarioId,plan.ScenarioVersion,plan.InputHash,ResetLevel.LogicalState,ThreadAffinity.Any,false,"One completed operation",variants);
        var ids=new List<string>(); var markers=new List<HarnessMarker>(); var pauses=new List<SuspensionInterval>();
        for(int block=0;block<10;block++)
            for(int arm=0;arm<variants.Length;arm++)
            {
                string runId=Wire.Id("run"),blockId=executionId+"-block-"+block;
                long start=1000+block*2000+arm*400,elapsed=arm==0?300:200+block;
                var measurement=new Measurement(executionId,runId,blockId,variants[arm],start,start+elapsed,1000,10,0,0,1,1,true,null,plan.InputHash,plan.ScenarioVersion);
                var run=new RunRecord(runId,executionId,plan.Id,Wire.Hash(plan),block,arm,target,scenario,Profiles.Resolve(plan.Profile).Hash,"fixture-observer",measurement);
                service.Catalog.Put(runId,"run",run); var artifact=await service.Artifacts.WriteJsonAsync(executionId,measurement,"fixture-measurement");
                service.Catalog.Put(artifact.Id,"artifact",artifact); service.Catalog.Put(runId+"-measurement","measurement_artifact",new { artifact_id=artifact.Id }); ids.Add(runId);
                markers.Add(new(runId,blockId,executionId,start-10,1000,start-10,"clock_before"));
                markers.Add(new(runId,blockId,executionId,start+elapsed+10,1000,start+elapsed+10,"clock_after"));
                markers.Add(new(runId,blockId,executionId,start,1000,start,"measurement_start"));
                markers.Add(new(runId,blockId,executionId,start+elapsed,1000,start+elapsed,"measurement_end"));
                double duration=factorial?new[]{100+block*.1,104+block*.2,97+block*.05,90+block*.3}[arm]:arm==0?50+block:20+block*.5;
                pauses.Add(new(1,start+10,start+10+duration,"SuspendForGC","complete"));
            }
        if(defect=="silent") pauses.Clear();
        if(defect=="incomplete") pauses.Add(new(1,1010,null,"SuspendForGC","missing_end"));
        if(defect is "shared_episode" or "legacy_shared_episode") pauses.Add(new(1,1010,3500,"SuspendForGC","complete"));
        if(defect=="no_clock") markers.RemoveAll(marker=>marker.Phase.StartsWith("clock_",StringComparison.Ordinal));
        if(defect=="clock_residual") markers[0]=markers[0] with { TraceMilliseconds=markers[0].TraceMilliseconds+20 };
        var raw=await service.Artifacts.WriteJsonAsync(executionId,new { fixture="synthetic retained trace input" },"fixture-trace"); service.Catalog.Put(raw.Id,"artifact",raw);
        var capture=new Capture(Wire.Id("capture"),defect=="identity"?target with { StartTimeUtcTicks=2 }:target,Profiles.Resolve(plan.Profile),CaptureState.Sealed,raw.Id,null,DateTimeOffset.UtcNow);
        service.Catalog.Put(capture.Id,"capture",capture);
        string analysisId=Wire.Id("analysis");
        var quality=new EvidenceQuality("parsed_capture",defect=="loss"?"unknown":"none_reported",TraceAnalyzer.Version,[]);
        var summary=new TraceSummary(analysisId,capture.Id,raw.Id,capture.Profile.Hash,defect=="parser"?"unqualified-parser":TraceAnalyzer.Version,quality,[],[],[],[],markers,0,new Dictionary<string,long>(),pauses,new(0,22000));
        if(defect=="legacy_shared_episode") summary=summary with { Suspensions=pauses.Select(p=>new TimeInterval(p.StartMilliseconds!.Value,p.EndMilliseconds!.Value,p.Reason)).ToArray(),SuspensionEvidence=null };
        var derived=await service.Artifacts.WriteJsonAsync(analysisId,summary,TraceAnalyzer.Version); service.Catalog.Put(derived.Id,"artifact",derived);
        service.Catalog.Put(analysisId,"analysis",new AnalysisIndex(analysisId,capture.Id,derived.Id,summary.ParserVersion,quality));
        service.Catalog.Put(executionId,"execution",new ExecutionRecord(executionId,plan.Id,Wire.Hash(plan),target,"completed",ids,DateTimeOffset.UtcNow,
            CaptureId:capture.Id,AnalysisId:defect=="no_original_analysis"?null:analysisId));
        if(defect is "corrupt_trace" or "corrupt_analysis")
            await File.WriteAllTextAsync(await service.Artifacts.VerifiedPathAsync(defect=="corrupt_trace"?raw:derived),"corrupted source bytes");
        return new(executionId,raw.Id,analysisId,plan.Id);
    }
    [Fact,Case("IT-CLOCK-FIT-RETENTION","FR-012,FR-013,FR-021,FR-026,FR-031,FR-072","M1","Integration")]
    public async Task RejectedClockDiagnosticsRemainSealedAcrossRestartWithoutRewritingHistoricalWindows()
    {
        using var workspace=new TestWorkspace(); string analysisId,runId,windowId,legacyId,legacyHash,windowHash;
        await using(var service=new LabService(workspace.Root))
        {
            var fixture=await Create(service,"clock_residual"); analysisId=fixture.AnalysisId;
            runId=service.Catalog.Get<ExecutionRecord>(fixture.ExecutionId).RunIds[0];
            var summary=await service.AnalysisAsync(analysisId); var run=service.Catalog.Get<RunRecord>(runId);
            var current=WindowAnalyzer.Analyze(summary,service.Catalog.Get<Capture>(summary.CaptureId),run.Measurement);
            legacyId="window-"+Wire.Hash(new { summary.Id,summary.ParserVersion,measurement=run.Measurement,toleranceMilliseconds=1d,algorithm=WindowAnalyzer.Version });
            var legacy=current with { Id=legacyId,ClockFitFailure=null }; service.Catalog.Put(legacyId,"execution_window",legacy); legacyHash=Wire.Hash(legacy);
            var reply=await new LabTools(service).Query(new QueryRunWindow(analysisId,runId)); Assert.Equal("ok",reply.Status);
            var retained=reply.Data!.Value.Deserialize<RunWindow>(Wire.Json)!; windowId=retained.Id; windowHash=Wire.Hash(retained);
            Assert.NotEqual(legacyId,windowId); Assert.Equal(Availability.Invalid,retained.Availability);
            Assert.Null(retained.ClockMapping); Assert.Null(retained.StartMilliseconds); Assert.Null(retained.GcSuspensionOverlap.Value);
            Assert.Equal("excessive_residual",retained.ClockFitFailure!.Failure); Assert.Equal(40,retained.ClockFitFailure.PairCount);
            Assert.True(retained.ClockFitFailure.MaximumResidualMilliseconds>retained.ResidualToleranceMilliseconds);
            var manifest=service.Catalog.Get<JsonElement>(windowId+"-manifest");
            var artifact=service.Catalog.Get<Artifact>(manifest.GetProperty("artifact_id").GetString()!); Assert.True(service.Catalog.IsPinned(artifact.Id));
            Assert.Equal(windowHash,Wire.Hash(Wire.Deserialize<RunWindow>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(artifact)))));
            Assert.Equal(legacyHash,service.Catalog.Get(legacyId).Hash);
        }
        await using var reopened=new LabService(workspace.Root); var again=await reopened.RunWindowAsync(analysisId,runId);
        Assert.Equal(windowId,again.Id); Assert.Equal(windowHash,Wire.Hash(again)); Assert.Equal(legacyHash,reopened.Catalog.Get(legacyId).Hash);
    }
    [Fact,Case("UT-GC-005,UT-STAT-027","FR-012,FR-022,FR-036","M2","Integration")]
    public async Task ExecutionBoundGcGuardrail_UsesVerifiedWindowsAndRetainsSourceManifestAcrossRestart()
    {
        using var workspace=new TestWorkspace(); string vectorId,executionId,traceId;
        await using(var service=new LabService(workspace.Root))
        {
            var fixture=await Create(service); executionId=fixture.ExecutionId; traceId=fixture.TraceId;
            var plan=service.Catalog.Get<ExperimentPlan>(fixture.PlanId); Assert.NotNull(plan.DiagnosticWindows);
            var index=Assert.Single(await service.CompareObjectivesAsync(executionId)); vectorId=index.Id;
            var vector=await service.ReadObjectivesAsync(index.Id); var gc=vector.Effects[1];
            Assert.Equal(Availability.Available,gc.Availability); Assert.Equal(EffectClass.Improved,gc.EffectClass); Assert.True(gc.NonInferiorityPassed);
            Assert.Equal(-32.25e6,gc.Effect); Assert.Equal("ns",gc.EffectUnit); Assert.False(vector.PromotionEligible); Assert.NotNull(index.DiagnosticEvidence);
            var manifest=Wire.Deserialize<DiagnosticObjectiveManifest>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(index.DiagnosticEvidence!.ArtifactId))));
            Assert.True(manifest.RawVerified); Assert.Equal(fixture.AnalysisId,manifest.Analysis!.Id); Assert.Equal(20,manifest.Runs.Count);
            Assert.All(manifest.Runs,r=>Assert.Equal(Availability.Available,r.Value.Status)); Assert.True(service.Catalog.IsPinned(traceId));
            Assert.All(manifest.Runs,r=>Assert.NotNull(r.Window!.ClockMapping));
            var comparison=Assert.Single(await service.CompareAsync(executionId)); Assert.Equal(index.Id,comparison.ObjectiveVectorId);
        }
        await using var reopened=new LabService(workspace.Root); Assert.Equal(vectorId,Assert.Single(await reopened.CompareObjectivesAsync(executionId)).Id);
        reopened.Artifacts.Delete(reopened.Catalog.Get<Artifact>(traceId));
        Assert.Equal(vectorId,(await reopened.ReadObjectivesAsync(vectorId)).Id);
        var expired=Assert.Single(await reopened.CompareObjectivesAsync(executionId)); Assert.NotEqual(vectorId,expired.Id);
        Assert.Equal(Availability.ArtifactExpired,expired.EffectsWithoutRunIds[1].Availability); Assert.Null(expired.EffectsWithoutRunIds[1].Effect);
        Assert.Equal(EffectClass.Improved,expired.EffectsWithoutRunIds[0].EffectClass);
    }
    [Theory,InlineData("loss",Availability.Partial),InlineData("incomplete",Availability.Partial),InlineData("silent",Availability.NotCollected),
        InlineData("no_clock",Availability.NotCollected),InlineData("identity",Availability.Invalid),InlineData("no_original_analysis",Availability.NotCollected),
        InlineData("parser",Availability.Invalid),InlineData("corrupt_trace",Availability.Invalid),InlineData("corrupt_analysis",Availability.Invalid),
        Case("UT-GC-007,UT-MET-004,UT-STAT-027","FR-013,FR-021,FR-032,FR-036","M2","Integration")]
    public async Task IncompleteUnrelatedOrMissingDiagnostics_BlockOnlyGcInference(string defect,Availability expected)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var fixture=await Create(service,defect);
        var index=Assert.Single(await service.CompareObjectivesAsync(fixture.ExecutionId)); var gc=index.EffectsWithoutRunIds[1];
        Assert.Equal(expected,gc.Availability); Assert.Null(gc.Effect); Assert.False(gc.NonInferiorityPassed); Assert.False(index.PromotionEligible);
        Assert.Equal(EffectClass.Improved,index.EffectsWithoutRunIds[0].EffectClass);
        Assert.NotNull(index.EffectsWithoutRunIds[0].DifferenceInterval); Assert.NotNull(index.DiagnosticEvidence);
    }
    [Fact,Case("UT-ART-010","FR-026,FR-032","M2","Integration")]
    public async Task LaterReprocessingCannotRedirectAnExistingExecutionDiagnosticBinding()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var fixture=await Create(service);
        var original=Assert.Single(await service.CompareObjectivesAsync(fixture.ExecutionId)); var summary=await service.AnalysisAsync(fixture.AnalysisId);
        var later=summary with { Id=Wire.Id("analysis"),SuspensionEvidence=summary.SuspensionEvidence!.Select(p=>p with { EndMilliseconds=p.StartMilliseconds+1 }).ToArray() };
        var artifact=await service.Artifacts.WriteJsonAsync(later.Id,later,TraceAnalyzer.Version); service.Catalog.Put(artifact.Id,"artifact",artifact);
        service.Catalog.Put(later.Id,"analysis",new AnalysisIndex(later.Id,later.CaptureId,artifact.Id,later.ParserVersion,later.Quality));
        var retained=Assert.Single(await service.CompareObjectivesAsync(fixture.ExecutionId)); Assert.Equal(original.Id,retained.Id);
        Assert.Equal(original.DiagnosticEvidence,retained.DiagnosticEvidence);
        Assert.Equal(fixture.AnalysisId,service.Catalog.Get<ExecutionRecord>(fixture.ExecutionId).AnalysisId);
    }
    [Theory,InlineData("shared_episode"),InlineData("legacy_shared_episode"),Case("UT-EXP-004","FR-022,FR-032,FR-033","M2","Integration")]
    public async Task OneGcEpisodeAcrossDeclaredBlocks_CannotManufactureIndependentGuardrailEvidence(string defect)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var fixture=await Create(service,defect);
        var index=Assert.Single(await service.CompareObjectivesAsync(fixture.ExecutionId)); var gc=index.EffectsWithoutRunIds[1];
        Assert.Equal(EffectClass.NotComparable,gc.EffectClass); Assert.Contains("gc_episode_spans_multiple_declared_blocks",gc.ReasonCodes);
        Assert.Null(gc.DifferenceInterval); Assert.Equal(EffectClass.Improved,index.EffectsWithoutRunIds[0].EffectClass);
    }
    [Fact,Case("UT-STAT-018,UT-STAT-028","FR-022,FR-033,FR-036","M2","Integration")]
    public async Task GcPrimaryPolicy_UsesAbsoluteWindowEffectsWithoutAFalseRatio()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var fixture=await Create(service,gcPrimary:true);
        var comparison=Assert.Single(await service.CompareAsync(fixture.ExecutionId)); Assert.Equal(EffectClass.Improved,comparison.EffectClass);
        Assert.Equal("gc.suspension_overlap_ns",comparison.PrimaryEffect!.Metric); Assert.Equal(-32.25e6,comparison.PrimaryEffect.Effect);
        Assert.Null(comparison.Ratio); Assert.Null(comparison.ChangePercent); Assert.False(comparison.PromotionEligible);
    }
    [Fact,Case("UT-EXP-009,UT-STAT-030","FR-022,FR-036","M2","Integration")]
    public async Task FactorialGcGuardrail_ReusesOneBoundSourceAcrossFiveEffectsAndInteraction()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var fixture=await Create(service,factorial:true);
        var index=await service.CompareFactorialAsync(fixture.ExecutionId); var gc=index.InteractionsWithoutRunIds.Single(i=>i.Metric=="gc.suspension_overlap_ns");
        Assert.Equal("usable",gc.Quality); Assert.NotNull(gc.Interval); Assert.True(gc.Contrast<0);
        var links=new List<string>();
        foreach(string vectorId in index.EffectVectorIds.Values)
        {
            var vector=await service.ReadObjectivesAsync(vectorId); Assert.Equal(Availability.Available,vector.Effects[1].Availability);
            links.Add(vector.DiagnosticEvidence!.ArtifactId);
        }
        Assert.Single(links.Distinct());
    }
    [Fact,Case("UT-TIME-005,UT-EXP-006","FR-012,FR-031,FR-032","M2","Unit")]
    public async Task DiagnosticWindowToleranceAndAlgorithmsMustBeFrozenBeforeExecution()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var fixture=await Create(service);
        var plan=service.Catalog.Get<ExperimentPlan>(fixture.PlanId);
        foreach(var invalid in new[]{new DiagnosticWindowPolicy(double.NaN),new DiagnosticWindowPolicy(0),new DiagnosticWindowPolicy(11),
            new DiagnosticWindowPolicy(ParserVersion:"unqualified"),new DiagnosticWindowPolicy(WindowAlgorithm:"unqualified")})
            Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.Plan(plan with { DiagnosticWindows=invalid })).Code);
        Assert.Throws<LabException>(()=>service.Catalog.Put(plan.Id,"plan",plan with { DiagnosticWindows=new(.5) },1));
    }
    [Fact,Case("IT-CAP-001,IT-HAR-001","FR-012,FR-022,FR-036","M2","Integration")]
    public async Task LiveCapturedGcGuardrail_BindsOriginalAnalysisAndPreservesObservedWindowQuality()
    {
        using var workspace=new TestWorkspace(); using var process=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var identity=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var scenario=(await service.ScenariosAsync(identity.Id,CancellationToken.None)).Scenarios.Single(s=>s.Id=="gc-window"); var gc=ObjectiveTests.Gc();
            var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,10,256,1,42,"baseline-context",true,
                "Declared controlled fixture blocks.",false,60,new(FamilySize:2),GuardrailMetrics:[gc.Metric],GuardrailPolicies:[gc]));
            var execute=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,120,20); var observe=service.IssueGrant(identity.Id,Permission.Observe,120,1);
            var complete=await ServiceTests.Wait(service,(await service.StartExecutionAsync(identity.Id,identity.Epoch,plan.Id,execute.Id,"joined-gc",CancellationToken.None,observe.Id)).Id);
            Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); var execution=service.Catalog.Get<ExecutionRecord>(complete.ResultId!);
            Assert.NotNull(execution.AnalysisId); var index=Assert.Single(await service.CompareObjectivesAsync(execution.Id));
            var manifest=Wire.Deserialize<DiagnosticObjectiveManifest>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(index.DiagnosticEvidence!.ArtifactId))));
            Assert.Equal(execution.AnalysisId,manifest.Analysis!.Id); Assert.Equal(20,manifest.Runs.Count); Assert.True(manifest.RawVerified);
            Assert.Contains(manifest.Runs,r=>r.Window?.GcSuspensionOverlap.Value>0);
            Assert.All(manifest.Runs,r=>Assert.True(r.Value.Status is Availability.Available or Availability.Partial,Wire.Serialize(r)));
            Assert.False(index.PromotionEligible);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
