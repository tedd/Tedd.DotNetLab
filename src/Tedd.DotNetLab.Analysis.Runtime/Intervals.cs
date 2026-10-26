using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Runtime;

public record TimeInterval(double Start, double End, string Reason = "gc");
public record ClockPoint(long TargetTicks, double TraceMilliseconds);
public record ClockFitFailure(int PairCount,string Failure,double? ToleranceMilliseconds,
    double? EstimatedMillisecondsPerTick=null,double? MaximumResidualMilliseconds=null,ClockPoint? WorstObservedPair=null,
    string Algorithm="affine-clock-fit-diagnostics/v1");
public record ClockMapping(long AnchorTicks, double AnchorMilliseconds, double MillisecondsPerTick, double MaximumResidualMilliseconds)
{
    public double Map(long ticks) => AnchorMilliseconds + (ticks - AnchorTicks) * MillisecondsPerTick;
}
public static class Intervals
{
    public static double UnionOverlap(IEnumerable<TimeInterval> input, double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || end < start)
            throw new LabException("invalid_interval", "Invalid half-open interval.");
        var values=input.ToArray();
        if(values.Any(value=>!double.IsFinite(value.Start)||!double.IsFinite(value.End)||value.End<value.Start))
            throw new LabException("invalid_interval","Invalid retained interval.");
        var intervals = values.Select(x => new TimeInterval(Math.Max(start, x.Start), Math.Min(end, x.End), x.Reason))
            .Where(x => x.End > x.Start).OrderBy(x => x.Start).ToArray();
        double total = 0, left = 0, right = 0;
        bool first = true;
        foreach (var interval in intervals)
        {
            if (first) { left = interval.Start; right = interval.End; first = false; }
            else if (interval.Start <= right) right = Math.Max(right, interval.End);
            else { total += right - left; left = interval.Start; right = interval.End; }
        }
        return first ? 0 : total + right - left;
    }
    public static ClockMapping Fit(IReadOnlyList<ClockPoint> points, double toleranceMilliseconds)=>Fit(points,toleranceMilliseconds,out _);
    public static ClockMapping Fit(IReadOnlyList<ClockPoint> points,double toleranceMilliseconds,out ClockFitFailure? failure)
    {
        failure=null;
        if(!double.IsFinite(toleranceMilliseconds)||toleranceMilliseconds<=0)
        {
            failure=new(points.Count,"invalid_tolerance",double.IsFinite(toleranceMilliseconds)?toleranceMilliseconds:null);
            throw new LabException("clock_mapping_invalid", "Clock tolerance must be finite and positive.");
        }
        if(points.Count<2||points.Any(p=>p.TargetTicks<0||!double.IsFinite(p.TraceMilliseconds)))
        {
            failure=new(points.Count,points.Count<2?"insufficient_pairs":"invalid_pair",toleranceMilliseconds);
            throw new LabException("clock_mapping_invalid", "At least two finite clock pairs are required.");
        }
        long anchor = points[0].TargetTicks;
        double traceAnchor = points[0].TraceMilliseconds;
        double mx = points.Average(p => (double)(p.TargetTicks - anchor));
        double my = points.Average(p => p.TraceMilliseconds - traceAnchor);
        double sumxx = points.Sum(p => Math.Pow((p.TargetTicks - anchor) - mx, 2));
        if (sumxx <= 0)
        {
            failure=new(points.Count,"no_tick_separation",toleranceMilliseconds);
            throw new LabException("clock_mapping_invalid", "Clock pairs have no target time separation.");
        }
        double slope = points.Sum(p => ((p.TargetTicks - anchor) - mx) * (p.TraceMilliseconds - traceAnchor - my)) / sumxx;
        double intercept = traceAnchor + my - slope * mx;
        double residual=double.NegativeInfinity; ClockPoint? worst=null;
        foreach(var point in points)
        {
            double current=Math.Abs(point.TraceMilliseconds-intercept-(point.TargetTicks-anchor)*slope);
            if(!double.IsFinite(current)) { residual=double.NaN; worst=null; break; }
            if(current>residual) { residual=current; worst=point; }
        }
        if (!double.IsFinite(slope)||!double.IsFinite(intercept)||!double.IsFinite(residual)||slope <= 0 || residual > toleranceMilliseconds)
        {
            string reason=!double.IsFinite(slope)||!double.IsFinite(intercept)||!double.IsFinite(residual)?"nonfinite_fit":
                slope<=0?"nonpositive_slope":"excessive_residual";
            failure=new(points.Count,reason,toleranceMilliseconds,double.IsFinite(slope)?slope:null,double.IsFinite(residual)?residual:null,worst);
            throw new LabException("clock_mapping_invalid", "Clock correlation residual or slope is invalid.");
        }
        return new(anchor, intercept, slope, residual);
    }
}
