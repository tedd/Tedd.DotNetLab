using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    private async Task<TraceSummary> ParseTraceAsync(Capture capture,Artifact artifact,string operationId,CancellationToken cancellation)
    {
        var policy=new TraceParserPolicy(Limits.MaximumCaptureBytes,Limits.MaximumParserMemoryBytes,
            Math.Min(Limits.MaximumParserDurationSeconds,Limits.MaximumDurationSeconds),Limits.MaximumParserEvents,Limits.MaximumParserOutputBytes);
        policy.Validate(); using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(policy.MaximumDurationSeconds));
        try
        {
            string path=await Artifacts.VerifiedPathAsync(artifact,deadline.Token);
            if(artifact.Length>policy.MaximumInputBytes) throw new LabException("resource_limit","Trace input exceeds its parser byte policy.");
            return await traceParser.AnalyzeAsync(new(Wire.Version,Catalog.Root,path,artifact,capture,Artifacts.CreateIncoming(operationId),policy),deadline.Token);
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested)
        { throw new LabException("parser_timeout","Trace verification/parsing exceeded its wall deadline."); }
    }
    public Operation StartTraceAnalysis(string captureId,string key)
    {
        var capture=Record<Capture>(captureId,"capture");
        if(capture.ArtifactId is null) throw new LabException("not_collected","Capture has no retained raw trace.");
        var artifact=Record<Artifact>(capture.ArtifactId,"artifact");
        var target=new TargetIdentity("trace-analysis-"+captureId,0,0,captureId,"retained-evidence","analysis");
        return Start("trace_analysis",target,key,Wire.Hash(new { captureId,source_hash=artifact.Hash,TraceAnalyzer.Version,limits=Limits }),async(operation,cancellation)=>
        {
            var index=await AnalyzeAsync(captureId,cancellation); Catalog.Transition(operation.Id,OperationState.Completed,index.Id); return true;
        });
    }
}
