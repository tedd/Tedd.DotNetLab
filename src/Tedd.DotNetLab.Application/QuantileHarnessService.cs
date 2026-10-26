using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;

namespace Tedd.DotNetLab.Application;

public record QuantileHarnessPlanSelection(string BaselinePlanId,string CandidatePlanId);
public record QuantileHarnessArmOrder(string BlockId,IReadOnlyList<string> Arms);
public record QuantileHarnessBinding(FrozenRecordReference BaselinePlan,FrozenRecordReference CandidatePlan,IReadOnlyList<QuantileHarnessArmOrder> Order,
    string OrderAlgorithm="splitmix64-rejection-fisher-yates/v1");
public record QuantileRunPair(string Id,string BaselineRunId,string CandidateRunId);
public record QuantileHarnessRunEvidence(FrozenRecordReference Run,string ArtifactId,string ArtifactHash);
public record QuantileHarnessBlockEvidence(string Id,QuantileHarnessRunEvidence Baseline,QuantileHarnessRunEvidence Candidate);
public record QuantileHarnessManifest(string PlanId,string PlanHash,IReadOnlyList<QuantileHarnessBlockEvidence> Blocks,string Provenance);
public record QuantileHarnessManifestReference(string ArtifactId,string Hash);

public sealed partial class LabService
{
    private const string QuantileHarnessProvenance="verified_registered_harness_individual_observations/v1";
    private FrozenRecordReference QuantileReference(string id,string kind)
    {
        var record=Catalog.Get(id);
        if(record.Kind!=kind) throw new LabException("invalid_request","Quantile source record kind differs.");
        return new(record.Id,record.Revision,record.Hash);
    }
    private T QuantileFrozen<T>(FrozenRecordReference reference,string kind)
    {
        var record=Catalog.Get(reference.Id);
        if(record.Kind!=kind||record.Revision!=reference.Revision||record.Hash!=reference.Hash)
            throw new LabException("not_comparable","Frozen quantile source revision or hash differs.");
        return Wire.Deserialize<T>(record.Json);
    }
    private QuantileHarnessBinding? FreezeQuantileHarnessBinding(QuantilePlanRequest design)
    {
        if(design.HarnessPlans is not { } selected) return null;
        if(design.ObservationType!="individual_operation_latency"||selected.BaselinePlanId==selected.CandidatePlanId||
            design.ObserverIdentity is not ("harness_sync_individual_stopwatch/v1" or "harness_async_individual_stopwatch/v1"))
            throw new LabException("invalid_plan","Harness quantiles require two arm plans and a qualified individual-operation observer.");
        var baselineRef=QuantileReference(selected.BaselinePlanId,"individual_latency_plan");
        var candidateRef=QuantileReference(selected.CandidatePlanId,"individual_latency_plan");
        var baseline=QuantileFrozen<IndividualLatencyPlan>(baselineRef,"individual_latency_plan");
        var candidate=QuantileFrozen<IndividualLatencyPlan>(candidateRef,"individual_latency_plan");
        if(baseline.ScenarioId!=candidate.ScenarioId||baseline.ScenarioVersion!=candidate.ScenarioVersion||baseline.InputHash!=candidate.InputHash||
            baseline.WarmupBatches!=candidate.WarmupBatches||baseline.Operations!=design.ObservationsPerArmPerBlock||candidate.Operations!=baseline.Operations)
            throw new LabException("not_comparable","Arm plans differ in scenario, input, warm-up, or individual-observation budget.");
        var random=new DeterministicRandom(design.Seed);
        return new(baselineRef,candidateRef,design.Clusters.SelectMany(c=>c.Blocks)
            .Select(id=>new QuantileHarnessArmOrder(id,random.Shuffle(new[]{"baseline","candidate"}))).ToArray());
    }
    private async Task<(IndividualLatencyRun Run,Measurement Measurement,QuantileHarnessRunEvidence Evidence)> QuantileRunAsync(
        QuantilePlan plan,string runId,FrozenRecordReference armPlan,CancellationToken cancellation)
    {
        var runReference=QuantileReference(runId,"individual_latency_run");
        var run=QuantileFrozen<IndividualLatencyRun>(runReference,"individual_latency_run");
        var individualPlan=QuantileFrozen<IndividualLatencyPlan>(armPlan,"individual_latency_plan");
        var operation=Catalog.GetOperation(run.Measurement.ExecutionId);
        if(operation.Kind!="individual_latencies"||operation.State!=OperationState.Completed||operation.ResultId!=runId||operation.TargetId!=run.Target.Id||
            operation.CreatedAt<plan.FrozenAt||run.PlanId!=armPlan.Id||run.PlanHash!=armPlan.Hash||run.Measurement.RunId!=runId||
            run.Measurement.Variant!=individualPlan.Variant||run.Measurement.ScenarioVersion!=individualPlan.ScenarioVersion||run.Measurement.InputHash!=individualPlan.InputHash||
            run.ObserverIdentity!=plan.Design.ObserverIdentity||run.CompilationRegime!="registered_harness_individual_timing"||run.Runtime.NativeProfilerPresent)
            throw new LabException("not_comparable","Individual run lacks completed, post-freeze, exact arm/observer evidence or uses an unqualified compilation regime.");
        var artifact=Record<Artifact>(run.ArtifactId,"artifact");
        if(!artifact.Complete||artifact.MediaType!="application/json"||artifact.Length>16777216)
            throw new LabException("artifact_corrupt","Individual measurement source is incomplete or exceeds its read budget.");
        var measured=Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact,cancellation),cancellation));
        Policies.Validate(measured); var vector=measured.IndividualLatencies;
        if(Wire.Hash(measured with { IndividualLatencies=null })!=Wire.Hash(run.Measurement)||!measured.Correct||!measured.Quiescent||
            vector is null||vector.ObserverIdentity!=run.ObserverIdentity||vector.ObservationType!="individual_operation_latency"||
            vector.Frequency!=measured.Frequency||vector.Frequency!=run.Runtime.StopwatchFrequency||measured.CompletedOperations!=individualPlan.Operations||
            vector.ElapsedTicks.Count!=individualPlan.Operations||vector.ElapsedTicks.Any(t=>t<0)||
            vector.ElapsedTicks.Sum(t=>(decimal)t)>measured.EndTicks-measured.StartTicks)
            throw new LabException("artifact_corrupt","Individual measurement vector, index, or clock envelope differs.");
        return(run,measured,new(runReference,artifact.Id,artifact.Hash));
    }
    private async Task<(QuantileObservations Observations,QuantileHarnessManifest Manifest)> CollectQuantileBindingsAsync(
        QuantilePlan plan,IReadOnlyList<QuantileRunPair> selected,CancellationToken cancellation)
    {
        var binding=plan.HarnessBinding??throw new LabException("invalid_plan","Quantile plan has no frozen harness arm binding.");
        if(selected is null||selected.Any(b=>b is null||string.IsNullOrWhiteSpace(b.Id)||string.IsNullOrWhiteSpace(b.BaselineRunId)||string.IsNullOrWhiteSpace(b.CandidateRunId)||
            b.Id.Length>128||b.BaselineRunId.Length>512||b.CandidateRunId.Length>512)||selected.Count!=binding.Order.Count||selected.Select(b=>b.Id).Distinct().Count()!=selected.Count||
            selected.SelectMany(b=>new[]{b.BaselineRunId,b.CandidateRunId}).Distinct().Count()!=selected.Count*2)
            throw new LabException("not_comparable","Run selection must cover every paired block once, with distinct source runs.");
        var pairs=selected.ToDictionary(b=>b.Id,StringComparer.Ordinal); var blocks=new List<PairedObservationBlock>(); var evidence=new List<QuantileHarnessBlockEvidence>();
        var usedProcesses=new HashSet<string>(StringComparer.Ordinal); string? runtimeHash=null; string? runtimeVersion=null; string? architecture=null; long? epoch=null;
        foreach(var cluster in plan.Design.Clusters)
        {
            TargetIdentity? process=null; long priorEnd=0;
            foreach(string blockId in cluster.Blocks)
            {
                cancellation.ThrowIfCancellationRequested();
                if(!pairs.TryGetValue(blockId,out var pair)) throw new LabException("not_comparable","A declared quantile block is absent.");
                var baseline=await QuantileRunAsync(plan,pair.BaselineRunId,binding.BaselinePlan,cancellation);
                var candidate=await QuantileRunAsync(plan,pair.CandidateRunId,binding.CandidatePlan,cancellation);
                if(baseline.Run.Target!=candidate.Run.Target||Wire.Hash(baseline.Run.Runtime)!=Wire.Hash(candidate.Run.Runtime))
                    throw new LabException("not_comparable","Paired arms require the same process epoch and runtime/build/observer footprint.");
                process??=baseline.Run.Target;
                if(process!=baseline.Run.Target) throw new LabException("not_comparable","All blocks within a cluster must belong to one process session.");
                runtimeHash??=Wire.Hash(baseline.Run.Runtime); runtimeVersion??=process.RuntimeVersion; architecture??=process.Architecture; epoch??=process.Epoch;
                if(runtimeHash!=Wire.Hash(baseline.Run.Runtime)||runtimeVersion!=process.RuntimeVersion||architecture!=process.Architecture||epoch!=process.Epoch)
                    throw new LabException("not_comparable","Clusters differ in build, runtime configuration, observer footprint, or code epoch.");
                var order=binding.Order.Single(o=>o.BlockId==blockId);
                var first=order.Arms[0]=="baseline"?baseline.Measurement:candidate.Measurement;
                var second=order.Arms[0]=="baseline"?candidate.Measurement:baseline.Measurement;
                if(first.StartTicks<priorEnd||first.EndTicks>second.StartTicks)
                    throw new LabException("not_comparable","Observed run order differs from the frozen randomized arm/block order.");
                priorEnd=second.EndTicks;
                double[] Nanoseconds(Measurement value)=>value.IndividualLatencies!.ElapsedTicks.Select(t=>t*(1e9/value.Frequency)).ToArray();
                blocks.Add(new(blockId,Nanoseconds(baseline.Measurement),Nanoseconds(candidate.Measurement)));
                evidence.Add(new(blockId,baseline.Evidence,candidate.Evidence));
            }
            string processKey=Wire.Hash(new { process!.ProcessId,process.StartTimeUtcTicks });
            if(!usedProcesses.Add(processKey)) throw new LabException("insufficient_independent_evidence","One process session cannot be split across declared independent clusters.");
        }
        var observations=new QuantileObservations(plan.Id,plan.ContentHash,plan.Design.ObservationType,"ns",plan.Design.WorkloadIdentity,
            plan.Design.ObserverIdentity,blocks,QuantileHarnessProvenance);
        ValidateObservations(plan,observations,true);
        return(observations,new(plan.Id,plan.ContentHash,evidence,QuantileHarnessProvenance));
    }
    public async Task<QuantileDataset> RegisterQuantileHarnessRunsAsync(string planId,IReadOnlyList<QuantileRunPair> blocks,CancellationToken cancellation=default)
    {
        var plan=Record<QuantilePlan>(planId,"quantile_plan");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(plan.Design.MaximumDurationSeconds));
        cancellation=deadline.Token;
        var collected=await CollectQuantileBindingsAsync(plan,blocks,cancellation);
        var source=await Artifacts.WriteJsonAsync(Wire.Id("quantile-harness"),collected.Observations,QuantileHarnessProvenance,cancellation:cancellation);
        var manifest=await Artifacts.WriteJsonAsync(Wire.Id("quantile-bindings"),collected.Manifest,QuantileHarnessProvenance,cancellation:cancellation);
        string id="quantile-data-"+Wire.Hash(new { planId,source.Hash,manifest_hash=manifest.Hash });
        var dataset=new QuantileDataset(id,plan.Id,plan.ContentHash,source.Id,source.Hash,collected.Observations.Blocks.Count,plan.Design.Clusters.Count,
            2*blocks.Count*plan.Design.ObservationsPerArmPerBlock,new(manifest.Id,manifest.Hash));
        lock(gate)
        {
            Upsert(source.Id,"artifact",source); Upsert(manifest.Id,"artifact",manifest);
            Catalog.Pin(source.Id,"quantile_individual_observations"); Catalog.Pin(manifest.Id,"quantile_harness_bindings");
            foreach(var block in collected.Manifest.Blocks)
            { Catalog.Pin(block.Baseline.ArtifactId,"quantile_harness_source"); Catalog.Pin(block.Candidate.ArtifactId,"quantile_harness_source"); }
            Upsert(id,"quantile_dataset",dataset);
        }
        return dataset;
    }
    private async Task VerifyQuantileHarnessEvidenceAsync(QuantilePlan plan,QuantileDataset dataset,QuantileObservations data,CancellationToken cancellation)
    {
        var reference=dataset.HarnessEvidence!; var artifact=Record<Artifact>(reference.ArtifactId,"artifact");
        if(artifact.Hash!=reference.Hash||!artifact.Complete||artifact.Length>16777216||artifact.MediaType!="application/json")
            throw new LabException("artifact_corrupt","Harness binding manifest differs or exceeds its read budget.");
        var manifest=Wire.Deserialize<QuantileHarnessManifest>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact,cancellation),cancellation));
        if(manifest.PlanId!=plan.Id||manifest.PlanHash!=plan.ContentHash||manifest.Provenance!=QuantileHarnessProvenance||manifest.Blocks is null||
            manifest.Blocks.Any(b=>b is null||b.Baseline is null||b.Candidate is null))
            throw new LabException("artifact_corrupt","Harness manifest does not identify this frozen dataset.");
        var verified=await CollectQuantileBindingsAsync(plan,manifest.Blocks.Select(b=>new QuantileRunPair(b.Id,b.Baseline.Run.Id,b.Candidate.Run.Id)).ToArray(),cancellation);
        if(Wire.Hash(verified.Manifest)!=Wire.Hash(manifest)||Wire.Hash(verified.Observations)!=Wire.Hash(data)||
            dataset.Blocks!=data.Blocks.Count||dataset.Clusters!=plan.Design.Clusters.Count||dataset.RawObservations!=2*data.Blocks.Count*plan.Design.ObservationsPerArmPerBlock)
            throw new LabException("artifact_corrupt","Frozen run bindings or derived nanosecond observations differ from sealed sources.");
    }
}
