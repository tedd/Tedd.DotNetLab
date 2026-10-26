using Tedd.DotNetLab.Contracts;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Parsers;
using System.Text.Json.Serialization;

namespace Tedd.DotNetLab.Analysis.Runtime;

public record GcCycle(int RuntimeInstance, int Number, int Generation, string Reason, string Type,
    double StartMilliseconds, double? EndMilliseconds);
public record AllocationSample(double TimeMilliseconds, long Amount, string TypeName, string Semantics = "sampled_trigger_type_not_exact_ledger");
public record CounterPoint(string Name, string Kind, string Unit, double TimeMilliseconds, double IntervalSeconds,
    double Value, string Source = "System.Runtime.EventCounters", string Scope = "process",
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Series=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? Tags=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? InstrumentType=null,
    string Observation="interval_mean");
public record HarnessMarker(string RunId, string BlockId, string ExecutionId, long Ticks, long Frequency,
    double TraceMilliseconds, string Phase);
public record JitMethodEvent(double TimeMilliseconds, string MethodNamespace, string MethodName, string MethodSignature,
    bool IsJitted, string OptimizationTier);
public record JitInliningEvent(double TimeMilliseconds, string Inliner, string Inlinee, bool Succeeded,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? FailReason = null,
    bool FailAlways = false);
public record JitDiagnosticsSummary(long MethodsCompiledCount, long InliningSucceededCount, long InliningFailedCount);
public record ContentionEvent(int ThreadId, string Flags, double StartMilliseconds, double DurationMilliseconds);
public record ContentionDiagnosticsSummary(long TotalCount, long ManagedCount, long NativeCount,
    double TotalDurationMilliseconds, double MaxDurationMilliseconds);
public record TraceSummary(string Id, string CaptureId, string ArtifactId, string ProfileHash, string ParserVersion,
    EvidenceQuality Quality, IReadOnlyList<GcCycle> GcCycles, IReadOnlyList<TimeInterval> Suspensions,
    IReadOnlyList<AllocationSample> Allocations, IReadOnlyList<CounterPoint> Counters,
    IReadOnlyList<HarnessMarker> Markers, long ManagedSampleCount, IReadOnlyDictionary<string,long> EventCounts,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SuspensionInterval>? SuspensionEvidence=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] TimeInterval? TraceBounds=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] TraceParserExecution? ParserExecution=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] JitDiagnosticsSummary? JitSummary=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<JitMethodEvent>? JitMethods=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<JitInliningEvent>? JitInlinings=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] ContentionDiagnosticsSummary? ContentionSummary=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<ContentionEvent>? Contentions=null);
public sealed class TraceAnalyzer
{
    public const string Version = "traceevent-runtime/3";
    private const int MaximumRetainedJitMethods = 4000;
    private const int MaximumRetainedInlineFailures = 3000;
    private const int MaximumRetainedInlineSuccesses = 2000;
    private const int MaximumRetainedContentions = 4000;

    private static string FormatMethod(string? ns, string? name) =>
        string.IsNullOrEmpty(ns) ? (name ?? "unresolved") : ns + "." + (name ?? "unresolved");

    private static string ReadOptimizationTier(TraceEvent data)
    {
        try
        {
            if (data.PayloadByName("OptimizationTier") is { } tier)
                return tier.ToString() ?? "Unknown";
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return "Unknown";
    }

    public TraceSummary Analyze(string path, Capture capture, int maximumEvents = 1000000,Action<TraceEvent,Exception>? counterParseFailure=null)
    {
        var gc=new List<GcObservation>(); var pauses=new List<SuspensionObservation>();
        double first=double.PositiveInfinity,last=double.NegativeInfinity;
        var allocations = new List<AllocationSample>();
        var counters = new List<CounterPoint>();
        var modern=new ModernMetricsAdapter();
        var markers = new List<HarnessMarker>();
        var jitMethods = new List<JitMethodEvent>();
        var jitInlinings = new List<JitInliningEvent>();
        var contentions = new List<ContentionEvent>();
        var activeContentions = new Dictionary<int, (double StartMs, string Flags)>();
        long methodsCompiledCount = 0, inlineSucceededCount = 0, inlineFailedCount = 0;
        int retainedInlineSuccesses = 0, retainedInlineFailures = 0;
        long contentionTotal = 0, contentionManaged = 0, contentionNative = 0;
        double contentionTotalMs = 0, contentionMaxMs = 0;
        var counts = new Dictionary<string,long>();
        var warnings = new HashSet<string>();
        long samples = 0;
        int events = 0;
        string loss = "unknown";
        try
        {
            using var source = new EventPipeEventSource(path);
            source.Clr.GCStart += data =>
            {
                if (data.Version > 2) { warnings.Add("unsupported_gc_start_version"); return; }
                gc.Add(new(data.ClrInstanceID,data.Count,data.Depth,data.Reason.ToString(),data.Type.ToString(),data.TimeStampRelativeMSec,true));
            };
            source.Clr.GCStop += data =>
            {
                gc.Add(new(data.ClrInstanceID,data.Count,data.Depth,"unknown","unknown",data.TimeStampRelativeMSec,false));
            };
            source.Clr.GCSuspendEEStart += data =>
            {
                pauses.Add(new(data.ClrInstanceID,data.TimeStampRelativeMSec,true,data.Reason.ToString()));
            };
            source.Clr.GCRestartEEStop += data =>
            {
                pauses.Add(new(data.ClrInstanceID,data.TimeStampRelativeMSec,false));
            };
            source.Clr.GCAllocationTick += data =>
            {
                if (data.Version is < 2 or > 4) { warnings.Add("unsupported_allocation_version"); return; }
                allocations.Add(new(data.TimeStampRelativeMSec, data.AllocationAmount64, data.TypeName ?? "unresolved"));
            };
            source.Clr.MethodLoadVerbose += data =>
            {
                methodsCompiledCount++;
                if (jitMethods.Count < MaximumRetainedJitMethods)
                {
                    jitMethods.Add(new(data.TimeStampRelativeMSec, data.MethodNamespace ?? "", data.MethodName ?? "unresolved",
                        data.MethodSignature ?? "", data.IsJitted, ReadOptimizationTier(data)));
                }
            };
            source.Clr.MethodInliningSucceeded += data =>
            {
                inlineSucceededCount++;
                if (retainedInlineSuccesses < MaximumRetainedInlineSuccesses)
                {
                    retainedInlineSuccesses++;
                    jitInlinings.Add(new(data.TimeStampRelativeMSec,
                        FormatMethod(data.InlinerNamespace, data.InlinerName),
                        FormatMethod(data.InlineeNamespace, data.InlineeName),
                        true));
                }
            };
            source.Clr.MethodInliningFailed += data =>
            {
                inlineFailedCount++;
                if (retainedInlineFailures < MaximumRetainedInlineFailures)
                {
                    retainedInlineFailures++;
                    jitInlinings.Add(new(data.TimeStampRelativeMSec,
                        FormatMethod(data.InlinerNamespace, data.InlinerName),
                        FormatMethod(data.InlineeNamespace, data.InlineeName),
                        false, data.FailReason, data.FailAlways));
                }
            };
            source.Clr.MethodInliningFailedAnsi += data =>
            {
                inlineFailedCount++;
                if (retainedInlineFailures < MaximumRetainedInlineFailures)
                {
                    retainedInlineFailures++;
                    jitInlinings.Add(new(data.TimeStampRelativeMSec,
                        FormatMethod(data.InlinerNamespace, data.InlinerName),
                        FormatMethod(data.InlineeNamespace, data.InlineeName),
                        false, data.FailReason, data.FailAlways));
                }
            };
            source.Clr.ContentionStart += data =>
            {
                activeContentions[data.ThreadID] = (data.TimeStampRelativeMSec, data.ContentionFlags.ToString());
            };
            source.Clr.ContentionStop += data =>
            {
                string flags = data.ContentionFlags.ToString();
                double startMs = data.TimeStampRelativeMSec;
                double durationMs = data.Version >= 1 && data.DurationNs > 0 ? data.DurationNs / 1e6 : 0;
                if (activeContentions.Remove(data.ThreadID, out var started))
                {
                    startMs = started.StartMs;
                    if (durationMs <= 0 && data.TimeStampRelativeMSec >= started.StartMs)
                        durationMs = data.TimeStampRelativeMSec - started.StartMs;
                    if (string.IsNullOrEmpty(flags)) flags = started.Flags;
                }
                if (!double.IsFinite(durationMs) || durationMs < 0) durationMs = 0;
                contentionTotal++;
                if (flags.Contains("Native", StringComparison.OrdinalIgnoreCase)) contentionNative++;
                else contentionManaged++;
                contentionTotalMs += durationMs;
                if (durationMs > contentionMaxMs) contentionMaxMs = durationMs;
                if (contentions.Count < MaximumRetainedContentions)
                    contentions.Add(new(data.ThreadID, flags, startMs, durationMs));
            };
            source.Dynamic.All += data =>
            {
                if (data.ProviderName == "Microsoft-DotNETCore-SampleProfiler") samples++;
                if (data.ProviderName == "Tedd.DotNetLab-Harness" && data.EventName == "Boundary")
                {
                    try
                    {
                        markers.Add(new((string)data.PayloadByName("runId"), (string)data.PayloadByName("blockId"),
                            (string)data.PayloadByName("executionId"), Convert.ToInt64(data.PayloadByName("ticks")),
                            Convert.ToInt64(data.PayloadByName("frequency")), data.TimeStampRelativeMSec,
                            (string)data.PayloadByName("phase")));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { warnings.Add("unsupported_marker_schema"); }
                }
                if (data.ProviderName == "System.Runtime" && data.EventName == "EventCounters")
                {
                    try
                    {
                        var payload = (IDictionary<string,object>)data.PayloadValue(0);
                        if (payload.TryGetValue("Payload", out var nested)) payload = (IDictionary<string,object>)nested;
                        counters.Add(EventCounterAdapter.Parse(payload,data.TimeStampRelativeMSec));
                    }
                    catch (Exception e) when (e is not OutOfMemoryException) { warnings.Add("unsupported_counter_schema"); }
                }
                if(data.ProviderName=="System.Diagnostics.Metrics")
                {
                    try
                    {
                        var payload=data.PayloadNames.ToDictionary(name=>name,name=>data.PayloadByName(name));
                        counters.AddRange(modern.Parse(data.EventName,data.Version,data.TimeStampRelativeMSec,payload));
                    }
                    catch(Exception error) when(error is not OutOfMemoryException)
                    { warnings.Add("unsupported_modern_metrics_schema"); counterParseFailure?.Invoke(data,error); }
                }
            };
            source.AllEvents += data =>
            {
                if (++events > maximumEvents) { warnings.Add("analysis_event_cap"); source.StopProcessing(); return; }
                if(double.IsFinite(data.TimeStampRelativeMSec)&&data.TimeStampRelativeMSec>=0)
                { first=Math.Min(first,data.TimeStampRelativeMSec); last=Math.Max(last,data.TimeStampRelativeMSec); }
                string key = data.ProviderName + "/" + data.EventName + "/v" + data.Version;
                if (counts.Count < 10000 || counts.ContainsKey(key)) counts[key] = counts.GetValueOrDefault(key) + 1;
                else warnings.Add("event_name_cardinality_cap");
            };
            source.Process();
            loss = source.EventsLost > 0 ? "reported_loss" : "none_reported";
            if (source.EventsLost > 0) warnings.Add("events_lost");
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { warnings.Add("parser_failed"); }
        var temporal=TemporalReconstruction.Reconstruct(gc,pauses);
        foreach(string warning in modern.Warnings) warnings.Add(warning);
        foreach(string warning in temporal.Warnings) warnings.Add(warning);
        if (capture.State != CaptureState.Sealed) warnings.Add("capture_incomplete");
        warnings.Add("provider_coverage_requires_stimulus");
        if (capture.Profile.DataClasses.Contains("samples")) warnings.Add("stack_symbolization_not_qualified");
        bool hasJitData = capture.Profile.DataClasses.Contains("jit") || capture.Profile.DataClasses.Contains("inlining") ||
            methodsCompiledCount > 0 || inlineSucceededCount > 0 || inlineFailedCount > 0;
        bool hasContentionData = capture.Profile.DataClasses.Contains("contention") || contentionTotal > 0;
        var jitSummary = hasJitData ? new JitDiagnosticsSummary(methodsCompiledCount, inlineSucceededCount, inlineFailedCount) : null;
        var contentionSummary = hasContentionData
            ? new ContentionDiagnosticsSummary(contentionTotal, contentionManaged, contentionNative, contentionTotalMs, contentionMaxMs)
            : null;
        return new(Wire.Id("analysis"), capture.Id, capture.ArtifactId!, capture.Profile.Hash, Version,
            new(warnings.Any(w => w != "provider_coverage_requires_stimulus") ? "partial" : "parsed_capture",
                loss, Version, warnings.Order().ToArray()), temporal.GcCycles,
            temporal.Suspensions.Where(value=>value.StartMilliseconds.HasValue&&value.EndMilliseconds.HasValue)
                .Select(value=>new TimeInterval(value.StartMilliseconds!.Value,value.EndMilliseconds!.Value,value.Reason)).ToArray(),
            allocations,counters,markers,samples,counts,temporal.Suspensions,
            double.IsFinite(first)&&double.IsFinite(last)?new(first,last,"observed_event_bounds"):null,
            ParserExecution:null,
            JitSummary:jitSummary,
            JitMethods:jitMethods.Count>0?jitMethods:null,
            JitInlinings:jitInlinings.Count>0?jitInlinings:null,
            ContentionSummary:contentionSummary,
            Contentions:contentions.Count>0?contentions:null);
    }
    public MetricResult Metric(TraceSummary summary, Capture capture, string metric)
    {
        var provenance = new Provenance(capture.Id, summary.ArtifactId, summary.ParserVersion, summary.ProfileHash);
        bool has(string data) => capture.Profile.DataClasses.Contains(data);
        MetricResult Missing(string reason,string unit="unknown",string scope="unknown") => new(metric, Availability.NotCollected, null, unit, scope, reason, summary.Quality, provenance);
        if (metric is "method.average_ns" or "method.call_count")
            return Missing("Stack samples cannot establish per-call timing or exact invocation counts.");
        if (metric == "gc.cycle_count")
        {
            if (!has("gc")) return Missing("Runtime GC provider was not enabled.");
            if(summary.GcCycles.Count==0) return Missing("No retained GC starts or provider-coverage qualification; absence of events does not establish zero.","count","capture_process");
            var status = summary.Quality.Coverage == "partial" ? Availability.Partial : Availability.Available;
            return new(metric, status, summary.GcCycles.Count, "count", "capture_process", null, summary.Quality, provenance);
        }
        if (metric == "allocation.sampled_bytes")
        {
            if (!has("allocation_samples")) return Missing("Allocation sampling was not enabled.");
            if(summary.Allocations.Count==0) return Missing("No retained allocation ticks establish sampling coverage.","bytes","process_sampled_estimate");
            return new(metric, summary.Quality.Coverage == "partial" ? Availability.Partial : Availability.Available,
                summary.Allocations.Sum(a => (double)a.Amount), "bytes", "process_sampled_estimate", null, summary.Quality, provenance);
        }
        if (metric == "samples.count")
        {
            if (!has("samples")) return Missing("Managed stack sampling was not enabled.");
            if(summary.ManagedSampleCount==0) return Missing("No retained samples establish profiler coverage.","samples","process");
            return new(metric, Availability.Partial, summary.ManagedSampleCount, "samples", "process", "Symbol coverage is not qualified.", summary.Quality, provenance);
        }
        if (metric is "jit.methods_compiled" or "jit.inline_failures" or "jit.inline_succeeded")
        {
            bool required = metric == "jit.methods_compiled" ? (has("jit") || capture.Profile.Name == "cpu-diagnostic") : has("inlining");
            if (!required || summary.JitSummary is null) return Missing("Runtime JIT/inlining provider class was not enabled.", "count", "capture_process");
            double value = metric switch
            {
                "jit.methods_compiled" => summary.JitSummary.MethodsCompiledCount,
                "jit.inline_failures" => summary.JitSummary.InliningFailedCount,
                _ => summary.JitSummary.InliningSucceededCount
            };
            if (value == 0 && summary.Markers.Count == 0 && summary.JitSummary.MethodsCompiledCount == 0 && summary.JitSummary.InliningSucceededCount == 0)
                return Missing("No retained JIT events or harness boundaries establish provider coverage.", "count", "capture_process");
            return new(metric, summary.Quality.Coverage == "partial" ? Availability.Partial : Availability.Available,
                value, "count", "capture_process", null, summary.Quality, provenance);
        }
        if (metric is "contention.count" or "contention.duration_ms")
        {
            string unit = metric == "contention.count" ? "count" : "ms";
            if (!has("contention") || summary.ContentionSummary is null)
                return Missing("Runtime monitor-contention provider class was not enabled.", unit, "capture_process");
            if (summary.ContentionSummary.TotalCount == 0 && summary.Markers.Count == 0)
                return Missing("No retained contention events or harness boundaries establish provider coverage.", unit, "capture_process");
            double value = metric == "contention.count"
                ? summary.ContentionSummary.TotalCount
                : summary.ContentionSummary.TotalDurationMilliseconds;
            return new(metric, summary.Quality.Coverage == "partial" ? Availability.Partial : Availability.Available,
                value, unit, "capture_process", null, summary.Quality, provenance);
        }
        return Missing("Metric has no retained qualified evidence.");
    }
}
