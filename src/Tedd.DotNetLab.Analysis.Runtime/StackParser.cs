using Tedd.DotNetLab.Contracts;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Etlx;

namespace Tedd.DotNetLab.Analysis.Runtime;

public record StackParserPolicy(int MaximumEvents=250000,int MaximumSamples=100000,int MaximumDepth=256,int MaximumMethods=10000,
    long MaximumRawBytes=134217728,long MaximumTemporaryBytes=536870912,int MaximumDurationSeconds=300,
    double? StartMilliseconds=null,double? EndMilliseconds=null)
{
    public void Validate()
    {
        if(MaximumEvents is <1 or >1000000||MaximumSamples is <1 or >1000000||MaximumDepth is <1 or >1024||MaximumMethods is <1 or >100000||
            MaximumRawBytes is <1 or >536870912||MaximumTemporaryBytes is <1048576 or >2147483648||MaximumDurationSeconds is <1 or >600||
            StartMilliseconds.HasValue!=EndMilliseconds.HasValue||StartMilliseconds is double start&&
            (!double.IsFinite(start)||start<0||!double.IsFinite(EndMilliseconds!.Value)||EndMilliseconds<=start))
            throw new LabException("invalid_plan","Invalid bounded stack-parser policy.");
    }
}
public record StackParserResult(string CaptureId,string TraceArtifactId,string ProfileHash,string ParserVersion,
    StackParserPolicy Policy,StackEvidence Evidence,EvidenceQuality Quality,long OtherProcessSamples,IReadOnlyDictionary<string,long> IndexedEventCounts);

public static class StackParser
{
    public const string Version="traceevent-stack/1";
    public static StackParserResult Analyze(string raw,string scratch,Capture capture,Artifact artifact,StackParserPolicy policy,CancellationToken cancellation)
    {
        policy.Validate(); Directory.CreateDirectory(scratch);
        var accumulator=new StackAccumulator(policy.MaximumMethods,policy.MaximumDepth); var warnings=new HashSet<string>(); long other=0;
        var indexedCounts=new Dictionary<string,long>(); string etlx=Path.Combine(scratch,"trace.etlx");
        var options=new TraceLogOptions { MaxEventCount=policy.MaximumEvents,ShouldResolveSymbols=_=>false,LocalSymbolsOnly=true,
            AlwaysResolveSymbols=false,ConversionLogName=Path.Combine(scratch,"conversion.log"),ContinueOnError=false,
            OnLostEvents=(truncated,lost,total)=> { if(truncated) warnings.Add("stack_conversion_event_cap"); if(lost>0) warnings.Add("stack_conversion_reported_loss"); } };
        using var conversionLog=options.ConversionLog;
        void CheckTemporary()
        {
            cancellation.ThrowIfCancellationRequested();
            if(Directory.EnumerateFiles(scratch).Sum(file=>new FileInfo(file).Length)>policy.MaximumTemporaryBytes)
                throw new LabException("resource_limit","Stack conversion exceeds its temporary byte budget.");
        }
        using(var source=new EventPipeEventSource(raw))
        {
            int processed=0; source.AllEvents+=_=> { if(++processed%1024==0) CheckTemporary(); else cancellation.ThrowIfCancellationRequested(); };
            TraceLog.CreateFromEventTraceLogFile(source,etlx,options);
        }
        CheckTemporary(); using var log=new TraceLog(etlx);
        foreach(var data in log.Events)
        {
            cancellation.ThrowIfCancellationRequested();
            string eventKey=data.ProviderName+"/"+data.EventName;
            if(indexedCounts.Count<64||indexedCounts.ContainsKey(eventKey)) indexedCounts[eventKey]=indexedCounts.GetValueOrDefault(eventKey)+1;
            if(data.ProviderName!="Microsoft-DotNETCore-SampleProfiler"||data.EventName is not ("ThreadSample" or "Thread/Sample")) continue;
            if(data.ProcessID!=capture.Target.ProcessId) { other++; continue; }
            if(policy.StartMilliseconds.HasValue&&(data.TimeStampRelativeMSec<policy.StartMilliseconds||data.TimeStampRelativeMSec>=policy.EndMilliseconds)) continue;
            if(accumulator.ObservedSamples>=policy.MaximumSamples) { warnings.Add("stack_sample_cap"); break; }
            var current=data.CallStack(); var frames=new List<SampleFrame>(); int depth=0;
            for(;current is not null&&depth++<policy.MaximumDepth;current=current.Caller)
            {
                var code=current.CodeAddress; var method=code.Method;
                bool named=method is not null&&method.MethodToken!=0&&!string.IsNullOrWhiteSpace(method.FullMethodName);
                string? module=code.ModuleFile?.Name;
                string id="sampled-method-"+Wire.Hash(new { artifact.Hash,process=capture.Target.ProcessId,
                    module_index=code.ModuleFile is null?-1:(int)code.ModuleFile.ModuleFileIndex,
                    token=named?method!.MethodToken:0,name=named?method!.FullMethodName:null,address=named?0UL:code.Address });
                frames.Add(new(id,named?method!.FullMethodName:null,module,named?"managed_rundown":"unresolved",code.Address,named?method!.MethodToken:null));
            }
            accumulator.Add(frames,current is not null);
        }
        var evidence=accumulator.Snapshot(); foreach(string warning in evidence.Warnings) warnings.Add(warning);
        if(evidence.ObservedSamples==0) warnings.Add("no_observed_stack_samples");
        if(capture.State!=CaptureState.Sealed) warnings.Add("capture_incomplete");
        if(other>0) warnings.Add("other_process_samples_excluded");
        if(capture.Quality?.LossStatus is null or "unknown") warnings.Add("stack_loss_status_unknown");
        var quality=new EvidenceQuality(warnings.Count>0?"partial":"sampled_capture",capture.Quality?.LossStatus??"unknown",Version,warnings.Order().ToArray(),evidence.SymbolCoverage);
        return new(capture.Id,artifact.Id,capture.Profile.Hash,Version,policy,evidence,quality,other,indexedCounts);
    }
}
