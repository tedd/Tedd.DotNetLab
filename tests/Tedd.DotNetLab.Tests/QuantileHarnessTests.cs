using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using System.Diagnostics;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class QuantileHarnessTests
{
    private static async Task<QuantilePlan> Prepare(LabService service,Process process,int clusters=10,int blocks=1)
    {
        var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
        var scenario=(await service.ScenariosAsync(target.Id,CancellationToken.None)).Scenarios.Single(s=>s.Id=="cpu-sum");
        var baseline=service.CreateIndividualLatencyPlan(scenario.Id,scenario.Version,scenario.InputHash,"baseline",4,1);
        var candidate=service.CreateIndividualLatencyPlan(scenario.Id,scenario.Version,scenario.InputHash,"candidate",4,1);
        return service.CreateQuantilePlan(new("individual_operation_latency","cpu-sum/v1","harness_sync_individual_stopwatch/v1",
            Enumerable.Range(0,clusters).Select(i=>new QuantileCluster("session-"+i,Enumerable.Range(0,blocks).Select(j=>$"block-{i}-{j}").ToArray())).ToArray(),
            4,.9,71337,1000,IndependentClusters:true,IndependenceJustification:"Distinct process sessions with paired arms and frozen randomized order.",
            HarnessPlans:new(baseline.Id,candidate.Id)));
    }
    private static async Task<QuantileRunPair[]> Collect(LabService service,QuantilePlan plan,Process process,int cluster,bool reverseOrder=false)
    {
        var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity; var declared=plan.Design.Clusters[cluster];
        var grant=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,120,declared.Blocks.Count*2);
        var blocks=new List<QuantileRunPair>();
        foreach(string block in declared.Blocks)
        {
            var runs=new Dictionary<string,string>();
            var order=plan.HarnessBinding!.Order.Single(o=>o.BlockId==block).Arms;
            foreach(string arm in reverseOrder?order.Reverse():order)
            {
                string planId=arm=="baseline"?plan.HarnessBinding.BaselinePlan.Id:plan.HarnessBinding.CandidatePlan.Id;
                var operation=await service.StartIndividualLatenciesAsync(target.Id,target.Epoch,planId,grant.Id,Wire.Id("quantile-arm"),CancellationToken.None);
                var complete=await ServiceTests.Wait(service,operation.Id); Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete));
                runs.Add(arm,complete.ResultId!);
            }
            blocks.Add(new(block,runs["baseline"],runs["candidate"]));
        }
        return blocks.ToArray();
    }
    [Theory,InlineData("reverse_order"),InlineData("observer_mismatch"),InlineData("cancelled"),Case("UT-EXP-006,UT-STAT-033","FR-031,FR-032,FR-033,FR-037","M2","Integration")]
    public async Task ActualOrderObserverAndCancellationRemainAdmissionGates(string defect)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); using var process=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            var plan=await Prepare(service,process,1);
            if(defect=="observer_mismatch") plan=service.CreateQuantilePlan(plan.Design with { ObserverIdentity="harness_async_individual_stopwatch/v1" });
            var pairs=await Collect(service,plan,process,0,defect=="reverse_order");
            if(defect=="cancelled")
            {
                using var cancellation=new CancellationTokenSource(); cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>service.RegisterQuantileHarnessRunsAsync(plan.Id,pairs,cancellation.Token));
            }
            else Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.RegisterQuantileHarnessRunsAsync(plan.Id,pairs))).Code);
            Assert.Empty(service.Catalog.List("quantile_dataset").Rows);
        }
        finally { await Stop(process); }
    }
    private static async Task Stop(Process process)
    { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }

    [Fact,Case("UT-STAT-021,UT-STAT-023,IT-HAR-001","FR-032,FR-033,FR-037","M2","Integration")]
    public async Task TenCollectedSessions_BindExactTicksAndRetainVerifiedQuantileProvenanceAfterRestart()
    {
        using var workspace=new TestWorkspace(); string datasetId; string sourceMeasurementId;
        await using(var service=new LabService(workspace.Root))
        {
            using var first=await CaptureTests.StartTarget(workspace.Root); var pairs=new List<QuantileRunPair>();
            QuantilePlan plan;
            try { plan=await Prepare(service,first,blocks:2); pairs.AddRange(await Collect(service,plan,first,0)); }
            finally { await Stop(first); }
            for(int i=1;i<10;i++)
            {
                using var process=await CaptureTests.StartTarget(workspace.Root);
                try { pairs.AddRange(await Collect(service,plan,process,i)); } finally { await Stop(process); }
            }
            var response=await new LabTools(service).Experiment(new RegisterQuantileHarnessRuns(plan.Id,pairs)); Assert.True(response.Status=="ok",Wire.Serialize(response));
            var dataset=Wire.Deserialize<QuantileDataset>(response.Data!.Value.GetRawText()); datasetId=dataset.Id;
            Assert.Equal(20,dataset.Blocks); Assert.Equal(10,dataset.Clusters); Assert.Equal(160,dataset.RawObservations); Assert.NotNull(dataset.HarnessEvidence);
            var manifest=Wire.Deserialize<QuantileHarnessManifest>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(dataset.HarnessEvidence!.ArtifactId))));
            Assert.Equal(20,manifest.Blocks.Count); Assert.Equal(plan.ContentHash,manifest.PlanHash);
            var data=Wire.Deserialize<QuantileObservations>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(dataset.ArtifactId))));
            sourceMeasurementId=manifest.Blocks[0].Baseline.ArtifactId;
            var measured=Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(sourceMeasurementId))));
            Assert.Equal(measured.IndividualLatencies!.ElapsedTicks.Select(t=>t*(1e9/measured.Frequency)),data.Blocks[0].Baseline);
            Assert.True(service.Catalog.IsPinned(sourceMeasurementId)); Assert.True(service.Catalog.IsPinned(dataset.HarnessEvidence.ArtifactId));
            var complete=await ServiceTests.Wait(service,service.StartQuantileComparison(dataset.Id,"collected-comparison").Id);
            Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); var result=service.Catalog.Get<QuantileComparison>(complete.ResultId!);
            Assert.Equal(data.Provenance,result.ObservationProvenance); Assert.NotNull(result.Bootstrap); Assert.Equal(10,result.IndependentClusters);
            Assert.DoesNotContain("supplied_observations_not_capture_certified",result.Reasons); Assert.False(result.PromotionEligible); Assert.Null(result.DifferencePValue);
            Assert.Equal(StatisticalEngine.Quantile(data.Blocks.SelectMany(b=>b.Baseline).ToArray(),plan.Design.Quantile),result.BaselineQuantile);
        }
        await using var reopened=new LabService(workspace.Root);
        var retained=await ServiceTests.Wait(reopened,reopened.StartQuantileComparison(datasetId,"restart-reverification").Id);
        Assert.True(retained.State==OperationState.Completed,Wire.Serialize(retained));
        var source=reopened.Catalog.Get<Artifact>(sourceMeasurementId); string path=await reopened.Artifacts.VerifiedPathAsync(source);
        await File.WriteAllTextAsync(path,"tampered source");
        var corrupt=await ServiceTests.Wait(reopened,reopened.StartQuantileComparison(datasetId,"source-corruption").Id);
        Assert.Equal(OperationState.Failed,corrupt.State); Assert.Equal("artifact_corrupt",corrupt.ErrorCode);
    }
    [Fact,Case("UT-EXP-004,UT-STAT-023","FR-032,FR-033","M2","Integration")]
    public async Task OneProcessCannotBeRelabeledAsTwoIndependentClusters()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); using var process=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            var plan=await Prepare(service,process,2); var pairs=(await Collect(service,plan,process,0)).Concat(await Collect(service,plan,process,1)).ToArray();
            var error=await Assert.ThrowsAsync<LabException>(()=>service.RegisterQuantileHarnessRunsAsync(plan.Id,pairs));
            Assert.Equal("insufficient_independent_evidence",error.Code); Assert.Empty(service.Catalog.List("quantile_dataset").Rows);
        }
        finally { await Stop(process); }
    }
    [Fact,Case("UT-EXP-006,UT-STAT-033","FR-031,FR-032,FR-033","M2","Integration")]
    public async Task PreFreezeRunsDuplicateRunsAndMismatchedPairsCannotEnterCollectedDataset()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); using var first=await CaptureTests.StartTarget(workspace.Root);
        var pairs=new List<QuantileRunPair>(); QuantilePlan plan;
        try { plan=await Prepare(service,first,2); pairs.AddRange(await Collect(service,plan,first,0)); } finally { await Stop(first); }
        using var second=await CaptureTests.StartTarget(workspace.Root);
        try { pairs.AddRange(await Collect(service,plan,second,1)); } finally { await Stop(second); }
        foreach(var invalid in new[]
        {
            pairs.Take(1).ToArray(),
            new[]{pairs[0],pairs[1] with { CandidateRunId=pairs[0].CandidateRunId }},
            new[]{pairs[0] with { CandidateRunId=pairs[1].CandidateRunId },pairs[1] with { CandidateRunId=pairs[0].CandidateRunId }},
            pairs.Select(p=>p with { BaselineRunId=p.CandidateRunId,CandidateRunId=p.BaselineRunId }).ToArray()
        })
            Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.RegisterQuantileHarnessRunsAsync(plan.Id,invalid))).Code);
        var later=service.CreateQuantilePlan(plan.Design);
        Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.RegisterQuantileHarnessRunsAsync(later.Id,pairs))).Code);
        Assert.Empty(service.Catalog.List("quantile_dataset").Rows);
    }
    [Fact,Case("UT-STAT-033,UT-MET-015","FR-020,FR-033","M2","Integration")]
    public async Task FrozenArmBudgetAndObserverCannotBeReplacedWithSuppliedArrays()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); using var process=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            var plan=await Prepare(service,process,1);
            Assert.Equal("not_comparable",Assert.Throws<LabException>(()=>service.CreateQuantilePlan(plan.Design with { ObservationsPerArmPerBlock=5 })).Code);
            Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.CreateQuantilePlan(plan.Design with { ObserverIdentity="batch-stopwatch/v1" })).Code);
            Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.CreateQuantilePlan(plan.Design with { ObservationType="frame_time" })).Code);
            var data=new QuantileObservations(plan.Id,plan.ContentHash,plan.Design.ObservationType,"ns",plan.Design.WorkloadIdentity,plan.Design.ObserverIdentity,
                [new("block-0-0",new double[]{1,2,3,4},new double[]{1,2,3,4})]);
            var artifact=await service.Artifacts.WriteJsonAsync("spoof",data,"fixture"); service.Catalog.Put(artifact.Id,"artifact",artifact);
            Assert.Equal("invalid_plan",(await Assert.ThrowsAsync<LabException>(()=>service.RegisterQuantileObservationsAsync(plan.Id,artifact.Id))).Code);
        }
        finally { await Stop(process); }
    }
}
