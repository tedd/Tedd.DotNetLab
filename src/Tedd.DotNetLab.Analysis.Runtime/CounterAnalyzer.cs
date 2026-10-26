using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Runtime;

public record CounterWindow(string Name,string? Source,string? Series,string? Kind,string Unit,Availability Status,double? Value,string Aggregation,
    double StartMilliseconds,double EndMilliseconds,double CoveredMilliseconds,int Observations,int ResetIntervals,
    IReadOnlyList<string> Warnings,Provenance? Provenance=null,string Version="counter-window/v1",string Scope="process_contextual",
    string Meaning="Aggregates retained observations over covered intervals; not exact attribution to a scenario or invocation.");

public static class CounterAnalyzer
{
    public static CounterWindow Analyze(IReadOnlyList<CounterPoint> input,string name,double start,double end,
        string? kind=null,string? source=null,string? series=null,EvidenceQuality? quality=null,TimeInterval? traceBounds=null,Provenance? provenance=null)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>256||!double.IsFinite(start)||!double.IsFinite(end)||start<0||end<=start)
            throw new LabException("invalid_interval","Counter query requires a name and finite positive trace interval.");
        var points=input.Where(p=>p.Name==name&&(kind is null||p.Kind==kind)&&(source is null||p.Source==source)&&(series is null||p.Series==series)).OrderBy(p=>p.TimeMilliseconds).ToArray();
        var warnings=new HashSet<string>(); int resets=0; string aggregation="unavailable",unit="unknown"; double covered=0;
        CounterWindow Result(Availability status,double? value=null)=>new(name,source,series,kind,unit,status,value,aggregation,start,end,covered,points.Length,resets,warnings.Order().ToArray(),provenance);
        if(points.Length==0) return Result(Availability.NotCollected);
        var identities=points.Select(p=>new { p.Source,p.Series,p.Kind,p.Unit,p.Scope,p.Observation }).Distinct().ToArray();
        if(identities.Length!=1) { warnings.Add("ambiguous_counter_series_or_semantics"); return Result(Availability.Invalid); }
        var identity=identities[0]; source=identity.Source; series=identity.Series; kind=identity.Kind; unit=identity.Unit;
        if(points.Any(p=>!double.IsFinite(p.Value)||!double.IsFinite(p.TimeMilliseconds)||!double.IsFinite(p.IntervalSeconds)||p.IntervalSeconds<=0)||
            points.GroupBy(p=>p.TimeMilliseconds).Any(g=>g.Count()>1))
        { warnings.Add("invalid_or_duplicate_counter_observations"); return Result(Availability.Invalid); }
        if(kind is not ("gauge" or "cumulative_counter" or "interval_increment" or "rate")) return Result(Availability.NotSupported);
        double resolution=points.Max(p=>p.IntervalSeconds)*1000;
        if(end-start+1e-6<resolution) { warnings.Add("insufficient_temporal_resolution"); return Result(Availability.Partial); }
        if(traceBounds is not null&&(start<traceBounds.Start||end>traceBounds.End)) warnings.Add("outside_retained_trace_bounds");
        if(quality is not null&&(quality.Coverage=="partial"||quality.LossStatus!="none_reported")) warnings.Add("capture_quality_limits_counter_coverage");
        if(kind=="gauge"&&identity.Observation=="instantaneous")
        {
            aggregation="reported_gauge_sample_mean/v1"; warnings.Add("instantaneous_samples_do_not_establish_time_weighted_mean");
            var selected=points.Where(p=>p.TimeMilliseconds>=start&&p.TimeMilliseconds<end).ToArray();
            return Result(Availability.Partial,selected.Length>0?selected.Average(p=>p.Value):null);
        }
        var intervals=new List<TimeInterval>(); double sum=0;
        if(kind=="cumulative_counter")
        {
            aggregation="observed_nonnegative_differences/v1";
            if(points.Any(p=>p.Value<0)) { warnings.Add("negative_monotonic_total"); return Result(Availability.Invalid); }
            for(int i=1;i<points.Length;i++)
            {
                var before=points[i-1]; var after=points[i];
                if(before.TimeMilliseconds<start-1e-6||after.TimeMilliseconds>end+1e-6) continue;
                if(after.Value<before.Value) { resets++; warnings.Add("counter_reset_interval_excluded"); continue; }
                sum+=after.Value-before.Value; intervals.Add(new(before.TimeMilliseconds,after.TimeMilliseconds,"counter_observation"));
            }
        }
        else
        {
            aggregation=kind switch { "gauge"=>"duration_weighted_reported_interval_means/v1","rate"=>"duration_weighted_interval_rates/v1",_=>"sum_complete_interval_increments/v1" };
            if(kind=="gauge"&&identity.Observation!="interval_mean"||kind=="rate"&&identity.Observation!="interval_rate_per_second")
            { warnings.Add("unsupported_observation_semantics"); return Result(Availability.NotSupported); }
            if(kind=="interval_increment"&&points.Any(p=>p.Value<0)) { warnings.Add("negative_interval_increment"); return Result(Availability.Invalid); }
            foreach(var point in points)
            {
                double left=point.TimeMilliseconds-point.IntervalSeconds*1000,right=point.TimeMilliseconds;
                if(left<start-1e-6||right>end+1e-6) continue; // Never prorate a coarse observation into an unobserved subinterval.
                if(intervals.Count>0&&left<intervals[^1].End-1e-6) { warnings.Add("overlapping_counter_intervals"); return Result(Availability.Invalid); }
                intervals.Add(new(left,right,"counter_observation")); sum+=kind=="interval_increment"?point.Value:point.Value*point.IntervalSeconds;
            }
        }
        covered=Intervals.UnionOverlap(intervals,start,end);
        if(covered<=0) { warnings.Add("no_complete_observation_interval"); return Result(resets>0?Availability.Invalid:Availability.Partial); }
        if(covered<end-start-1e-6) warnings.Add("incomplete_counter_window_coverage");
        double aggregate=kind is "gauge" or "rate"?sum/(covered/1000):sum;
        if(!double.IsFinite(aggregate)) { warnings.Add("nonfinite_counter_aggregate"); return Result(Availability.Invalid); }
        return Result(warnings.Count==0?Availability.Available:Availability.Partial,aggregate);
    }
}
