using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record StackAnalysisPolicy(int MaximumEvents=250000,int MaximumSamples=100000,int MaximumDepth=256,int MaximumMethods=10000,
    long MaximumRawBytes=134217728,long MaximumTemporaryBytes=536870912,int MaximumDurationSeconds=300,
    double? StartMilliseconds=null,double? EndMilliseconds=null);
public record StackAnalysisReport(string Id,string CaptureId,string TraceArtifactId,string ProfileHash,string ParserVersion,
    StackAnalysisPolicy Policy,StackEvidence Evidence,EvidenceQuality Quality,long OtherProcessSamples,
    string Version="sampled-stack-report/v1",IReadOnlyDictionary<string,long>? IndexedEventCounts=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] TraceParserExecution? ParserExecution=null);
public record StackAnalysisIndex(string Id,string CaptureId,string ArtifactId,long ObservedSamples,int Methods,double? SymbolCoverage,EvidenceQuality Quality);
public record SampledMethodQuery(string Selector,Availability Status,long ObservedSamples,IReadOnlyList<SampledMethod> Matches,long? SelectedSelfCount,long? SelectedInclusiveCount,
    string Meaning="No match means zero observed selected samples, not proof that the method never executed. Selection filters retained evidence after capture; it does not change provider enablement or reduce producer emission cost. No invocation or per-call duration inference is made.",
    Provenance? Provenance=null,string Filtering="post_capture_exact_method_identity_or_name",
    string SampleDenominator="all_observed_samples_in_the_retained_analysis");

public sealed partial class LabService
{
    public Operation StartStackAnalysis(string captureId,string key,StackAnalysisPolicy? policy=null)
    {
        policy??=new();
        if(policy.MaximumEvents is <1 or >1000000||policy.MaximumSamples is <1 or >1000000||policy.MaximumDepth is <1 or >1024||policy.MaximumMethods is <1 or >100000||
            policy.MaximumRawBytes is <1 or >536870912||policy.MaximumTemporaryBytes is <1048576 or >2147483648||policy.MaximumDurationSeconds is <1 or >600||
            policy.MaximumDurationSeconds>Limits.MaximumDurationSeconds||policy.StartMilliseconds.HasValue!=policy.EndMilliseconds.HasValue||
            policy.StartMilliseconds is double start&&(!double.IsFinite(start)||start<0||!double.IsFinite(policy.EndMilliseconds!.Value)||policy.EndMilliseconds<=start))
            throw new LabException("invalid_plan","Invalid bounded stack-analysis policy.");
        if(policy.MaximumEvents>Limits.MaximumParserEvents||policy.MaximumRawBytes>Limits.MaximumCaptureBytes)
            throw new LabException("resource_limit","Stack analysis exceeds the workspace parser event/input policy.");
        var capture=Record<Capture>(captureId,"capture");
        if(!capture.Profile.DataClasses.Contains("samples")||capture.ArtifactId is null)
            throw new LabException("not_collected","Capture has no retained stack-sampling provider.");
        var artifact=Record<Artifact>(capture.ArtifactId,"artifact");
        if(artifact.Length>policy.MaximumRawBytes) throw new LabException("resource_limit","Raw trace exceeds the stack conversion byte budget.");
        string scope="stack-analysis-"+capture.Id;
        return Start("stack_analysis",new(scope,0,0,scope,"workspace","local"),key,Wire.Hash(new { captureId,artifact.Hash,policy }),async(operation,cancellation)=>
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(policy.MaximumDurationSeconds));
            using var permit=await activityScheduler.EnterParserAsync(deadline.Token);
            string raw=await Artifacts.VerifiedPathAsync(artifact,deadline.Token);
            long free=DiskSpace.AvailableBytes(Catalog.Root);
            if(free<Limits.DiskReserveBytes||policy.MaximumTemporaryBytes>free-Limits.DiskReserveBytes)
                throw new LabException("resource_limit","Stack conversion would violate the workspace disk reserve.");
            string output=Artifacts.CreateIncoming(operation.Id);
            var workerPolicy=new TraceParserPolicy(policy.MaximumRawBytes,Limits.MaximumParserMemoryBytes,
                Math.Min(policy.MaximumDurationSeconds,Limits.MaximumParserDurationSeconds),policy.MaximumEvents,Limits.MaximumParserOutputBytes);
            var stackPolicy=new StackParserPolicy(policy.MaximumEvents,policy.MaximumSamples,policy.MaximumDepth,policy.MaximumMethods,
                policy.MaximumRawBytes,policy.MaximumTemporaryBytes,policy.MaximumDurationSeconds,policy.StartMilliseconds,policy.EndMilliseconds);
            StackParserResult parsed; TraceParserExecution execution;
            try
            {
                (parsed,execution)=await traceParser.AnalyzeStacksAsync(new(Wire.Version,Catalog.Root,raw,artifact,capture,output,workerPolicy,"stacks",stackPolicy),deadline.Token);
            }
            catch(Exception error) when(error is not OutOfMemoryException)
            {
                Catalog.Put(operation.Id+"-stack-failure","stack_analysis_failure",new { phase="isolated_worker",error_type=error.GetType().Name,
                    code=error is LabException lab?lab.Code:"stack_parser_failed" }); throw;
            }
            var evidence=parsed.Evidence; var quality=parsed.Quality;
            string idReport=Wire.Id("stack-analysis"); var report=new StackAnalysisReport(idReport,capture.Id,artifact.Id,capture.Profile.Hash,StackParser.Version,
                policy,evidence,quality,parsed.OtherProcessSamples,IndexedEventCounts:parsed.IndexedEventCounts,ParserExecution:execution);
            var sealedReport=await Artifacts.WriteJsonAsync(operation.Id,report,"traceevent-stack/1",limit:67108864,cancellation:deadline.Token);
            Upsert(sealedReport.Id,"artifact",sealedReport); Catalog.Pin(sealedReport.Id,"sampled_stack_report");
            var index=new StackAnalysisIndex(idReport,capture.Id,sealedReport.Id,evidence.ObservedSamples,evidence.Methods.Count,evidence.SymbolCoverage,quality);
            Catalog.Put(index.Id,"stack_analysis",index); Catalog.Transition(operation.Id,OperationState.Completed,index.Id); return true;
        });
    }
    public async Task<StackAnalysisReport> StackAnalysisAsync(string id)
    {
        var index=Record<StackAnalysisIndex>(id,"stack_analysis"); var artifact=Record<Artifact>(index.ArtifactId,"artifact");
        if(artifact.Length>67108864) throw new LabException("resource_limit","Stack report exceeds its read budget.");
        return Wire.Deserialize<StackAnalysisReport>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact)));
    }
    public async Task<SampledMethodQuery> SampledMethodAsync(string id,string selector)
    {
        if(string.IsNullOrWhiteSpace(selector)||selector.Length>1024) throw new LabException("invalid_request","A bounded exact method identity or name is required.");
        var report=await StackAnalysisAsync(id);
        var matches=report.Evidence.Methods.Where(m=>m.Frame.Id==selector||m.Frame.Name==selector).ToArray();
        var status=matches.Length>1?Availability.Invalid:report.Evidence.ObservedSamples>0?Availability.Partial:Availability.NotCollected;
        long? self=report.Evidence.ObservedSamples==0||matches.Length>1?null:matches.Sum(m=>m.SelfCount);
        long? inclusive=report.Evidence.ObservedSamples==0||matches.Length>1?null:matches.Sum(m=>m.InclusiveCount);
        return new(selector,status,report.Evidence.ObservedSamples,matches,self,inclusive,
            Provenance:new(report.CaptureId,report.TraceArtifactId,report.ParserVersion,report.ProfileHash));
    }
}
