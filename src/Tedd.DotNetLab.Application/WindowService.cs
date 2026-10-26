using System.Text.Json;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public async Task<RunWindow> RunWindowAsync(string analysisId,string runId,double toleranceMilliseconds=1)
    {
        var summary=await AnalysisAsync(analysisId); var capture=Record<Capture>(summary.CaptureId,"capture");
        var run=Record<RunRecord>(runId,"run"); var execution=Record<ExecutionRecord>(run.ExecutionId,"execution");
        if(!execution.RunIds.Contains(runId)||run.Measurement.RunId!=runId||run.Measurement.ExecutionId!=execution.Id||
            run.Target.ProcessId!=capture.Target.ProcessId||run.Target.StartTimeUtcTicks!=capture.Target.StartTimeUtcTicks||
            run.Target.RuntimeInstance!=capture.Target.RuntimeInstance)
            throw new LabException("not_comparable","Run and capture must share the retained process/runtime identity and measurement provenance.");
        var reference=Record<JsonElement>(run.Id+"-measurement","measurement_artifact");
        var measurementArtifact=Record<Artifact>(reference.GetProperty("artifact_id").GetString()!,"artifact");
        string path=await Artifacts.VerifiedPathAsync(measurementArtifact);
        if(Wire.Hash(Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(path)))!=Wire.Hash(run.Measurement))
            throw new LabException("artifact_corrupt","Persisted window boundary differs from its sealed measurement.");
        try { await Artifacts.VerifiedPathAsync(Record<Artifact>(summary.ArtifactId,"artifact")); }
        catch(LabException error) when(error.Code=="artifact_expired")
        {
            return new("window-expired-"+Wire.Hash(new { analysisId,runId }),analysisId,capture.Id,runId,execution.Id,run.Measurement.BlockId,
                Availability.ArtifactExpired,"unavailable",run.Measurement.StartTicks,run.Measurement.EndTicks,run.Measurement.Frequency,null,null,null,
                new("gc.suspension_overlap_ns",Availability.ArtifactExpired,null,"ns","process_overlap_with_run","Raw trace evidence expired."),0,[],
                ResidualToleranceMilliseconds:toleranceMilliseconds);
        }
        var window=WindowAnalyzer.Analyze(summary,capture,run.Measurement,toleranceMilliseconds);
        var artifact=await Artifacts.WriteJsonAsync(window.Id,window,WindowAnalyzer.Version);
        lock(gate)
        {
            Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"execution_window");
            Upsert(window.Id,"execution_window",window);
            Upsert(window.Id+"-manifest","window_manifest",new { artifact_id=artifact.Id,measurement_artifact_id=measurementArtifact.Id,trace_artifact_id=summary.ArtifactId });
        }
        return window;
    }
}
