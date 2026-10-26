using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Domain;

public record MetricDefinition(string Name,string Unit,string ObservationKind,string Scope,MetricDirection Direction,EffectScale Scale);
public static class Metrics
{
    public static string Identity(string name)=>name=="scenario.operations_per_second"?"scenario.throughput_ops_s":name;
    public static MetricDefinition Define(string name)=>name switch
    {
        "scenario.ns_per_operation"=>new(name,"ns/operation","batch_normalized_mean","registered_batch",MetricDirection.LowerIsBetter,EffectScale.PositiveRatio),
        "scenario.elapsed_ns"=>new(name,"ns","batch_elapsed","registered_batch",MetricDirection.LowerIsBetter,EffectScale.PositiveRatio),
        "scenario.operations_per_second" or "scenario.throughput_ops_s"=>new(name,"operations/second","batch_rate","registered_batch",MetricDirection.HigherIsBetter,EffectScale.PositiveRatio),
        "allocation.process_bytes"=>new(name,"bytes","allocation_delta","process",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "allocation.thread_bytes"=>new(name,"bytes","allocation_delta","current_thread",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "scenario.process_allocation_bytes_per_operation"=>new(name,"bytes/operation","batch_normalized_allocation_delta","process",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "scenario.thread_allocation_bytes_per_operation"=>new(name,"bytes/operation","batch_normalized_allocation_delta","current_thread",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "gc.suspension_overlap_ns"=>new(name,"ns","execution_suspension_overlap","process",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "cpu.thread_cycles_per_operation"=>new(name,"cycles/operation","batch_normalized_mean","current_thread",MetricDirection.LowerIsBetter,EffectScale.PositiveRatio),
        "cpu.thread_ns_per_operation"=>new(name,"ns/operation","batch_normalized_mean","current_thread",MetricDirection.LowerIsBetter,EffectScale.PositiveRatio),
        "pmu.instructions_per_operation"=>new(name,"instructions/operation","batch_normalized_mean","current_thread",MetricDirection.LowerIsBetter,EffectScale.PositiveRatio),
        "pmu.hardware_cycles_per_operation"=>new(name,"cycles/operation","batch_normalized_mean","current_thread",MetricDirection.LowerIsBetter,EffectScale.PositiveRatio),
        "pmu.branch_misses_per_operation"=>new(name,"misses/operation","batch_normalized_delta","current_thread",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "pmu.cache_misses_per_operation"=>new(name,"misses/operation","batch_normalized_delta","current_thread",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "gc.gen0_collections"=>new(name,"collections","gc_collection_delta","process",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "gc.gen1_collections"=>new(name,"collections","gc_collection_delta","process",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "gc.gen2_collections"=>new(name,"collections","gc_collection_delta","process",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "gc.heap_size_bytes"=>new(name,"bytes","heap_snapshot","process",MetricDirection.LowerIsBetter,EffectScale.PositiveRatio),
        "gc.fragmented_bytes"=>new(name,"bytes","heap_snapshot","process",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        "gc.committed_bytes"=>new(name,"bytes","heap_snapshot","process",MetricDirection.LowerIsBetter,EffectScale.PositiveRatio),
        "gc.pinned_objects"=>new(name,"objects","heap_snapshot","process",MetricDirection.LowerIsBetter,EffectScale.AbsoluteDifference),
        _=>throw new LabException("invalid_plan","Unknown objective metric.")
    };
    public static MetricPolicy Primary(ExperimentPlan plan)
    {
        var metric=Define(plan.PrimaryMetric); var analysis=plan.Analysis;
        if(plan.PrimaryPolicy is { } declared)
        {
            if(declared.Metric!=plan.PrimaryMetric||declared.DescriptiveOnly)
                throw new LabException("invalid_plan","Primary policy must identify the primary metric and declare inferential thresholds.");
            return declared;
        }
        if(metric.Scale!=EffectScale.PositiveRatio||metric.Direction!=MetricDirection.LowerIsBetter)
            throw new LabException("invalid_plan","Non-elapsed primary objectives require an explicit frozen metric policy.");
        return new(metric.Name,metric.Unit,metric.ObservationKind,metric.Direction,metric.Scale,analysis.EquivalenceLowerRatio,
            analysis.EquivalenceUpperRatio,analysis.UsefulImprovementRatio,analysis.MeaningfulRegressionRatio,analysis.MinimumBlocks);
    }
    public static MetricValue Read(string name,Measurement measurement)
    {
        var metric=Define(name);
        MetricValue Result(Availability status,double? value,string? reason=null)=>new(name,status,value,metric.Unit,metric.ObservationKind,metric.Scope,reason);
        if(name=="gc.suspension_overlap_ns") return Result(Availability.NotCollected,null,"Target-clock-aligned suspension intervals are not part of this measurement envelope.");
        if(measurement.CompletedOperations<=0||measurement.EndTicks<=measurement.StartTicks||measurement.Frequency<=0)
            return Result(Availability.Invalid,null,"Invalid clock or completed-work count.");
        bool telemetryMetric=name is "cpu.thread_cycles_per_operation" or "cpu.thread_ns_per_operation" or
            "pmu.instructions_per_operation" or "pmu.hardware_cycles_per_operation" or "pmu.branch_misses_per_operation" or
            "pmu.cache_misses_per_operation" or "gc.gen0_collections" or "gc.gen1_collections" or "gc.gen2_collections" or
            "gc.heap_size_bytes" or "gc.fragmented_bytes" or "gc.committed_bytes" or "gc.pinned_objects";
        if(telemetryMetric)
        {
            if(measurement.Telemetry is not { } telemetry)
                return Result(Availability.NotCollected,null,"Batch hardware/GC telemetry was not retained in this measurement.");
            if(metric.Scope=="current_thread"&&measurement.StartThreadId!=measurement.EndThreadId)
                return Result(Availability.NotApplicable,null,"Async or cross-thread execution cannot use current-thread CPU/PMU counters.");
            double? telemetryValue=name switch
            {
                "cpu.thread_cycles_per_operation"=>telemetry.ThreadCpuCycles/(double)measurement.CompletedOperations,
                "cpu.thread_ns_per_operation"=>telemetry.ThreadCpuNanoseconds/(double)measurement.CompletedOperations,
                "pmu.instructions_per_operation"=>telemetry.InstructionsRetired/(double)measurement.CompletedOperations,
                "pmu.hardware_cycles_per_operation"=>telemetry.HardwareCpuCycles/(double)measurement.CompletedOperations,
                "pmu.branch_misses_per_operation"=>telemetry.BranchMisses/(double)measurement.CompletedOperations,
                "pmu.cache_misses_per_operation"=>telemetry.CacheMisses/(double)measurement.CompletedOperations,
                "gc.gen0_collections"=>telemetry.Gen0Collections,
                "gc.gen1_collections"=>telemetry.Gen1Collections,
                "gc.gen2_collections"=>telemetry.Gen2Collections,
                "gc.heap_size_bytes"=>telemetry.HeapSizeBytes,
                "gc.fragmented_bytes"=>telemetry.FragmentedBytes,
                "gc.committed_bytes"=>telemetry.TotalCommittedBytes,
                "gc.pinned_objects"=>telemetry.PinnedObjectsCount,
                _=>null
            };
            if(telemetryValue is null) return Result(Availability.NotSupported,null,"Counter is not supported or permitted on this OS/kernel.");
            if(!double.IsFinite(telemetryValue.Value)||telemetryValue<0||metric.Scale==EffectScale.PositiveRatio&&telemetryValue<=0)
                return Result(Availability.Invalid,null,"Metric value is nonfinite, negative, or nonpositive on a ratio scale.");
            return Result(Availability.Available,telemetryValue);
        }
        double? value=name switch
        {
            "scenario.ns_per_operation"=>measurement.NanosecondsPerOperation,
            "scenario.elapsed_ns"=>measurement.ElapsedNanoseconds,
            "scenario.operations_per_second" or "scenario.throughput_ops_s"=>measurement.CompletedOperations*1e9/measurement.ElapsedNanoseconds,
            "allocation.process_bytes"=>measurement.ProcessAllocationBytes,
            "allocation.thread_bytes" when measurement.StartThreadId==measurement.EndThreadId=>measurement.ThreadAllocationBytes,
            "scenario.process_allocation_bytes_per_operation"=>measurement.ProcessAllocationBytes/(double)measurement.CompletedOperations,
            "scenario.thread_allocation_bytes_per_operation" when measurement.StartThreadId==measurement.EndThreadId=>measurement.ThreadAllocationBytes/(double)measurement.CompletedOperations,
            _=>null
        };
        if(value is null) return Result(Availability.NotApplicable,null,"Eligible same-thread allocation evidence is unavailable.");
        if(!double.IsFinite(value.Value)||value<0) return Result(Availability.Invalid,null,"Metric value is nonfinite or negative.");
        return Result(Availability.Available,value);
    }
}
