using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class LiveBenchmarkTests
{
    private static readonly TargetIdentity Target=new("live-test",1,1,"live-test","10.0.15","x64");
    private static LiveBenchmarkPolicy FastPolicy => new(MaximumOperationsPerIteration:1024,TargetIterationMilliseconds:1,
        MeasurementIterations:6,MinimumWarmupIterations:6,MaximumWarmupIterations:8,OverheadIterations:5);
    private static Tedd.DotNetLab.Contracts.RunRequest Request(TargetIdentity target,string workspace="workspace")=>
        new("execution","run","block","live-test","baseline",1,0,true,true,target.Epoch,target,
            new("grant",workspace,target.Id,target.StartTimeUtcTicks,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddMinutes(1),10),
            DateTimeOffset.UtcNow.AddSeconds(30));

    [Fact,Case("UT-LIVE-BENCH-001","FR-037","M7","Unit")]
    public async Task CalibratesWarmsRetainsEveryStageAndCleansOnce()
    {
        var scenario=new Scenario(); var registry=new ScenarioRegistry(); registry.Register(scenario);
        var session=await new ScenarioRunner(registry,Target,"workspace").BenchmarkAsync(Request(Target),FastPolicy,CancellationToken.None);
        Assert.True(session.Correct,session.Failure); Assert.Equal(1,scenario.Prepared); Assert.Equal(1,scenario.Cleaned);
        Assert.Equal(6,session.Iterations.Count(i=>i.Phase=="actual")); Assert.Equal(5,session.Iterations.Count(i=>i.Phase=="overhead"));
        Assert.InRange(session.Iterations.Count(i=>i.Phase=="warmup"),6,8);
        var pilot=session.Iterations.Where(i=>i.Phase=="pilot").ToArray(); Assert.NotEmpty(pilot);
        Assert.Equal(session.OperationsPerIteration,pilot[^1].Measurement.CompletedOperations);
        Assert.All(session.Iterations,i=> { Assert.True(i.Measurement.Correct); Assert.Equal("input",i.Measurement.InputHash); });
        Assert.Equal(session.Iterations.Count(i=>i.Phase!="overhead"),scenario.Validated);
        Assert.Equal(scenario.Validated,scenario.ResetCount);
        Assert.Equal(6,LiveBenchmarkStatistics.Summarize(session,FastPolicy).Samples);
    }

    [Fact,Case("UT-LIVE-BENCH-002","FR-037","M7","Unit")]
    public async Task FailedOracleRetainsPartialObservationsAndNeverProducesStableSummary()
    {
        var scenario=new Scenario { FailAt=2 }; var registry=new ScenarioRegistry(); registry.Register(scenario);
        var session=await new ScenarioRunner(registry,Target,"workspace").BenchmarkAsync(Request(Target),FastPolicy,CancellationToken.None);
        Assert.False(session.Correct); Assert.Equal("scenario_failed",session.Failure); Assert.Equal(2,session.Iterations.Count);
        Assert.False(session.Iterations[^1].Measurement.Correct); Assert.Equal(1,scenario.Cleaned);
        Assert.Equal("invalid",LiveBenchmarkStatistics.Summarize(session,FastPolicy).Quality);
    }

    [Fact,Case("UT-LIVE-BENCH-003","FR-037","M7","Unit")]
    public async Task FixedCalibrationHonorsOperationBudgetAndUnjoinedWorkTaints()
    {
        var scenario=new Scenario(); var registry=new ScenarioRegistry(); registry.Register(scenario);
        var runner=new ScenarioRunner(registry,Target,"workspace");
        var policy=FastPolicy with { OperationsPerIteration=10,MaximumTotalOperations=15 };
        var session=await runner.BenchmarkAsync(Request(Target),policy,CancellationToken.None);
        Assert.Equal("resource_limit",session.Failure); Assert.Single(session.Iterations); Assert.DoesNotContain(session.Iterations,r=>r.Phase=="pilot");
        scenario.Unjoined=true;
        session=await runner.BenchmarkAsync(Request(Target),FastPolicy,CancellationToken.None);
        Assert.False(session.Quiescent); Assert.True(runner.Tainted);
    }

    [Fact,Case("UT-LIVE-BENCH-008","FR-037,FR-038","M7","Unit")]
    public async Task IgnoredCancellationAndFailedCleanupDoNotReleaseAHealthyTarget()
    {
        var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario=new Scenario { CompletionGate=release.Task }; var registry=new ScenarioRegistry(); registry.Register(scenario);
        var runner=new ScenarioRunner(registry,Target,"workspace"); using var cancellation=new CancellationTokenSource();
        var run=runner.BenchmarkAsync(Request(Target),FastPolicy,cancellation.Token);
        try
        {
            await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
            Assert.Equal("target_tainted",(await Assert.ThrowsAsync<LabException>(()=>run)).Code);
            Assert.Equal(0,scenario.Cleaned);
            Assert.Equal("target_tainted",(await Assert.ThrowsAsync<LabException>(()=>runner.BenchmarkAsync(Request(Target),FastPolicy,CancellationToken.None))).Code);
        }
        finally { release.TrySetResult(); await scenario.CleanupCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
        var broken=new Scenario { CleanupThrows=true }; var brokenRegistry=new ScenarioRegistry(); brokenRegistry.Register(broken);
        var brokenRunner=new ScenarioRunner(brokenRegistry,Target,"workspace");
        var failed=await brokenRunner.BenchmarkAsync(Request(Target),FastPolicy,CancellationToken.None);
        Assert.Equal("cleanup_failed",failed.Failure); Assert.False(failed.Quiescent); Assert.True(brokenRunner.Tainted);
    }

    [Fact,Case("IT-LIVE-BENCH-004","FR-037,FR-038","M7","Integration")]
    public async Task PipeCancellationAcknowledgesQuiescenceAndHoldsWholeSessionLease()
    {
        using var workspace=new TestWorkspace();
        var scenario=new Scenario { WaitForCancellation=true }; var registry=new ScenarioRegistry(); registry.Register(scenario);
        var host=new HarnessHost(registry,workspace.Root); using var hostCancellation=new CancellationTokenSource();
        var listener=host.ListenAsync(hostCancellation.Token); var client=new HarnessClient();
        using var cancellation=new CancellationTokenSource();
        try
        {
            var run=client.BenchmarkAsync(Request(host.Target,workspace.Root),FastPolicy,cancellation.Token);
            await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal("lease_conflict",(await Assert.ThrowsAsync<LabException>(()=>client.ExecuteAsync(Request(host.Target,workspace.Root),CancellationToken.None))).Code);
            cancellation.Cancel(); var session=await run.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("cancelled",session.Failure); Assert.True(session.Quiescent); Assert.Equal(1,scenario.Cleaned);
            scenario.WaitForCancellation=false;
            Assert.True((await client.ExecuteAsync(Request(host.Target,workspace.Root),CancellationToken.None)).Correct);
        }
        finally { cancellation.Cancel(); hostCancellation.Cancel(); await listener; }
    }

    [Fact,Case("UT-LIVE-BENCH-005","FR-052","M7","Unit")]
    public void StatisticsDetectDriftNoiseAndTimerFloorWithoutSubtractingOrClaimingConfidence()
    {
        var policy=FastPolicy with { OperationsPerIteration=1000,MaximumOperationsPerIteration=1000 };
        var stable=Synthetic(policy,[2000,2000,2000,2000,2000,2000]);
        var summary=LiveBenchmarkStatistics.Summarize(stable,policy);
        Assert.Equal("stable_live_observation",summary.Quality); Assert.Equal(2000,summary.MeanNanoseconds); Assert.Equal(2,summary.ThreadAllocatedBytesPerOperation);
        var noisy=LiveBenchmarkStatistics.Summarize(Synthetic(policy,[1000,1200,1000,3000,5000,4000]),policy);
        Assert.Contains("measurement_drift",noisy.Warnings); Assert.Contains("high_variability",noisy.Warnings);
        Assert.Equal("unstable",noisy.Quality);
        var floor=LiveBenchmarkStatistics.Summarize(Synthetic(policy,[.1,.1,.1,.1,.1,.1]),policy);
        Assert.Contains("timer_floor_significant",floor.Warnings); Assert.Equal(.1,floor.MedianNanoseconds);
        Assert.Equal("invalid",LiveBenchmarkStatistics.Summarize(stable with { Iterations=stable.Iterations.Take(stable.Iterations.Count-1).ToArray() },policy).Quality);
        Assert.Throws<LabException>(()=>LiveBenchmarkStatistics.Summarize(stable with { PolicyHash="different" },policy));
    }

    [Theory,Case("UT-LIVE-BENCH-009","FR-052","M7","Unit")]
    [InlineData("ordinal")]
    [InlineData("phase_order")]
    [InlineData("clock")]
    [InlineData("completed_work")]
    [InlineData("allocation")]
    [InlineData("total_budget")]
    public void StatisticsRejectMalformedEvidence(string defect)
    {
        var policy=FastPolicy with { OperationsPerIteration=1000,MaximumOperationsPerIteration=1000,
            MaximumTotalOperations=defect=="total_budget"?11999:12000 };
        var session=Synthetic(policy,[2000,2000,2000,2000,2000,2000]);
        var rows=session.Iterations.ToArray();
        var last=rows[^1];
        rows[^1]=defect switch
        {
            "ordinal"=>last with { Ordinal=rows[^2].Ordinal },
            "phase_order"=>last with { Phase="warmup" },
            "clock"=>last with { Measurement=last.Measurement with { Frequency=0 } },
            "completed_work"=>last with { Measurement=last.Measurement with { CompletedOperations=999 } },
            "allocation"=>last with { Measurement=last.Measurement with { ProcessAllocationBytes=-1 } },
            _=>last
        };
        Assert.Equal("invalid_measurement",Assert.Throws<LabException>(()=>
            LiveBenchmarkStatistics.Summarize(session with { Iterations=rows },policy)).Code);
    }

    [Fact,Case("UT-LIVE-BENCH-010","FR-052","M7","Unit")]
    public void StatisticsNormalizeCostsAndDoNotInventMissingTelemetry()
    {
        var policy=FastPolicy with { OperationsPerIteration=1000,MaximumOperationsPerIteration=1000 };
        var session=Synthetic(policy,[1000,2000,3000,4000,5000,6000]);
        var summary=LiveBenchmarkStatistics.Summarize(session,policy);
        Assert.Equal(3500,summary.MeanNanoseconds);
        Assert.Equal(3500,summary.MedianNanoseconds);
        Assert.Equal(Math.Sqrt(3_500_000),summary.StandardDeviationNanoseconds!.Value,9);
        Assert.Equal(2.5,summary.DriftRatio);
        Assert.Equal(3,summary.ProcessAllocatedBytesPerOperation);
        Assert.Equal(4,summary.TelemetryPerOperation["cpu.thread_cycles"]);
        Assert.Equal(.001,summary.TelemetryPerOperation["gc.gen0_collections"]);

        var rows=session.Iterations.ToArray();
        var last=rows[^1];
        rows[^1]=last with { Measurement=last.Measurement with { EndThreadId=2,Telemetry=null } };
        var partial=LiveBenchmarkStatistics.Summarize(session with { Iterations=rows,WarmupConverged=false },policy);
        Assert.Null(partial.ThreadAllocatedBytesPerOperation);
        Assert.Empty(partial.TelemetryPerOperation);
        Assert.Contains("thread_changed",partial.Warnings);
        Assert.Contains("warmup_not_converged",partial.Warnings);
    }

    private static LiveBenchmarkSession Synthetic(LiveBenchmarkPolicy policy,double[] values)
    {
        var rows=new List<LiveBenchmarkIteration>();
        void Add(string phase,double nanoseconds)
        {
            int n=rows.Count;
            var measurement=new Measurement("execution","run-"+n,"block","baseline",0,(long)nanoseconds,1_000_000_000,
                1000,3000,2000,1,1,true,null,"input","1",Telemetry:new(1,0,0,0,0,0,0,ThreadCpuCycles:4000));
            rows.Add(new(phase,n,measurement));
        }
        for(int i=0;i<6;i++) Add("warmup",2_000_000);
        for(int i=0;i<5;i++) Add("overhead",10);
        foreach(double value in values) Add("actual",value*1000);
        return new("live-calibrated-batches/v1",Wire.Hash(policy),1000,true,true,true,null,rows);
    }

    [Fact,Case("IT-LIVE-BENCH-006","FR-037,FR-052,FR-074","M7","Integration")]
    public async Task PersistentProcessMeasurementsGroupByVariantAndSurviveControllerRestart()
    {
        using var workspace=new TestWorkspace(); using var process=await CaptureTests.StartTarget(workspace.Root);
        string baselineId,candidateId,failedId;
        try
        {
            await using(var service=new LabService(workspace.Root,collectControllerResources:false))
            {
                var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
                var grant=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,120,10);
                var baseline=await service.CreateLiveBenchmarkPlanAsync(target.Id,0,"cpu-sum",policy:FastPolicy);
                var b=await Measure(service,baseline,grant); baselineId=b.Id;
                var candidate=await service.CreateLiveBenchmarkPlanAsync(target.Id,0,"cpu-sum","candidate",FastPolicy,calibrationResultId:b.Id);
                var c=await Measure(service,candidate,grant); candidateId=c.Id;
                var repeat=await Measure(service,candidate,grant);
                Assert.Equal(b.OperationsPerIteration,c.OperationsPerIteration);
                Assert.NotEqual(b.Modification.Id,c.Modification.Id);
                var groups=await service.AggregateLiveBenchmarksAsync([b.Id,c.Id,repeat.Id]);
                Assert.Equal(2,groups.Count);
                var group=Assert.Single(groups,g=>g.ModificationId==c.Modification.Id);
                Assert.Equal(2,group.Runs);
                Assert.Equal(1,group.ProcessSessions);
                Assert.Equal(c.Summary!.Samples+repeat.Summary!.Samples,group.Samples);
                double first=c.Summary.MeanNanoseconds!.Value,second=repeat.Summary.MeanNanoseconds!.Value;
                Assert.Equal((first+second)/2,group.MeanNanoseconds);
                Assert.Equal(Math.Abs(first-second)/Math.Sqrt(2),group.RunMeanStandardDeviationNanoseconds!.Value,8);
                var comparison=await service.CompareLiveBenchmarksAsync([b.Id],[c.Id,repeat.Id]);
                Assert.Equal(group.MeanNanoseconds/b.Summary!.MeanNanoseconds!.Value,comparison.Ratio);
                Assert.False(comparison.PromotionEligible);
                await Assert.ThrowsAsync<LabException>(()=>service.AggregateLiveBenchmarksAsync([b.Id,b.Id]));
                var incompatible=await service.CreateLiveBenchmarkPlanAsync(target.Id,0,"cpu-sum","candidate",
                    FastPolicy with { OperationsPerIteration=b.OperationsPerIteration==1?2:1 });
                var wrong=await Measure(service,incompatible,grant);
                await Assert.ThrowsAsync<LabException>(()=>service.CompareLiveBenchmarksAsync([b.Id],[wrong.Id]));
                var limited=await service.CreateLiveBenchmarkPlanAsync(target.Id,0,"cpu-sum",policy:FastPolicy with { OperationsPerIteration=1,MaximumTotalOperations=2 });
                var failed=await ServiceTests.Wait(service,(await service.StartLiveBenchmarkAsync(limited.Id,grant.Id,"limited")).Id);
                Assert.Equal(OperationState.Failed,failed.State); failedId=failed.ResultId!;
                var partial=service.Catalog.Get<LiveBenchmarkResult>(failedId);
                Assert.Equal("resource_limit",partial.Failure); Assert.Equal("invalid",partial.Summary!.Quality); Assert.NotNull(partial.ArtifactId);
                Assert.Equal(failed.Id,(await service.StartLiveBenchmarkAsync(limited.Id,grant.Id,"limited")).Id);
                await Assert.ThrowsAsync<LabException>(()=>service.AggregateLiveBenchmarksAsync([failedId]));
                var response=await new LabTools(service).Query(new QueryLiveBenchmarkSummary([b.Id,c.Id])); Assert.Equal("ok",response.Status);
            }
            await using(var reopened=new LabService(workspace.Root,collectControllerResources:false))
            {
                Assert.Equal(2,(await reopened.AggregateLiveBenchmarksAsync([baselineId,candidateId])).Count);
                var partial=reopened.Catalog.Get<LiveBenchmarkResult>(failedId);
                var artifact=reopened.Catalog.Get<Artifact>(partial.ArtifactId!);
                Assert.True(File.Exists(await reopened.Artifacts.VerifiedPathAsync(artifact,CancellationToken.None)));
            }
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }

    [Fact,Case("IT-LIVE-BENCH-007","FR-031,FR-037,FR-052,FR-074","M7","Integration")]
    public async Task LiveRejitBindsEachModificationRejectsStaleEpochAndAggregatesSeparately()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root,collectControllerResources:false);
            var prepared=await CompilationControlTests.Prepare(service,process.Id);
            var control=await CompilationAdmissionTests.Control(service,prepared.Target,prepared.Candidate);
            var candidate=await CompilationAdmissionTests.Candidate(service,prepared.InspectionId,prepared.Candidate.OriginalIlHash);
            var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,300,20);
            var originalGrant=service.IssueMutationGrant(prepared.Target.Id,prepared.Candidate.Id,300,4);
            var changedGrant=service.IssueMutationGrant(prepared.Target.Id,candidate.Id,300,4);
            async Task<MutationOutcome> Apply(CilCandidateRecord selected,Grant mutation,long epoch)
            {
                var op=await service.ApplyCilAsync(prepared.Target.Id,epoch,selected.Id,mutation.Id,execute.Id,"native-value",Wire.Id("live-apply"));
                var done=await ServiceTests.Wait(service,op.Id); Assert.True(done.State==OperationState.Completed,Wire.Serialize(done));
                return service.Catalog.Get<MutationOutcome>(done.ResultId!);
            }
            async Task<MutationOutcome> Restore(MutationOutcome applied,Grant mutation)
            {
                var op=await service.RestoreCilAsync(applied.Id[..^8],applied.Epoch,mutation.Id,execute.Id,Wire.Id("live-restore"));
                var done=await ServiceTests.Wait(service,op.Id); Assert.True(done.State==OperationState.Completed,Wire.Serialize(done));
                return service.Catalog.Get<MutationOutcome>(done.ResultId!);
            }
            var original=await Apply(prepared.Candidate,originalGrant,control.RestoredEpoch);
            var policy=FastPolicy with { MaximumOperationsPerIteration=1_000_000 };
            var baselinePlan=await service.CreateLiveBenchmarkPlanAsync(prepared.Target.Id,original.Epoch,"native-value",policy:policy,
                mutationId:original.Id[..^8],compilationControlId:control.Id);
            var baseline=await Measure(service,baselinePlan,execute);
            var restored=await Restore(original,originalGrant);
            var changed=await Apply(candidate,changedGrant,restored.Epoch);
            Assert.Equal("stale_epoch",(await Assert.ThrowsAsync<LabException>(()=>service.StartLiveBenchmarkAsync(baselinePlan.Id,execute.Id,"stale"))).Code);
            var changedPlan=await service.CreateLiveBenchmarkPlanAsync(prepared.Target.Id,changed.Epoch,"native-value",policy:policy,
                calibrationResultId:baseline.Id,mutationId:changed.Id[..^8],compilationControlId:control.Id);
            var first=await Measure(service,changedPlan,execute); var second=await Measure(service,changedPlan,execute);
            Assert.Equal(candidate.Id,first.Modification.CandidateId); Assert.Equal(candidate.BodyHash,first.Modification.BodyHash);
            Assert.Equal(changed.Id[..^8],first.Modification.MutationId);
            var reverted=await Restore(changed,changedGrant);
            var reapplied=await Apply(candidate,changedGrant,reverted.Epoch);
            var reappliedPlan=await service.CreateLiveBenchmarkPlanAsync(prepared.Target.Id,reapplied.Epoch,"native-value",policy:policy,
                calibrationResultId:baseline.Id,mutationId:reapplied.Id[..^8],compilationControlId:control.Id);
            var third=await Measure(service,reappliedPlan,execute);
            Assert.Equal(first.Modification.Id,third.Modification.Id); Assert.NotEqual(first.Modification.MutationId,third.Modification.MutationId);
            var groups=await service.AggregateLiveBenchmarksAsync([baseline.Id,first.Id,second.Id,third.Id]);
            Assert.Equal(2,groups.Count);
            var modified=groups.Single(g=>g.Modification.CandidateId==candidate.Id);
            Assert.Equal(3,modified.Runs); Assert.Equal(2,modified.Observations.Select(o=>o.MutationId).Distinct().Count());
            Assert.Null(modified.Modification.MutationId);
            var contrast=await service.CompareLiveBenchmarksAsync([baseline.Id],[first.Id,second.Id,third.Id]);
            Assert.True(contrast.Ratio>0); Assert.False(contrast.PromotionEligible);
            await Restore(reapplied,changedGrant);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }

    private static async Task<LiveBenchmarkResult> Measure(LabService service,LiveBenchmarkPlan plan,Grant grant)
    {
        var operation=await service.StartLiveBenchmarkAsync(plan.Id,grant.Id,Wire.Id("measure"));
        var done=await ServiceTests.Wait(service,operation.Id);
        Assert.True(done.State==OperationState.Completed,Wire.Serialize(done));
        return service.Catalog.Get<LiveBenchmarkResult>(done.ResultId!);
    }

    private sealed class Scenario : ISynchronousScenario
    {
        private CancellationToken cancellation;
        public int Prepared,Cleaned,Validated,ResetCount,Calls;
        public int FailAt=int.MaxValue;
        public bool Unjoined,WaitForCancellation,CleanupThrows;
        public Task? CompletionGate;
        public TaskCompletionSource Entered { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CleanupCompleted { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ScenarioDescriptor Describe()=>new("live-test","1","input",ResetLevel.LogicalState,ThreadAffinity.Any,false,"operation",["baseline"]);
        public void Prepare(ScenarioContext context) { Prepared++; cancellation=context.Cancellation; }
        public void Reset(ResetContext context)=>ResetCount++;
        public long ExecuteBatch(int count)
        {
            Calls++; Entered.TrySetResult();
            CompletionGate?.GetAwaiter().GetResult();
            if(WaitForCancellation) { cancellation.WaitHandle.WaitOne(); cancellation.ThrowIfCancellationRequested(); }
            Thread.SpinWait(count*10); return count;
        }
        public ValidationResult Validate() { Validated++; return new(Calls!=FailAt,Quiescent:!Unjoined); }
        public void Cleanup() { Cleaned++; CleanupCompleted.TrySetResult(); if(CleanupThrows) throw new InvalidOperationException("cleanup failed"); }
    }
}
