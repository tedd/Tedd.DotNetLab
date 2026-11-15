using System.Diagnostics;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Harness;

internal static class LiveBenchmarkRunner
{
    public const string Protocol = "live-calibrated-batches/v1";

    public static LiveBenchmarkSession Run(ISynchronousScenario scenario, RunRequest request, LiveBenchmarkPolicy policy, CancellationToken token)
    {
        policy.Validate();
        var descriptor = scenario.Describe();
        var rows = new List<LiveBenchmarkIteration>();
        int operations = policy.OperationsPerIteration ?? policy.InitialOperations;
        long total = 0;
        bool settled = false, quiescent = true;
        string? failure = null;
        Func<int, long> workload = scenario.ExecuteBatch;
        Func<int, long> empty = static count => count;

        Measurement Observe(string phase, Func<int, long> execute, bool validate)
        {
            token.ThrowIfCancellationRequested();
            if (validate && total + operations > policy.MaximumTotalOperations)
                throw new LabException("resource_limit", "Live benchmark exhausted its total operation budget.");
            if (validate) { total += operations; scenario.Reset(new(descriptor.ResetLevel)); }
            var measured = Measure(execute, operations, request, rows.Count) with { InputHash = descriptor.InputHash, ScenarioVersion = descriptor.Version };
            if (validate)
            {
                var correctness = scenario.Validate();
                quiescent &= correctness.Quiescent;
                measured = measured with { Correct = correctness.Correct && correctness.Quiescent && measured.CompletedOperations == operations,
                    Quiescent = correctness.Quiescent, Failure = correctness.Reason };
            }
            rows.Add(new(phase, rows.Count, measured));
            if (!measured.Correct) throw new LabException("scenario_failed", measured.Failure ?? "Completed-work or quiescence contract failed.");
            return measured;
        }

        try
        {
            scenario.Prepare(new(request.Variant, token));
            if (policy.OperationsPerIteration is null)
            {
                while (true)
                {
                    var pilot = Observe("pilot", workload, true);
                    if (operations == policy.MaximumOperationsPerIteration) break;
                    // A slow first invocation can include JIT work. Confirm the batch duration before freezing its size.
                    if (pilot.ElapsedNanoseconds >= policy.TargetIterationMilliseconds * 1_000_000d &&
                        Observe("pilot", workload, true).ElapsedNanoseconds >= policy.TargetIterationMilliseconds * 1_000_000d) break;
                    operations = (int)Math.Min((long)operations * 2, policy.MaximumOperationsPerIteration);
                }
            }
            var warmups = new List<double>();
            for (int i = 0; i < policy.MaximumWarmupIterations; i++)
            {
                warmups.Add(Observe("warmup", workload, true).NanosecondsPerOperation);
                if (warmups.Count < policy.MinimumWarmupIterations) continue;
                double previous = Median(warmups.TakeLast(6).Take(3));
                double current = Median(warmups.TakeLast(3));
                if (previous > 0 && Math.Abs(current / previous - 1) <= policy.WarmupTolerance) { settled = true; break; }
            }
            // Warm the measurement delegate before retaining its empty-call floor.
            _ = Measure(empty, operations, request, -1);
            for (int i = 0; i < policy.OverheadIterations; i++) Observe("overhead", empty, false);
            for (int i = 0; i < policy.MeasurementIterations; i++) Observe("actual", workload, true);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failure = error is LabException lab ? lab.Code : error is OperationCanceledException ? "cancelled" : "scenario_failed";
        }
        finally
        {
            try { scenario.Cleanup(); }
            catch (Exception error) when (error is not OutOfMemoryException) { failure = "cleanup_failed"; quiescent = false; }
        }
        if(token.IsCancellationRequested) failure??="cancelled";
        return new(Protocol, Wire.Hash(policy), operations, settled, failure is null, quiescent, failure, rows);
    }

    private static double Median(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        return ordered[ordered.Length / 2];
    }

    private static Measurement Measure(Func<int, long> execute, int operations, RunRequest request, int ordinal)
    {
        using var telemetry = new BatchTelemetrySession(captureThreadCounters: true);
        int thread = Environment.CurrentManagedThreadId;
        telemetry.CapturePreBatchGcSnapshot();
        long processBefore = GC.GetTotalAllocatedBytes(true), threadBefore = GC.GetAllocatedBytesForCurrentThread();
        telemetry.BeginThreadWindow();
        long start = Stopwatch.GetTimestamp();
        long completed = execute(operations);
        long end = Stopwatch.GetTimestamp();
        telemetry.EndThreadWindow();
        long threadBytes = GC.GetAllocatedBytesForCurrentThread() - threadBefore;
        long processBytes = GC.GetTotalAllocatedBytes(true) - processBefore;
        int endThread = Environment.CurrentManagedThreadId;
        return new(request.ExecutionId, request.RunId + "-" + ordinal, request.BlockId, request.Variant, start, end,
            Stopwatch.Frequency, completed, processBytes, threadBytes, thread, endThread, completed == operations, null,
            "", "", Telemetry: telemetry.BuildSnapshot(thread == endThread));
    }
}
