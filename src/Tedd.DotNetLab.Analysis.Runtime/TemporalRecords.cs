using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Runtime;

public record GcObservation(int RuntimeInstance,int Number,int Generation,string Reason,string Type,double Time,bool Start,string? RecordIdentity=null);
public record SuspensionObservation(int RuntimeInstance,double Time,bool Start,string Reason="unknown",string? RecordIdentity=null);
public record SuspensionInterval(int RuntimeInstance,double? StartMilliseconds,double? EndMilliseconds,string Reason,string Completeness)
{
    public bool IsGc=>Reason is "SuspendForGC" or "SuspendForGCPrep";
}
public record TemporalResult(IReadOnlyList<GcCycle> GcCycles,IReadOnlyList<SuspensionInterval> Suspensions,IReadOnlyList<string> Warnings);
public record RunWindow(string Id,string AnalysisId,string CaptureId,string RunId,string ExecutionId,string BlockId,
    Availability Availability,string BoundarySource,long StartTicks,long EndTicks,long Frequency,double? StartMilliseconds,
    double? EndMilliseconds,ClockMapping? ClockMapping,MetricResult GcSuspensionOverlap,int OverlappingGcCycles,
    IReadOnlyList<string> Warnings,string Algorithm="target-clock-window/v1",double ResidualToleranceMilliseconds=1,
    string Meaning="Half-open target-side execution interval; GC overlap is process-level correlation, not method attribution.",
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] ClockFitFailure? ClockFitFailure=null);

public static class TemporalReconstruction
{
    public static TemporalResult Reconstruct(IReadOnlyList<GcObservation> gc,IReadOnlyList<SuspensionObservation> pauses)
    {
        if(gc.Count+pauses.Count>1000000||gc.Any(value=>!double.IsFinite(value.Time)||value.Time<0)||pauses.Any(value=>!double.IsFinite(value.Time)||value.Time<0))
            throw new LabException("invalid_interval","Temporal input must be bounded, finite, and nonnegative.");
        var warnings=new HashSet<string>();
        IEnumerable<T> Unique<T>(IReadOnlyList<T> rows,Func<T,string?> identity)
        {
            var seen=new Dictionary<string,T>();
            foreach(var row in rows)
            {
                string? key=identity(row);
                if(key is null) { yield return row; continue; }
                if(seen.TryGetValue(key,out var previous))
                {
                    if(EqualityComparer<T>.Default.Equals(previous,row)) { warnings.Add("known_duplicate_record"); continue; }
                    warnings.Add("conflicting_record_identity");
                }
                else seen[key]=row;
                yield return row;
            }
        }
        var cycles=new List<GcCycle>(); var active=new Dictionary<(int,int),int>();
        foreach(var value in Unique(gc,row=>row.RecordIdentity).OrderBy(row=>row.Time).ThenByDescending(row=>row.Start))
        {
            var key=(value.RuntimeInstance,value.Number);
            if(value.Start)
            {
                if(active.ContainsKey(key)) warnings.Add("duplicate_gc_start");
                active[key]=cycles.Count;
                cycles.Add(new(value.RuntimeInstance,value.Number,value.Generation,value.Reason,value.Type,value.Time,null));
            }
            else if(active.Remove(key,out int index)) cycles[index]=cycles[index] with { EndMilliseconds=value.Time };
            else warnings.Add("unmatched_gc_end");
        }
        if(active.Count>0) warnings.Add("unmatched_gc_start");
        var suspensions=new List<SuspensionInterval>(); var suspended=new Dictionary<int,SuspensionObservation>();
        foreach(var value in Unique(pauses,row=>row.RecordIdentity).OrderBy(row=>row.Time).ThenByDescending(row=>row.Start))
        {
            if(value.Start)
            {
                if(suspended.TryGetValue(value.RuntimeInstance,out var previous))
                {
                    suspensions.Add(new(previous.RuntimeInstance,previous.Time,null,previous.Reason,"overlapping_start_missing_end"));
                    warnings.Add("overlapping_runtime_suspension");
                }
                suspended[value.RuntimeInstance]=value;
            }
            else if(suspended.Remove(value.RuntimeInstance,out var start))
                suspensions.Add(new(value.RuntimeInstance,start.Time,value.Time,start.Reason,"complete"));
            else { suspensions.Add(new(value.RuntimeInstance,null,value.Time,"unknown","missing_start")); warnings.Add("unmatched_suspension_end"); }
        }
        foreach(var start in suspended.Values) { suspensions.Add(new(start.RuntimeInstance,start.Time,null,start.Reason,"missing_end")); warnings.Add("unmatched_suspension_start"); }
        return new(cycles,suspensions.OrderBy(value=>value.StartMilliseconds??value.EndMilliseconds).ToArray(),warnings.Order().ToArray());
    }
}
