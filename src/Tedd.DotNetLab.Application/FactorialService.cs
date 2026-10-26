using System.Text.Json;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public async Task<FactorialIndex> CompareFactorialAsync(string executionId)
    {
        var execution=Record<ExecutionRecord>(executionId,"execution"); var plan=Record<ExperimentPlan>(execution.PlanId,"plan");
        Policies.Validate(plan); var design=plan.Factorial??throw new LabException("invalid_plan","Interaction inference requires a predeclared four-arm design.");
        if(execution.State!="completed"||execution.PlanHash!=Wire.Hash(plan)||execution.RunIds.Count!=plan.Blocks*4||
            execution.RunIds.Distinct().Count()!=execution.RunIds.Count)
            throw new LabException("not_comparable","Factorial execution must complete its original four-arm budget.");
        var runs=execution.RunIds.Select(id=>Record<RunRecord>(id,"run")).ToArray();
        var sources=new List<object>(); var blockers=new List<string>();
        foreach(var run in runs)
        {
            var reference=Record<JsonElement>(run.Id+"-measurement","measurement_artifact");
            string artifactId=reference.GetProperty("artifact_id").GetString()!;
            string path=await Artifacts.VerifiedPathAsync(Record<Artifact>(artifactId,"artifact"));
            if(Wire.Hash(Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(path)))!=Wire.Hash(run.Measurement))
                throw new LabException("artifact_corrupt","Factorial input differs from its sealed measurement.");
            if(run.ExecutionId!=executionId||run.PlanId!=plan.Id||run.PlanHash!=execution.PlanHash||run.Measurement.ExecutionId!=executionId||
                run.Measurement.RunId!=run.Id||run.Measurement.InputHash!=plan.InputHash||run.Measurement.ScenarioVersion!=plan.ScenarioVersion||
                run.Measurement.CompletedOperations!=plan.OperationsPerRun||!run.Measurement.Correct||!run.Measurement.Quiescent)
                blockers.Add("run_fingerprint_or_correctness_mismatch");
            sources.Add(new { run_id=run.Id,record_hash=Catalog.Get(run.Id).Hash,artifact_id=artifactId });
        }
        if(runs.Select(run=>Wire.Hash(new { run.Target,run.ProfileHash,run.ObserverState,run.Scenario,run.Runtime,run.Compilation })).Distinct().Count()!=1)
            blockers.Add("factorial_runtime_or_observer_mismatch");
        var blocks=new List<RunRecord[]>();
        for(int block=0;block<plan.Blocks;block++)
        {
            var selected=runs.Where(run=>run.BlockOrdinal==block).ToArray();
            if(selected.Length!=4||!selected.Select(run=>run.ArmOrdinal).Order().SequenceEqual(new[]{0,1,2,3})||
                !selected.Select(run=>run.Measurement.Variant).Order(StringComparer.Ordinal).SequenceEqual(plan.Variants.Order(StringComparer.Ordinal))||
                selected.Select(run=>run.Measurement.BlockId).Distinct().Count()!=1)
                throw new LabException("not_comparable","A complete unique four-arm block is required; missing arms cannot be invented or excluded post hoc.");
            blocks.Add(plan.Variants.Select(variant=>selected.Single(run=>run.Measurement.Variant==variant)).ToArray());
        }
        var diagnostics=await DiagnosticMetricsAsync(execution,plan,runs);
        var definitions=new[]{("A_standalone",0,1),("B_standalone",0,2),("joint",0,3),("A_given_B",2,3),("B_given_A",1,3)};
        var vectors=new Dictionary<string,string>();
        foreach(var (name,baseline,candidate) in definitions)
        {
            var pairs=blocks.Select(block=>new MeasurementPair(block[0].Measurement.BlockId,block[0].Measurement.BlockId,
                block[baseline].Measurement,block[candidate].Measurement,block[baseline].Id,block[candidate].Id)).ToArray();
            var vector=EvaluateObjectives(executionId,plan,execution.PlanHash,plan.Variants[baseline],plan.Variants[candidate],pairs,false,blockers.Distinct().ToArray(),diagnostics:diagnostics);
            vectors[name]=(await StoreObjectivesAsync(vector)).Id;
        }
        var policies=new[]{Metrics.Primary(plan)}.Concat(plan.GuardrailPolicies??[]).ToArray();
        var interactions=policies.Select(policy=>FactorialEngine.Interaction(policy,design.InteractionScales[policy.Metric],plan.Analysis,
            blocks.Select(block=>new FactorialMetricBlock(block[0].Measurement.BlockId,block[0].Measurement.BlockId,
                ObjectiveValue(policy.Metric,block[0].Id,block[0].Measurement,diagnostics),ObjectiveValue(policy.Metric,block[1].Id,block[1].Measurement,diagnostics),
                ObjectiveValue(policy.Metric,block[2].Id,block[2].Measurement,diagnostics),ObjectiveValue(policy.Metric,block[3].Id,block[3].Measurement,diagnostics),block.Select(run=>run.Id).ToArray())).ToArray(),
            plan.IndependentBlocks,blockers.Concat(policy.Metric=="gc.suspension_overlap_ns"?diagnostics?.Blockers??[]:[]).Distinct().ToArray())).ToArray();
        string hash=diagnostics is null?Wire.Hash(new { executionId,execution.PlanHash,inputs=runs.Select(Wire.Hash),algorithm=FactorialEngine.Version,objectives=ObjectiveEngine.Version }):
            Wire.Hash(new { executionId,execution.PlanHash,inputs=runs.Select(Wire.Hash),diagnostics=diagnostics.Link,algorithm=FactorialEngine.Version,objectives=ObjectiveEngine.Version });
        string id="factorial-"+hash;
        var result=new FactorialResult(id,executionId,plan.Id,execution.PlanHash,FactorialEngine.Version,design,vectors,interactions,execution.RunIds);
        object payload=diagnostics is null?new { result,sources,plan }:new { result,sources,plan,diagnostic_evidence=diagnostics.Link };
        var artifact=await Artifacts.WriteJsonAsync(id,payload,FactorialEngine.Version,67108864);
        var index=new FactorialIndex(id,executionId,plan.Id,execution.PlanHash,FactorialEngine.Version,artifact.Id,design,vectors,
            interactions.Select(interaction=>interaction with { RunIds=[] }).ToArray(),execution.RunIds.Count);
        lock(gate)
        {
            Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"factorial_comparison");
            foreach(var run in runs)
            {
                var reference=Catalog.Get<JsonElement>(run.Id+"-measurement"); Catalog.Pin(reference.GetProperty("artifact_id").GetString()!,"factorial_input");
            }
            Upsert(index.Id,"factorial_comparison",index);
        }
        return index;
    }
    public async Task<FactorialResult> ReadFactorialAsync(string id)
    {
        var index=Record<FactorialIndex>(id,"factorial_comparison");
        string path=await Artifacts.VerifiedPathAsync(Record<Artifact>(index.ArtifactId,"artifact"));
        using var document=JsonDocument.Parse(await File.ReadAllTextAsync(path),new JsonDocumentOptions { MaxDepth=32 });
        var result=document.RootElement.GetProperty("result").Deserialize<FactorialResult>(Wire.Json)!;
        if(result.Id!=id||result.PlanHash!=index.PlanHash||result.ExecutionId!=index.ExecutionId||result.Algorithm!=index.Algorithm)
            throw new LabException("artifact_corrupt","Factorial artifact differs from its immutable index.");
        return result;
    }
}
