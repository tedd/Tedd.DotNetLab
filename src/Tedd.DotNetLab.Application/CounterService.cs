using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public async Task<CounterWindow> CounterWindowAsync(string analysisId,string name,double startMilliseconds,double endMilliseconds,
        string? kind=null,string? source=null,string? series=null)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>256||!double.IsFinite(startMilliseconds)||!double.IsFinite(endMilliseconds)||startMilliseconds<0||endMilliseconds<=startMilliseconds)
            throw new LabException("invalid_interval","Counter query requires a name and finite positive trace interval.");
        var summary=await AnalysisAsync(analysisId); var capture=Record<Capture>(summary.CaptureId,"capture");
        if(summary.CaptureId!=capture.Id||summary.ProfileHash!=capture.Profile.Hash)
            throw new LabException("artifact_corrupt","Counter summary differs from its retained capture provenance.");
        var provenance=new Provenance(capture.Id,summary.ArtifactId,summary.ParserVersion,summary.ProfileHash);
        try { await Artifacts.VerifiedPathAsync(Record<Artifact>(summary.ArtifactId,"artifact")); }
        catch(LabException error) when(error.Code=="artifact_expired")
        {
            return new(name,source,series,kind,"unknown",Availability.ArtifactExpired,null,"unavailable",startMilliseconds,endMilliseconds,0,0,0,
                ["raw_trace_expired"],provenance);
        }
        if(!capture.Profile.DataClasses.Contains("counters")) return new(name,source,series,kind,"unknown",Availability.NotCollected,null,"unavailable",
            startMilliseconds,endMilliseconds,0,0,0,["counter_provider_not_enabled"],provenance);
        return CounterAnalyzer.Analyze(summary.Counters,name,startMilliseconds,endMilliseconds,kind,source,series,summary.Quality,summary.TraceBounds,provenance);
    }
}
