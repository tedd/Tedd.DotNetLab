using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Statistics;

public record LiveBenchmarkSummary(int Samples, double? MeanNanoseconds, double? MedianNanoseconds,
    double? StandardDeviationNanoseconds, double? CoefficientOfVariation, double? DriftRatio,
    double? TimerFloorNanoseconds, double? TimerFloorFraction, double? ThreadAllocatedBytesPerOperation,
    double? ProcessAllocatedBytesPerOperation, IReadOnlyDictionary<string,double> TelemetryPerOperation,
    string Quality, IReadOnlyList<string> Warnings,
    string Meaning = "Repeated batch means within one live process; dispersion is descriptive, not an independent-sample confidence interval or operation-latency distribution.");

public static class LiveBenchmarkStatistics
{
    public const string Version = "live-batch-summary/v1";
    public static LiveBenchmarkSummary Summarize(LiveBenchmarkSession session, LiveBenchmarkPolicy policy)
    {
        policy.Validate();
        if(session.Protocol!="live-calibrated-batches/v1" || session.PolicyHash!=Wire.Hash(policy) ||
            session.OperationsPerIteration is < 1 || session.OperationsPerIteration>policy.MaximumOperationsPerIteration ||
            policy.OperationsPerIteration is {} fixedCount&&session.OperationsPerIteration!=fixedCount || session.Iterations.Count>288)
            throw new LabException("invalid_measurement","Live benchmark protocol or calibration policy differs.");
        int ordinal=0,stage=-1;
        long totalOperations=0;
        foreach(var row in session.Iterations)
        {
            var measurement=row.Measurement;
            int currentStage=row.Phase switch { "pilot"=>0,"warmup"=>1,"overhead"=>2,"actual"=>3,_=>-1 };
            if(row.Ordinal!=ordinal++ || row.Phase is not ("pilot" or "warmup" or "overhead" or "actual") ||
                currentStage<stage ||
                measurement.Frequency<=0 || measurement.EndTicks<measurement.StartTicks || measurement.CompletedOperations<=0 ||
                measurement.ProcessAllocationBytes<0 || measurement.ThreadAllocationBytes is <0 ||
                row.Phase!="pilot"&&measurement.CompletedOperations!=session.OperationsPerIteration ||
                !double.IsFinite(measurement.NanosecondsPerOperation))
                throw new LabException("invalid_measurement","Live benchmark contains invalid iteration measurements.");
            stage=currentStage;
            if(row.Phase!="overhead") totalOperations=checked(totalOperations+measurement.CompletedOperations);
        }
        if(totalOperations>policy.MaximumTotalOperations) throw new LabException("invalid_measurement","Live workload exceeded the frozen operation budget.");
        var actual=session.Iterations.Where(r=>r.Phase=="actual").Select(r=>r.Measurement).ToArray();
        var warnings=new List<string>();
        if(!session.Correct||!session.Quiescent||session.Failure is not null||actual.Length!=policy.MeasurementIterations||
            session.Iterations.Any(r=>!r.Measurement.Correct||!r.Measurement.Quiescent))
            warnings.Add("incomplete_or_incorrect");
        int warmupCount=session.Iterations.Count(r=>r.Phase=="warmup");
        if(warmupCount<policy.MinimumWarmupIterations||warmupCount>policy.MaximumWarmupIterations||
            session.Iterations.Count(r=>r.Phase=="overhead")!=policy.OverheadIterations)
            if(!warnings.Contains("incomplete_or_incorrect")) warnings.Add("incomplete_or_incorrect");
        if(!session.WarmupConverged) warnings.Add("warmup_not_converged");
        if(actual.Length==0) return new(0,null,null,null,null,null,null,null,null,null,new Dictionary<string,double>(),"invalid",warnings);
        double[] costs=actual.Select(m=>m.NanosecondsPerOperation).ToArray();
        double mean=StatisticalEngine.Mean(costs),median=Median(costs);
        double sd=costs.Length>1?Math.Sqrt(costs.Sum(v=>(v-mean)*(v-mean))/(costs.Length-1)):0;
        double cv=mean>0?sd/mean:0;
        int half=costs.Length/2;
        double first=half>0?Median(costs.Take(half)):0, last=half>0?Median(costs.TakeLast(half)):0;
        double? drift=first>0?last/first:null;
        var overhead=session.Iterations.Where(r=>r.Phase=="overhead").Select(r=>r.Measurement.ElapsedNanoseconds).ToArray();
        double? floor=overhead.Length>0?Median(overhead):null;
        double elapsedMean=actual.Average(m=>m.ElapsedNanoseconds);
        double? fraction=elapsedMean>0&&floor.HasValue?floor/elapsedMean:null;
        if(mean<=0) warnings.Add("below_timer_resolution");
        if(actual.Any(m=>m.ElapsedNanoseconds<policy.TargetIterationMilliseconds*1_000_000d)) warnings.Add("iteration_too_short");
        if(cv>policy.MaximumCoefficientOfVariation) warnings.Add("high_variability");
        if(drift is null||Math.Abs(drift.Value-1)>policy.MaximumDrift) warnings.Add("measurement_drift");
        if(fraction is null||fraction>.01) warnings.Add("timer_floor_significant");
        if(actual.Any(m=>m.StartThreadId!=m.EndThreadId)) warnings.Add("thread_changed");
        var telemetry=new Dictionary<string,double>();
        void Metric(string name,Func<BatchTelemetry,long?> read)
        {
            if(actual.All(m=>m.Telemetry is {} t&&read(t).HasValue))
                telemetry[name]=actual.Average(m=>(double)read(m.Telemetry!)!.Value/m.CompletedOperations);
        }
        Metric("gc.gen0_collections",t=>t.Gen0Collections); Metric("gc.gen1_collections",t=>t.Gen1Collections); Metric("gc.gen2_collections",t=>t.Gen2Collections);
        Metric("cpu.thread_cycles",t=>t.ThreadCpuCycles); Metric("cpu.thread_nanoseconds",t=>t.ThreadCpuNanoseconds);
        Metric("cpu.instructions_retired",t=>t.InstructionsRetired); Metric("cpu.hardware_cycles",t=>t.HardwareCpuCycles);
        Metric("cpu.branch_misses",t=>t.BranchMisses); Metric("cpu.cache_misses",t=>t.CacheMisses);
        return new(actual.Length,mean,median,sd,cv,drift,floor,fraction,
            actual.All(m=>m.ThreadAllocationBytes.HasValue&&m.StartThreadId==m.EndThreadId)?actual.Average(m=>(double)m.ThreadAllocationBytes!.Value/m.CompletedOperations):null,
            actual.Average(m=>(double)m.ProcessAllocationBytes/m.CompletedOperations),telemetry,
            warnings.Contains("incomplete_or_incorrect")?"invalid":warnings.Count>0?"unstable":"stable_live_observation",warnings);
    }
    private static double Median(IEnumerable<double> values)
    {
        var sorted=values.Order().ToArray(); int middle=sorted.Length/2;
        return sorted.Length%2==0?sorted[middle-1]/2+sorted[middle]/2:sorted[middle];
    }
}
