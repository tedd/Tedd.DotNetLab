using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using System.Text.Json;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class PrimaryObjectiveTests
{
    public static MetricPolicy Policy(string name)
    {
        var definition=Metrics.Define(name); bool ratio=definition.Scale==EffectScale.PositiveRatio; bool higher=definition.Direction==MetricDirection.HigherIsBetter;
        double margin=name.Contains("per_operation",StringComparison.Ordinal)? .01:1;
        return new(name,definition.Unit,definition.ObservationKind,definition.Direction,definition.Scale,
            ratio?.99:-margin,ratio?1.01:margin,ratio?higher?1.01:.99:-margin,ratio?higher?.99:1.01:margin);
    }
    private static async Task<string> StoredExecution(LabService service,MetricPolicy policy,bool zeroBaseline=false)
    {
        var plan=service.Plan(new("","fixture","1","input",["baseline","candidate"],10,100,1,42,"quiet-validation",true,
            "Independent synthetic matched blocks.",false,60,new(),PrimaryMetric:policy.Metric,PrimaryPolicy:policy));
        string id=Wire.Id("execution"); var target=new TargetIdentity("fixture-target",1,1,"fixture","10.0.12","X64");
        var scenario=new ScenarioDescriptor("fixture","1","input",ResetLevel.LogicalState,ThreadAffinity.Any,false,"One verified operation",plan.Variants);
        var ids=new List<string>();
        for(int block=0;block<10;block++)
            for(int arm=0;arm<2;arm++)
            {
                string runId=Wire.Id("run"),variant=plan.Variants[arm],blockId=id+"-block-"+block;
                long start=100000+block*50000+arm*20000,elapsed=arm==0?10000:8000+block*10;
                long allocation=zeroBaseline?arm==0?0:1+block:arm==0?100:80+block;
                var measured=new Measurement(id,runId,blockId,variant,start,start+elapsed,1000000000,100,allocation,allocation,1,1,true,null,"input","1");
                var run=new RunRecord(runId,id,plan.Id,Wire.Hash(plan),block,arm,target,scenario,Profiles.Resolve(plan.Profile).Hash,"fixture-observer",measured);
                service.Catalog.Put(runId,"run",run);
                var artifact=await service.Artifacts.WriteJsonAsync(id,measured,"primary-fixture/v1"); service.Catalog.Put(artifact.Id,"artifact",artifact);
                service.Catalog.Put(runId+"-measurement","measurement_artifact",new { artifact_id=artifact.Id }); ids.Add(runId);
            }
        service.Catalog.Put(id,"execution",new ExecutionRecord(id,plan.Id,Wire.Hash(plan),target,"completed",ids,DateTimeOffset.UtcNow)); return id;
    }
    [Theory,InlineData("scenario.throughput_ops_s"),InlineData("scenario.operations_per_second"),InlineData("allocation.process_bytes"),InlineData("allocation.thread_bytes"),
        InlineData("scenario.process_allocation_bytes_per_operation"),InlineData("scenario.thread_allocation_bytes_per_operation"),InlineData("scenario.ns_per_operation"),
        Case("UT-STAT-028,UT-STAT-018","FR-033,FR-034,FR-036","M2","Integration")]
    public async Task FrozenPrimaryPolicy_DrivesPublicComparisonWithActualDirectionScaleAndUnits(string metric)
    {
        using var workspace=new TestWorkspace(); string executionId,comparisonId;
        await using(var service=new LabService(workspace.Root))
        {
            var policy=Policy(metric); executionId=await StoredExecution(service,policy);
            var reply=await new LabTools(service).Compare(new(executionId)); Assert.True(reply.Status=="ok",Wire.Serialize(reply));
            var comparison=Assert.Single(reply.Data!.Value.Deserialize<Comparison[]>(Wire.Json)!); comparisonId=comparison.Id;
            var primary=comparison.PrimaryEffect!; Assert.Equal(policy,primary.Policy); Assert.Equal(metric,primary.Metric);
            Assert.Equal(EffectClass.Improved,comparison.EffectClass); Assert.False(comparison.PromotionEligible); Assert.NotNull(primary.DifferenceInterval);
            Assert.Equal(policy.Unit,primary.Unit); Assert.Empty(primary.RunIds); Assert.Empty(comparison.RunIds); Assert.Equal(20,comparison.InputRunCount);
            Assert.NotNull(comparison.ObjectiveVectorId); var vector=await service.ReadObjectivesAsync(comparison.ObjectiveVectorId!);
            Assert.Equal(primary.Effect,vector.Effects[0].Effect); Assert.Equal(20,vector.Effects[0].RunIds.Count);
            if(policy.Scale==EffectScale.AbsoluteDifference)
            { Assert.Null(comparison.Ratio); Assert.Null(comparison.ChangePercent); Assert.Null(comparison.DifferenceIntervalRatio); Assert.True(primary.Effect<0); }
            else
            {
                Assert.Equal(primary.Effect,comparison.Ratio); Assert.Equal(primary.DifferenceInterval,comparison.DifferenceIntervalRatio);
                Assert.True(policy.Direction==MetricDirection.HigherIsBetter?comparison.Ratio>1:comparison.Ratio<1);
            }
            Assert.Equal(comparisonId,Assert.Single(service.Compare(executionId)).Id);
        }
        await using var reopened=new LabService(workspace.Root); Assert.Equal(comparisonId,Assert.Single(await reopened.CompareAsync(executionId)).Id);
    }
    [Fact,Case("UT-STAT-018","FR-024,FR-033","M2","Integration")]
    public async Task ZeroBaselineAllocation_UsesAbsoluteRegressionWithoutInventingPercentage()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        string id=await StoredExecution(service,Policy("allocation.process_bytes"),true); var comparison=Assert.Single(await service.CompareAsync(id));
        Assert.Equal(EffectClass.Regressed,comparison.EffectClass); Assert.Equal(5.5,comparison.PrimaryEffect!.Effect);
        Assert.Equal("bytes",comparison.PrimaryEffect.EffectUnit); Assert.Null(comparison.Ratio); Assert.Null(comparison.ChangePercent);
        Assert.False(comparison.PromotionEligible);
    }
    [Fact,Case("UT-EXP-006,UT-STAT-028","FR-031,FR-033,FR-036","M2","Unit")]
    public async Task PrimaryPolicyMissingWrongDirectionScaleOrDuplicateAlias_IsRejectedBeforeExecution()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var policy=Policy("scenario.throughput_ops_s");
        var plan=new ExperimentPlan("","fixture","1","input",["b","c"],10,100,1,42,"quiet-validation",true,"Independent fixture blocks.",false,60,new(),PrimaryMetric:policy.Metric,PrimaryPolicy:policy);
        foreach(var invalid in new[]{plan with { PrimaryPolicy=null },plan with { PrimaryPolicy=policy with { Direction=MetricDirection.LowerIsBetter } },
            plan with { PrimaryPolicy=policy with { Scale=EffectScale.AbsoluteDifference } },plan with { PrimaryPolicy=policy with { Unit="bytes" } },
            plan with { PrimaryPolicy=policy with { DescriptiveOnly=true } },plan with { PrimaryPolicy=Policy("allocation.process_bytes") },
            plan with { Analysis=new(FamilySize:2),GuardrailMetrics=["scenario.operations_per_second"],GuardrailPolicies=[Policy("scenario.operations_per_second")] }})
            Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.Plan(invalid)).Code);
        Assert.Empty(service.Catalog.List("plan").Rows); Assert.DoesNotContain("primary_policy",Wire.Serialize(plan with { PrimaryPolicy=null }));
    }
    [Theory,InlineData("scenario.throughput_ops_s","cpu-sum"),InlineData("allocation.process_bytes","cpu-sum"),InlineData("allocation.thread_bytes","async-yield"),
        Case("IT-HAR-001,IT-HAR-003","FR-024,FR-033,FR-037","M2","Integration")]
    public async Task LiveRegisteredPrimary_MeasuresDeclaredQuantityAndKeepsAsyncThreadAllocationUnavailable(string metric,string scenarioId)
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var scenario=(await service.ScenariosAsync(identity.Id,CancellationToken.None)).Scenarios.Single(s=>s.Id==scenarioId);
            var prepared=await new LabTools(service).Experiment(new CreatePlan(scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,
                OperationsPerRun:100,IndependentBlocks:true,IndependenceJustification:"Declared controlled fixture blocks.",PrimaryMetric:metric,PrimaryPolicy:Policy(metric)));
            Assert.True(prepared.Status=="ok",Wire.Serialize(prepared)); var plan=prepared.Data!.Value.Deserialize<ExperimentPlan>(Wire.Json)!;
            var grant=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,60,20);
            var complete=await ServiceTests.Wait(service,(await service.StartExecutionAsync(identity.Id,identity.Epoch,plan.Id,grant.Id,Wire.Id("primary-run"),CancellationToken.None)).Id);
            Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); var comparison=Assert.Single(await service.CompareAsync(complete.ResultId!));
            Assert.Equal(metric,comparison.PrimaryEffect!.Metric); Assert.False(comparison.PromotionEligible);
            if(scenario.IsAsync)
            { Assert.Equal(Availability.NotApplicable,comparison.PrimaryEffect.Availability); Assert.Null(comparison.PrimaryEffect.Effect); Assert.Equal(EffectClass.NotComparable,comparison.EffectClass); }
            else Assert.Equal(Availability.Available,comparison.PrimaryEffect.Availability);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
}
