using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Runtime;

public record SampleFrame(string Id,string? Name,string? Module,string Resolution,ulong Address,int? MethodToken=null);
public record SampledMethod(SampleFrame Frame,long SelfCount,long InclusiveCount,double SelfShare,double InclusiveShare);
public record StackEvidence(long ObservedSamples,long MissingStacks,long TruncatedStacks,long FrameOccurrences,long ResolvedFrameOccurrences,
    double? SymbolCoverage,IReadOnlyList<SampledMethod> Methods,IReadOnlyList<string> Warnings,
    string Convention="leaf_self_and_once_per_sample_inclusive_presence/v1",
    string Meaning="Observed sample counts/shares; not invocation counts, per-call latency, or measured on-CPU duration. Inclusive shares can sum above one.");

public sealed class StackAccumulator(int maximumMethods=10000,int maximumDepth=256)
{
    private sealed class Counts(SampleFrame frame)
    {
        public SampleFrame Frame { get; }=frame;
        public long Self,Inclusive;
    }
    private readonly Dictionary<string,Counts> methods=new(StringComparer.Ordinal);
    private readonly HashSet<string> warnings=new();
    private long samples,missing,truncated,frames,resolved;
    public long ObservedSamples=>samples;
    public void Add(IReadOnlyList<SampleFrame> stack,bool truncatedInput=false)
    {
        if(maximumMethods is <1 or >100000||maximumDepth is <1 or >1024)
            throw new LabException("resource_limit","Invalid stack attribution limits.");
        samples++;
        if(stack.Count==0) { missing++; warnings.Add("sample_without_retained_stack"); return; }
        if(truncatedInput||stack.Count>maximumDepth) { truncated++; warnings.Add("stack_depth_cap"); }
        var seen=new HashSet<string>(StringComparer.Ordinal);
        for(int i=0;i<Math.Min(stack.Count,maximumDepth);i++)
        {
            var frame=stack[i];
            if(string.IsNullOrEmpty(frame.Id)||frame.Resolution is not ("managed_rundown" or "unresolved"))
                throw new LabException("invalid_stack","Frame identity or resolution convention is invalid.");
            if(frame.Resolution=="managed_rundown"&&string.IsNullOrWhiteSpace(frame.Name))
                throw new LabException("invalid_stack","Resolved frames require retained metadata names.");
            if(frame.Resolution=="unresolved"&&frame.Name is not null)
                throw new LabException("invalid_stack","Unresolved frames cannot contain a guessed symbol name.");
            frames++; if(frame.Resolution=="managed_rundown") resolved++; else warnings.Add("unresolved_stack_frames");
            if(!methods.TryGetValue(frame.Id,out var count))
            {
                if(methods.Count==maximumMethods) throw new LabException("resource_limit","Sampled method cardinality exceeds policy.");
                count=new(frame); methods.Add(frame.Id,count);
            }
            else if(count.Frame.Name!=frame.Name||count.Frame.Module!=frame.Module||count.Frame.Resolution!=frame.Resolution||count.Frame.MethodToken!=frame.MethodToken)
                throw new LabException("invalid_stack","One frame identity has conflicting symbol metadata.");
            if(i==0) count.Self++;
            if(seen.Add(frame.Id)) count.Inclusive++;
        }
    }
    public StackEvidence Snapshot()=>new(samples,missing,truncated,frames,resolved,frames>0?(double)resolved/frames:null,
        methods.Values.Select(m=>new SampledMethod(m.Frame,m.Self,m.Inclusive,samples>0?(double)m.Self/samples:0,samples>0?(double)m.Inclusive/samples:0))
            .OrderByDescending(m=>m.InclusiveCount).ThenByDescending(m=>m.SelfCount).ThenBy(m=>m.Frame.Id,StringComparer.Ordinal).ToArray(),warnings.Order().ToArray());
}
