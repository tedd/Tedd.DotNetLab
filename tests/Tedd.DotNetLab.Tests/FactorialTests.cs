using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class FactorialTests
{
    private static double[][] OracleArms(string name)
    {
        using var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics","golden.json")));
        return data.RootElement.GetProperty("factorial").GetProperty(name).GetProperty("arms").EnumerateArray()
            .Select(arm=>arm.EnumerateArray().Select(value=>value.GetDouble()).ToArray()).ToArray();
    }
    private static FactorialMetricBlock[] Blocks(double[][] arms)
    {
        var policy=ObjectiveTests.Cost(); var definition=Metrics.Define(policy.Metric);
        MetricValue Value(double value)=>new(policy.Metric,Availability.Available,value,policy.Unit,policy.ObservationKind,definition.Scope);
        return arms[0].Select((_,index)=>new FactorialMetricBlock(index.ToString(),index.ToString(),Value(arms[0][index]),Value(arms[1][index]),
            Value(arms[2][index]),Value(arms[3][index]),Enumerable.Range(0,4).Select(arm=>$"run-{index}-{arm}").ToArray())).ToArray();
    }
    [Theory,InlineData("absolute",ContrastScale.Absolute),InlineData("log",ContrastScale.Log),Case("UT-STAT-025","FR-036","M2","Unit")]
    public void FourArmInteraction_MatchesIndependentSciPyOracleOnDeclaredScale(string name,ContrastScale scale)
    {
        using var data=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics","golden.json")));
        var expected=data.RootElement.GetProperty("factorial").GetProperty(name);
        var estimate=FactorialEngine.Interaction(ObjectiveTests.Cost(),scale,new(FamilySize:6),Blocks(OracleArms(name)),true);
        Assert.Equal("usable",estimate.Quality); Assert.Equal(40,estimate.RunIds.Count);
        Assert.Equal(expected.GetProperty("contrast").GetDouble(),estimate.Contrast!.Value,11);
        Assert.Equal(expected.GetProperty("standard_error").GetDouble(),estimate.StandardError!.Value,11);
        Assert.Equal(expected.GetProperty("interval")[0].GetDouble(),estimate.Interval!.Lower,10);
        Assert.Equal(expected.GetProperty("interval")[1].GetDouble(),estimate.Interval.Upper,10);
        Assert.InRange(Math.Abs(Math.Log(estimate.DifferenceP!.Value)-Math.Log(expected.GetProperty("difference_p").GetDouble())),0,1e-8);
        Assert.Equal(.05/6,estimate.AlphaLocal,12);
        Assert.Equal(scale==ContrastScale.Log?"log_ratio_contrast":"ns/operation",estimate.Unit);
        if(scale==ContrastScale.Absolute) Assert.Equal(-11,estimate.Contrast.Value,10);
    }
    [Fact,Case("UT-EXP-009","FR-036","M2","Unit")]
    public void MissingDuplicateOrDependentArms_DoNotProduceInteractionInference()
    {
        var blocks=Blocks(OracleArms("absolute"));
        var incomplete=blocks.Select((block,index)=>index==0?block with { RunIds=block.RunIds.Take(3).ToArray() }:block).ToArray();
        Assert.Equal("invalid",FactorialEngine.Interaction(ObjectiveTests.Cost(),ContrastScale.Absolute,new(FamilySize:6),incomplete,true).Quality);
        var repeated=blocks.Select(block=>block with { ClusterId="one-session" }).ToArray();
        var result=FactorialEngine.Interaction(ObjectiveTests.Cost(),ContrastScale.Absolute,new(FamilySize:6),repeated,true);
        Assert.Equal(1,result.IndependentUnits); Assert.Null(result.Interval); Assert.Contains("dependent_cluster_rows",result.ReasonCodes);
        Assert.Throws<LabException>(()=>FactorialEngine.Interaction(ObjectiveTests.Cost(),ContrastScale.Log,new(FamilySize:3),blocks,true));
        Assert.Throws<LabException>(()=>FactorialEngine.Interaction(ObjectiveTests.Allocation(),ContrastScale.Log,new(FamilySize:6),blocks,true));
    }
    [Fact,Case("UT-EXP-010","FR-036,FR-039","M2","Unit")]
    public void StandaloneRegressionAndBeneficialCombination_AreSeparateRetainedEffects()
    {
        var blocks=Blocks(OracleArms("absolute"));
        MetricEffect Pair(Func<FactorialMetricBlock,MetricValue> baseline,Func<FactorialMetricBlock,MetricValue> candidate)=>
            ObjectiveEngine.Evaluate(ObjectiveTests.Cost(),new(FamilySize:6),blocks.Select(block=>new MetricPair(block.BlockId,block.ClusterId,
                baseline(block),candidate(block),block.RunIds[0],block.RunIds[3])).ToArray(),true);
        Assert.Equal(EffectClass.Regressed,Pair(block=>block.Value00,block=>block.Value10).EffectClass);
        Assert.Equal(EffectClass.Improved,Pair(block=>block.Value00,block=>block.Value11).EffectClass);
        Assert.Equal(EffectClass.Improved,Pair(block=>block.Value01,block=>block.Value11).EffectClass);
        Assert.Equal(-11,FactorialEngine.Interaction(ObjectiveTests.Cost(),ContrastScale.Absolute,new(FamilySize:6),blocks,true).Contrast!.Value,10);
    }
    [Fact,Case("UT-STAT-008","FR-033,FR-036","M2","Unit")]
    public void ConstantInteraction_RemainsDescriptive()
    {
        var blocks=Blocks([Enumerable.Repeat(100d,10).ToArray(),Enumerable.Repeat(104d,10).ToArray(),Enumerable.Repeat(97d,10).ToArray(),Enumerable.Repeat(90d,10).ToArray()]);
        var result=FactorialEngine.Interaction(ObjectiveTests.Cost(),ContrastScale.Absolute,new(FamilySize:6),blocks,true);
        Assert.Equal(-11,result.Contrast); Assert.Null(result.Interval); Assert.Contains("degenerate_variance",result.ReasonCodes);
    }
    [Fact,Case("IT-HAR-001","FR-033,FR-036,FR-062","M2","Integration")]
    public async Task PublicFourArmPlan_RandomizesCompleteBlocksAndPersistsAllFiveEffectsAcrossRestart()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            string resultId,executionId,artifactId;
            await using(var service=new LabService(workspace.Root))
            {
                var identity=service.Discover().Single(item=>item.Identity.ProcessId==target.Id).Identity;
                var scenario=(await service.ScenariosAsync(identity.Id,CancellationToken.None)).Scenarios.Single(item=>item.Id=="factorial-sum");
                var a=service.Candidate("A","A-build",[]); var b=service.Candidate("B","B-build",[]); var both=service.Candidate("A+B","joint-build",[a.Id,b.Id]);
                var design=new FactorialDesign("00","10","01","11",a.Id,b.Id,both.Id,new Dictionary<string,ContrastScale> { ["scenario.ns_per_operation"]=ContrastScale.Log });
                var tools=new LabTools(service);
                var reply=await tools.Experiment(new CreatePlan(scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,OperationsPerRun:1000,
                    IndependentBlocks:true,IndependenceJustification:"Declared independent complete laboratory blocks.",Analysis:new(FamilySize:6),Factorial:design));
                Assert.True(reply.Status=="ok",Wire.Serialize(reply)); var plan=reply.Data!.Value.Deserialize<ExperimentPlan>(Wire.Json)!;
                Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.Plan(plan with { Analysis=new(FamilySize:3) })).Code);
                var wrong=service.Candidate("wrong combination","wrong-build",[a.Id]);
                Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.Plan(plan with { Factorial=design with { CombinationId=wrong.Id } })).Code);
                var grant=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,120,40);
                var done=await ServiceTests.Wait(service,(await service.StartExecutionAsync(identity.Id,0,plan.Id,grant.Id,"four-arm",CancellationToken.None)).Id);
                Assert.True(done.State==OperationState.Completed,done.ErrorCode); executionId=done.ResultId!;
                var execution=service.Catalog.Get<ExecutionRecord>(executionId); Assert.Equal(40,execution.RunIds.Count);
                var comparison=await tools.Compare(new(executionId,IncludeFactorial:true)); Assert.True(comparison.Status=="ok",Wire.Serialize(comparison));
                var index=comparison.Data!.Value.Deserialize<FactorialIndex>(Wire.Json)!; resultId=index.Id; artifactId=index.ArtifactId;
                Assert.Equal(5,index.EffectVectorIds.Count); Assert.Equal(40,index.InputRunCount); Assert.False(index.PromotionEligible);
                var result=await service.ReadFactorialAsync(resultId); Assert.Equal(40,result.RunIds.Count);
                Assert.Equal(40,Assert.Single(result.Interactions).RunIds.Count); Assert.True(service.Catalog.IsPinned(artifactId));
                foreach(string id in index.EffectVectorIds.Values)
                {
                    var vector=await service.ReadObjectivesAsync(id); Assert.Equal(20,vector.Effects[0].RunIds.Count); Assert.False(vector.PromotionEligible);
                }
                Assert.Equal(index.Id,(await service.CompareFactorialAsync(executionId)).Id);
                var incomplete=execution with { Id=Wire.Id("incomplete-exec"),RunIds=execution.RunIds.Take(39).ToArray() };
                service.Catalog.Put(incomplete.Id,"execution",incomplete);
                Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.CompareFactorialAsync(incomplete.Id))).Code);
                var forbidden=await tools.Compare(new(executionId,IncludeObjectives:true,IncludeFactorial:true)); Assert.Equal("invalid_request",forbidden.Code);
                Assert.Throws<LabException>(()=>service.Catalog.Put(plan.Id,"plan",plan with { Factorial=design with { Variant10="changed" } },1));
            }
            await using var reopened=new LabService(workspace.Root);
            Assert.Equal(resultId,(await reopened.CompareFactorialAsync(executionId)).Id);
            reopened.Artifacts.Delete(reopened.Catalog.Get<Artifact>(artifactId));
            Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>reopened.ReadFactorialAsync(resultId))).Code);
        }
        finally { if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); } }
    }
}
