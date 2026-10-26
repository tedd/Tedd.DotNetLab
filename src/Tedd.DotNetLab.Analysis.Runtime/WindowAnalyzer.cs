using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Analysis.Runtime;

public static class WindowAnalyzer
{
    public const string Version="target-clock-window/v1";
    public static RunWindow Analyze(TraceSummary summary,Capture capture,Measurement measurement,double toleranceMilliseconds=1)
    {
        if(!double.IsFinite(toleranceMilliseconds)||toleranceMilliseconds is <=0 or >10)
            throw new LabException("invalid_request","Clock residual tolerance must be finite and within (0,10] milliseconds.");
        Policies.Validate(measurement);
        if(summary.CaptureId!=capture.Id||summary.ArtifactId!=capture.ArtifactId||summary.ProfileHash!=capture.Profile.Hash)
            throw new LabException("not_comparable","Window analysis and capture identities differ.");
        string id="window-"+Wire.Hash(new { summary.Id,summary.ParserVersion,measurement,toleranceMilliseconds,algorithm=Version });
        ClockFitFailure? clockFailure=null;
        var warnings=new HashSet<string>();
        var provenance=new Provenance(capture.Id,summary.ArtifactId,summary.ParserVersion,summary.ProfileHash);
        RunWindow Result(Availability status,string reason,string boundary="unavailable",ClockMapping? mapping=null,double? start=null,double? end=null,
            double? overlap=null,int cycles=0)=>new(id,summary.Id,capture.Id,measurement.RunId,measurement.ExecutionId,measurement.BlockId,status,boundary,
                measurement.StartTicks,measurement.EndTicks,measurement.Frequency,start,end,mapping,
                new("gc.suspension_overlap_ns",status,overlap,"ns","process_overlap_with_run",reason,summary.Quality,provenance),cycles,
                warnings.Order().ToArray(),Version,toleranceMilliseconds,ClockFitFailure:clockFailure);
        if(!capture.Profile.DataClasses.Contains("markers")) return Result(Availability.NotCollected,"Target-side harness markers were not enabled.");
        var clock=summary.Markers.Where(marker=>marker.Phase is "clock_before" or "clock_after").ToArray();
        if(clock.Length<2) return Result(Availability.NotCollected,"No validated target-to-trace clock mapping exists; capture time is not a scenario boundary.");
        if(clock.Any(marker=>marker.Frequency!=measurement.Frequency||marker.Ticks<0||!double.IsFinite(marker.TraceMilliseconds)))
            return Result(Availability.Invalid,"Clock marker frequency or timestamp is invalid.");
        ClockMapping mapping;
        try { mapping=Intervals.Fit(clock.Select(marker=>new ClockPoint(marker.Ticks,marker.TraceMilliseconds)).ToArray(),toleranceMilliseconds,out clockFailure); }
        catch(LabException error) when(error.Code=="clock_mapping_invalid")
        {
            // Diagnostics create a distinct derived identity; historical windows are immutable.
            id="window-"+Wire.Hash(new { legacy_window_id=id,clock_fit_failure=clockFailure });
            return Result(Availability.Invalid,error.Code);
        }
        double expected=1000d/measurement.Frequency;
        if(Math.Abs(mapping.MillisecondsPerTick/expected-1)>.005)
            return Result(Availability.Invalid,"Clock slope differs from the target frequency by more than the qualified 5000 ppm policy.",mapping:mapping);
        if(measurement.StartTicks<clock.Min(marker=>marker.Ticks)||measurement.EndTicks>clock.Max(marker=>marker.Ticks))
            return Result(Availability.Partial,"Execution lies outside retained clock calibration; extrapolation is not qualified.",mapping:mapping);
        var semantic=summary.Markers.Where(marker=>marker.RunId==measurement.RunId&&marker.Phase is "measurement_start" or "measurement_end").ToArray();
        if(semantic.Any(marker=>marker.ExecutionId!=measurement.ExecutionId||marker.BlockId!=measurement.BlockId||marker.Frequency!=measurement.Frequency)||
            semantic.Count(marker=>marker.Phase=="measurement_start")>1||semantic.Count(marker=>marker.Phase=="measurement_end")>1)
            return Result(Availability.Invalid,"Measurement marker identity is ambiguous or inconsistent.",mapping:mapping);
        var before=semantic.SingleOrDefault(marker=>marker.Phase=="measurement_start"); var after=semantic.SingleOrDefault(marker=>marker.Phase=="measurement_end");
        if(before is not null&&before.Ticks!=measurement.StartTicks||after is not null&&after.Ticks!=measurement.EndTicks)
            return Result(Availability.Invalid,"Persisted measurement ticks differ from retained semantic markers.",mapping:mapping);
        string boundary=before is not null&&after is not null?"target_measurement_markers":"persisted_measurement_clock_recovery";
        if(boundary=="persisted_measurement_clock_recovery") warnings.Add("missing_measurement_marker_recovered_from_sealed_ticks");
        double start=mapping.Map(measurement.StartTicks),end=mapping.Map(measurement.EndTicks);
        if(!double.IsFinite(start)||!double.IsFinite(end)||end<=start) return Result(Availability.Invalid,"Mapped interval is invalid.",boundary,mapping);
        if(summary.TraceBounds is null||start<summary.TraceBounds.Start||end>summary.TraceBounds.End)
            return Result(Availability.Partial,"Execution extends beyond the observed retained trace interval.",boundary,mapping,start,end);
        if(!capture.Profile.DataClasses.Contains("suspension")) return Result(Availability.NotCollected,"Suspension provider class was not enabled.",boundary,mapping,start,end);
        var evidence=summary.SuspensionEvidence??summary.Suspensions.Select(interval=>new SuspensionInterval(-1,interval.Start,interval.End,interval.Reason,"complete")).ToArray();
        if(!evidence.Any(interval=>interval.IsGc&&interval.Completeness=="complete"))
            return Result(Availability.NotCollected,"No complete GC suspension establishes provider coverage; silent evidence is not zero.",boundary,mapping,start,end);
        var complete=evidence.Where(interval=>interval.IsGc&&interval.StartMilliseconds.HasValue&&interval.EndMilliseconds.HasValue)
            .Select(interval=>new TimeInterval(interval.StartMilliseconds!.Value,interval.EndMilliseconds!.Value,interval.Reason)).ToArray();
        double overlap;
        try { overlap=Intervals.UnionOverlap(complete,start,end)*1e6; }
        catch(LabException) { return Result(Availability.Invalid,"Retained GC suspension interval is malformed.",boundary,mapping,start,end); }
        bool incomplete=evidence.Any(interval=>(interval.IsGc||interval.Reason=="unknown")&&interval.Completeness!="complete"&&
            (interval.StartMilliseconds??double.NegativeInfinity)<end&&(interval.EndMilliseconds??double.PositiveInfinity)>start);
        if(incomplete) warnings.Add("incomplete_suspension_may_overlap_run");
        if(summary.Quality.LossStatus!="none_reported") warnings.Add("trace_loss_or_loss_unknown");
        if(capture.State!=CaptureState.Sealed||summary.Quality.Coverage=="partial") warnings.Add("capture_or_parser_partial");
        if(mapping.MaximumResidualMilliseconds*2>=end-start) warnings.Add("clock_residual_at_execution_resolution");
        int cycles=summary.GcCycles.Count(cycle=>cycle.StartMilliseconds<end&&(cycle.EndMilliseconds??double.PositiveInfinity)>start);
        bool partial=incomplete||summary.Quality.LossStatus!="none_reported"||capture.State!=CaptureState.Sealed||summary.Quality.Coverage=="partial"||
            mapping.MaximumResidualMilliseconds*2>=end-start;
        return Result(partial?Availability.Partial:Availability.Available,
            "Union of retained GC suspension-start to restart-end intervals intersecting [target_start,target_end); affine fit residual is reported, not a guarantee against unobserved scheduling delay.",
            boundary,mapping,start,end,overlap,cycles);
    }
}
