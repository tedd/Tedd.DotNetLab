using System.Text.Json;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    private const string PrimaryComparisonVersion="paired-primary-objective/v1";
    private static Comparison PrimaryComparison(ObjectiveVector vector,string id)
    {
        var effect=vector.Effects[0]; bool ratio=effect.Policy.Scale==EffectScale.PositiveRatio;
        double? percent=ratio&&effect.Effect.HasValue?100*(effect.Effect.Value-1):null;
        if(percent.HasValue&&!double.IsFinite(percent.Value)) percent=null;
        return new(id,effect.Estimator,effect.EffectClass,effect.Quality,effect.IndependentBlocks,
            ratio?effect.Effect:null,percent,effect.StandardError,effect.DegreesOfFreedom,
            ratio?effect.DifferenceInterval:null,ratio?effect.EquivalenceInterval:null,effect.DifferenceP,effect.TostP,
            effect.EquivalencePassed,effect.AlphaLocal,vector.PromotionEligible,vector.ReasonCodes,[],
            vector.Id,effect with { RunIds=[] },effect.RunIds.Count);
    }
    private static bool UsesLegacyElapsedComparison(ExperimentPlan plan)=>
        plan.PrimaryPolicy is null&&!UsesGcObjective(plan)&&plan.PrimaryMetric is "scenario.ns_per_operation" or "scenario.elapsed_ns";
    // Synchronous SDK compatibility; MCP and asynchronous callers use CompareAsync.
    public IReadOnlyList<Comparison> Compare(string executionId)
    {
        var execution=Record<ExecutionRecord>(executionId,"execution"); var plan=Record<ExperimentPlan>(execution.PlanId,"plan"); Policies.Validate(plan);
        return UsesLegacyElapsedComparison(plan)?CompareElapsed(executionId):Task.Run(()=>CompareAsync(executionId)).GetAwaiter().GetResult();
    }
    public async Task<IReadOnlyList<Comparison>> CompareAsync(string executionId)
    {
        var execution=Record<ExecutionRecord>(executionId,"execution"); var plan=Record<ExperimentPlan>(execution.PlanId,"plan");
        Policies.Validate(plan);
        if(UsesLegacyElapsedComparison(plan)) return CompareElapsed(executionId);
        var indexes=await CompareObjectivesAsync(executionId).ConfigureAwait(false); var comparisons=new List<Comparison>();
        foreach(var index in indexes)
        {
            var vector=await ReadObjectivesAsync(index.Id).ConfigureAwait(false);
            string id="cmp-"+Wire.Hash(new { vector_id=vector.Id,vector.PlanHash,algorithm=PrimaryComparisonVersion });
            var comparison=PrimaryComparison(vector,id);
            lock(gate) { Upsert(id,"comparison",comparison); }
            comparisons.Add(comparison);
        }
        return comparisons;
    }
    private record MeasurementPair(string BlockId,string ClusterId,Measurement Baseline,Measurement Candidate,string BaselineRunId,string CandidateRunId);
    private static ObjectiveVector EvaluateObjectives(string executionId,ExperimentPlan plan,string planHash,string baselineVariant,
        string candidateVariant,IReadOnlyList<MeasurementPair> pairs,bool freshConfirmation,IReadOnlyList<string> blockers,bool profileMismatch=false,DiagnosticMetricSet? diagnostics=null)
    {
        Policies.Validate(plan);
        var policies=new[]{Metrics.Primary(plan)}.Concat(plan.GuardrailPolicies??[]).ToArray();
        var effects=policies.Select(policy=>ObjectiveEngine.Evaluate(policy,plan.Analysis,pairs.Select(pair=>new MetricPair(pair.BlockId,pair.ClusterId,
            ObjectiveValue(policy.Metric,pair.BaselineRunId,pair.Baseline,diagnostics),ObjectiveValue(policy.Metric,pair.CandidateRunId,pair.Candidate,diagnostics),pair.BaselineRunId,pair.CandidateRunId)).ToArray(),
            plan.IndependentBlocks,blockers.Concat(profileMismatch&&policy.Scale==EffectScale.PositiveRatio?["timing_profile_or_observer_mismatch"]:[])
                .Concat(policy.Metric=="gc.suspension_overlap_ns"?diagnostics?.Blockers??[]:[]).ToArray(),
            profileMismatch&&policy.ObservationKind is "batch_normalized_allocation_delta" or "allocation_delta"?["observer_mismatch_descriptive_allocation_only"]:null)).ToArray();
        var promotion=ObjectiveEngine.Promotion(effects,freshConfirmation);
        string hash=diagnostics is null?Wire.Hash(new { executionId,planHash,baselineVariant,candidateVariant,pairs,freshConfirmation,blockers,profileMismatch,algorithm=ObjectiveEngine.Version }):
            Wire.Hash(new { executionId,planHash,baselineVariant,candidateVariant,pairs,freshConfirmation,blockers,profileMismatch,diagnostics=diagnostics.Link,algorithm=ObjectiveEngine.Version });
        string id="objectives-"+hash;
        return new(id,executionId,plan.Id,planHash,baselineVariant,candidateVariant,ObjectiveEngine.Version,effects,ObjectiveEngine.Pareto(effects),
            promotion.Eligible,promotion.Reasons.Concat(effects[0].ReasonCodes).Distinct().ToArray(),diagnostics?.Link);
    }
    private async Task<ObjectiveVectorIndex> StoreObjectivesAsync(ObjectiveVector vector)
    {
        var artifact=await Artifacts.WriteJsonAsync(vector.Id,vector,ObjectiveEngine.Version);
        var index=new ObjectiveVectorIndex(vector.Id,vector.ExecutionId,vector.PlanId,vector.PlanHash,vector.BaselineVariant,vector.CandidateVariant,
            vector.Algorithm,artifact.Id,vector.Effects[0].RunIds.Count,vector.Effects.Select(effect=>effect with { RunIds=[] }).ToArray(),
            vector.Pareto,vector.PromotionEligible,vector.ReasonCodes,vector.DiagnosticEvidence);
        lock(gate)
        {
            Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"objective_comparison");
            Upsert(index.Id,"objective_vector",index);
        }
        return index;
    }
    public async Task<ObjectiveVector> ReadObjectivesAsync(string vectorId)
    {
        var index=Record<ObjectiveVectorIndex>(vectorId,"objective_vector");
        string path=await Artifacts.VerifiedPathAsync(Record<Artifact>(index.DetailArtifactId,"artifact"));
        var vector=Wire.Deserialize<ObjectiveVector>(await File.ReadAllTextAsync(path));
        if(vector.Id!=index.Id||vector.ExecutionId!=index.ExecutionId||vector.PlanHash!=index.PlanHash||vector.Algorithm!=index.Algorithm||vector.DiagnosticEvidence!=index.DiagnosticEvidence)
            throw new LabException("artifact_corrupt","Objective artifact differs from its retained index.");
        if(vector.DiagnosticEvidence is { } diagnostic)
        {
            var manifest=Record<Artifact>(diagnostic.ArtifactId,"artifact");
            if(manifest.Hash!=diagnostic.Hash) throw new LabException("artifact_corrupt","Diagnostic manifest hash differs from its objective vector.");
            await Artifacts.VerifiedPathAsync(manifest);
        }
        return vector;
    }
    public async Task<IReadOnlyList<ObjectiveVectorIndex>> CompareObjectivesAsync(string executionId)
    {
        var execution=Record<ExecutionRecord>(executionId,"execution");
        var plan=Record<ExperimentPlan>(execution.PlanId,"plan"); Policies.Validate(plan);
        if(execution.State!="completed"||execution.PlanHash!=Wire.Hash(plan)||execution.RunIds.Count!=plan.Blocks*plan.Variants.Count||
            execution.RunIds.Distinct().Count()!=execution.RunIds.Count)
            throw new LabException("not_comparable","Execution must complete its exact frozen objective budget.");
        var runs=execution.RunIds.Select(id=>Record<RunRecord>(id,"run")).ToArray();
        foreach(var run in runs)
        {
            var reference=Record<JsonElement>(run.Id+"-measurement","measurement_artifact");
            var artifact=Record<Artifact>(reference.GetProperty("artifact_id").GetString()!,"artifact");
            string path=await Artifacts.VerifiedPathAsync(artifact);
            if(Wire.Hash(Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(path)))!=Wire.Hash(run.Measurement))
                throw new LabException("not_comparable","Measurement differs from its sealed evidence.");
            Catalog.Pin(artifact.Id,"objective_comparison_input");
        }
        var diagnostics=await DiagnosticMetricsAsync(execution,plan,runs);
        var results=new List<ObjectiveVectorIndex>();
        foreach(string variant in plan.Variants.Skip(1))
        {
            var pairs=new List<MeasurementPair>(); var blockers=new List<string>(); bool profileMismatch=false;
            for(int block=0;block<plan.Blocks;block++)
            {
                var baseline=runs.Where(run=>run.BlockOrdinal==block&&run.Measurement.Variant==plan.Variants[0]).ToArray();
                var candidate=runs.Where(run=>run.BlockOrdinal==block&&run.Measurement.Variant==variant).ToArray();
                if(baseline.Length!=1||candidate.Length!=1) throw new LabException("not_comparable","Every matched block requires both unique arms.");
                var b=baseline[0]; var c=candidate[0];
                foreach(var run in new[]{b,c})
                    if(run.PlanId!=plan.Id||run.PlanHash!=execution.PlanHash||run.ExecutionId!=executionId||run.Measurement.RunId!=run.Id||
                        run.Measurement.ExecutionId!=executionId||run.Measurement.CompletedOperations!=plan.OperationsPerRun||
                        run.Measurement.InputHash!=plan.InputHash||run.Measurement.ScenarioVersion!=plan.ScenarioVersion||
                        !run.Measurement.Correct||!run.Measurement.Quiescent)
                        blockers.Add("run_fingerprint_or_correctness_mismatch");
                profileMismatch|=b.ProfileHash!=c.ProfileHash||b.ObserverState!=c.ObserverState;
                if(Wire.Hash(b.Compilation)!=Wire.Hash(c.Compilation)) blockers.Add("compilation_regime_mismatch");
                if(b.Target.RuntimeVersion!=c.Target.RuntimeVersion||
                    b.Target.Architecture!=c.Target.Architecture||b.Measurement.BlockId!=c.Measurement.BlockId)
                    blockers.Add("runtime_profile_or_block_mismatch");
                pairs.Add(new(b.Measurement.BlockId,b.Measurement.BlockId,b.Measurement,c.Measurement,b.Id,c.Id));
            }
            var vector=EvaluateObjectives(executionId,plan,execution.PlanHash,plan.Variants[0],variant,pairs,false,blockers.Distinct().ToArray(),profileMismatch,diagnostics);
            results.Add(await StoreObjectivesAsync(vector));
        }
        return results;
    }
}
