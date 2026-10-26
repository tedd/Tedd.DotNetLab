using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;
using System.Text.Json;

namespace Tedd.DotNetLab.Tests;

public sealed class QuantileWorkflowTests
{
    [Fact,Case("UT-STAT-021,UT-STAT-022","FR-033,FR-074","M2","Reference")]
    public async Task UnequalSessionClusters_MatchIndependentNumpyPercentileOracle()
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics","quantile-cluster-golden.json")));
        var root=fixture.RootElement;
        var clusters=Wire.Deserialize<QuantileCluster[]>(root.GetProperty("clusters").GetRawText());
        var blocks=Wire.Deserialize<PairedObservationBlock[]>(root.GetProperty("blocks").GetRawText());
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var design=Design() with { Clusters=clusters,ObservationsPerArmPerBlock=4,Quantile=root.GetProperty("quantile").GetDouble(),
            Seed=ulong.Parse(root.GetProperty("seed").GetString()!),Resamples=root.GetProperty("resamples").GetInt32() };
        var plan=service.CreateQuantilePlan(design); var source=await Artifact(service,Observations(plan) with { Blocks=blocks });
        var dataset=await service.RegisterQuantileObservationsAsync(plan.Id,source.Id);
        var completed=await ServiceTests.Wait(service,service.StartQuantileComparison(dataset.Id,"independent-reference").Id);
        Assert.Equal(OperationState.Completed,completed.State); var result=service.Catalog.Get<QuantileComparison>(completed.ResultId!);
        var expected=root.GetProperty("expected"); Assert.NotNull(result.Bootstrap);
        Assert.InRange(Math.Abs(result.Difference-expected.GetProperty("difference").GetDouble()),0,1e-9);
        Assert.InRange(Math.Abs(result.Bootstrap.Interval.Lower-expected.GetProperty("lower").GetDouble()),0,1e-9);
        Assert.InRange(Math.Abs(result.Bootstrap.Interval.Upper-expected.GetProperty("upper").GetDouble()),0,1e-9);
        Assert.Equal(expected.GetProperty("confidence").GetDouble(),result.Bootstrap.Interval.Coverage);
        Assert.Equal(12,result.IndependentClusters); Assert.Equal(24,dataset.Blocks);
    }
    private static QuantilePlanRequest Design(int clusters=10,int blocksPerCluster=1,bool independent=true)=>new("frame_time","fixed-workload/v1","individual-stopwatch/v1",
        Enumerable.Range(0,clusters).Select(i=>new QuantileCluster("session-"+i,Enumerable.Range(0,blocksPerCluster).Select(j=>$"block-{i}-{j}").ToArray())).ToArray(),
        2,.9,42,1000,.05,1,independent,independent?"Independent paired sessions declared before observation collection.":"",2,2);
    private static QuantileObservations Observations(QuantilePlan plan)=>new(plan.Id,plan.ContentHash,plan.Design.ObservationType,"ns",plan.Design.WorkloadIdentity,plan.Design.ObserverIdentity,
        plan.Design.Clusters.SelectMany(c=>c.Blocks).Select((id,i)=>new PairedObservationBlock(id,new[]{100d+i*2,104d+i*2},new[]{80d+i*1.7,85d+i*1.5})).ToArray());
    private static async Task<Artifact> Artifact(LabService service,QuantileObservations observations)
    {
        var artifact=await service.Artifacts.WriteJsonAsync(Wire.Id("quantile-fixture"),observations,"quantile-fixture/v1"); service.Catalog.Put(artifact.Id,"artifact",artifact); return artifact;
    }
    [Fact,Case("UT-STAT-021,UT-STAT-022","FR-033,FR-074","M2","Integration")]
    public async Task FrozenPairedQuantiles_VerifyOrderedInputsAndPersistBootstrapWithoutPValues()
    {
        using var workspace=new TestWorkspace(); string resultId,datasetId,sourceId;
        await using(var service=new LabService(workspace.Root))
        {
            var tools=new LabTools(service); var prepared=await tools.Experiment(new CreateQuantilePlan(Design(10,2))); Assert.Equal("ok",prepared.Status);
            var plan=Wire.Deserialize<QuantilePlan>(prepared.Data!.Value.GetRawText()); var observations=Observations(plan); var source=await Artifact(service,observations); sourceId=source.Id;
            var registered=await tools.Experiment(new RegisterQuantileObservations(plan.Id,source.Id)); Assert.Equal("ok",registered.Status);
            var dataset=Wire.Deserialize<QuantileDataset>(registered.Data!.Value.GetRawText()); datasetId=dataset.Id;
            Assert.Equal(20,dataset.Blocks); Assert.Equal(10,dataset.Clusters); Assert.Equal(80,dataset.RawObservations); Assert.True(service.Catalog.IsPinned(source.Id));
            var started=service.StartQuantileComparison(dataset.Id,"paired-quantiles"); var complete=await ServiceTests.Wait(service,started.Id);
            Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); resultId=complete.ResultId!;
            var result=service.Catalog.Get<QuantileComparison>(resultId); Assert.Equal(10,result.IndependentClusters); Assert.Equal(1000,result.Bootstrap!.Resamples);
            Assert.Equal(10,result.Bootstrap.Clusters); Assert.False(result.EquivalenceInferenceSupported||result.PromotionEligible);
            Assert.Null(result.DifferencePValue); Assert.Null(result.EquivalencePValue); Assert.Equal("ns",result.Unit);
            Assert.Equal("bootstrap_interval_supports_useful_improvement",result.Assessment); Assert.True(result.Bootstrap.Interval.Upper< -2);
            Assert.Equal(started.Id,service.StartQuantileComparison(dataset.Id,"paired-quantiles").Id);
            var repeated=await ServiceTests.Wait(service,service.StartQuantileComparison(dataset.Id,"repeat-fixed-seed").Id);
            Assert.Equal(result.Bootstrap,service.Catalog.Get<QuantileComparison>(repeated.ResultId!).Bootstrap);
            Assert.Equal(Wire.Hash(observations),Wire.Hash(Wire.Deserialize<QuantileObservations>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(source)))));
        }
        await using var reopened=new LabService(workspace.Root); Assert.NotNull(reopened.Catalog.Get<QuantileComparison>(resultId).Bootstrap);
        reopened.Artifacts.Delete(reopened.Catalog.Get<Artifact>(sourceId));
        var expired=await ServiceTests.Wait(reopened,reopened.StartQuantileComparison(datasetId,"expired-source").Id); Assert.Equal("artifact_expired",expired.ErrorCode);
    }
    [Theory,InlineData(1,true),InlineData(10,false),Case("UT-EXP-004,UT-STAT-023","FR-033","M2","Integration")]
    public async Task DependentOrTooFewClusters_RemainDescriptiveDespiteManyRows(int clusters,bool independent)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var design=Design(clusters,independent:independent) with { ObservationsPerArmPerBlock=50 };
        var plan=service.CreateQuantilePlan(design); var data=Observations(plan) with { Blocks=plan.Design.Clusters.SelectMany(c=>c.Blocks)
            .Select((id,i)=>new PairedObservationBlock(id,Enumerable.Range(0,50).Select(j=>100d+i+j).ToArray(),Enumerable.Range(0,50).Select(j=>80d+i+j).ToArray())).ToArray() };
        var source=await Artifact(service,data); var dataset=await service.RegisterQuantileObservationsAsync(plan.Id,source.Id);
        var completed=await ServiceTests.Wait(service,service.StartQuantileComparison(dataset.Id,"dependent").Id); Assert.Equal(OperationState.Completed,completed.State);
        var result=service.Catalog.Get<QuantileComparison>(completed.ResultId!); Assert.Null(result.Bootstrap); Assert.Equal("descriptive_only",result.Assessment);
        Assert.Contains("insufficient_independent_evidence",result.Reasons); Assert.Equal(clusters*100,result.RawObservations);
    }
    [Theory,InlineData("batch_average"),InlineData("wrong_hash"),InlineData("missing_block"),InlineData("wrong_unit"),InlineData("spoofed_provenance"),Case("UT-STAT-033,UT-MET-015","FR-033,FR-034","M2","Integration")]
    public async Task IncompatibleObservationSemanticsOrIncompleteBudget_AreRejected(string defect)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var plan=service.CreateQuantilePlan(Design()); var data=Observations(plan);
        data=defect switch { "batch_average"=>data with { ObservationType="batch_average" },"wrong_hash"=>data with { PlanHash=Wire.Hash("other") },
            "missing_block"=>data with { Blocks=data.Blocks.Take(9).ToArray() },"spoofed_provenance"=>data with { Provenance="verified_registered_harness_individual_observations/v1" },_=>data with { Unit="ms" } };
        var source=await Artifact(service,data);
        Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.RegisterQuantileObservationsAsync(plan.Id,source.Id))).Code);
        Assert.Empty(service.Catalog.List("quantile_dataset").Rows);
    }
    [Fact,Case("UT-STAT-008","FR-033","M2","Integration")]
    public async Task DegenerateResampling_DoesNotFabricateInferentialClassification()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var plan=service.CreateQuantilePlan(Design());
        var data=Observations(plan) with { Blocks=plan.Design.Clusters.SelectMany(c=>c.Blocks).Select(id=>new PairedObservationBlock(id,new[]{100d,100d},new[]{80d,80d})).ToArray() };
        var source=await Artifact(service,data); var dataset=await service.RegisterQuantileObservationsAsync(plan.Id,source.Id);
        var complete=await ServiceTests.Wait(service,service.StartQuantileComparison(dataset.Id,"degenerate").Id);
        var result=service.Catalog.Get<QuantileComparison>(complete.ResultId!); Assert.Equal("descriptive_only",result.Assessment); Assert.Contains("degenerate_bootstrap_distribution",result.Reasons);
    }
    [Fact,Case("UT-STAT-022","FR-016,FR-033","M2","Unit")]
    public void Bootstrap_CancellationAndNonfinitePoliciesAreExplicit()
    {
        var clusters=Enumerable.Range(0,10).Select(i=>new PairedCluster(i.ToString(),new[]{1d+i,2d+i},new[]{2d+i,3d+i})).ToArray();
        using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(()=>StatisticalEngine.ClusterQuantile(clusters,.9,42,1000,cancellation:cancellation.Token));
        Assert.Throws<LabException>(()=>StatisticalEngine.ClusterQuantile(clusters,double.NaN,42,1000));
    }
}
