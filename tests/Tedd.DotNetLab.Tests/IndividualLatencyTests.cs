using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Mcp;
using Xunit;
using RunRequest=Tedd.DotNetLab.Contracts.RunRequest;

namespace Tedd.DotNetLab.Tests;

public sealed class IndividualLatencyTests
{
    private static readonly TargetIdentity Target=new("individual-test",1,1,"test","10.0.12","X64");
    private static RunRequest Request(string scenario,bool individual=true)=>new("execution","run","block",scenario,"baseline",5,0,true,false,0,Target,
        new("grant","workspace",Target.Id,1,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddMinutes(1),10),DateTimeOffset.UtcNow.AddMinutes(1),RetainIndividualLatencies:individual);
    private sealed class Sync : ISynchronousScenario
    {
        public int Calls,CleanupCalls; public bool FailCompleted;
        public ScenarioDescriptor Describe()=>new("sync-individual","1","input",ResetLevel.LogicalState,ThreadAffinity.Any,false,"operation",["baseline"]);
        public void Prepare(ScenarioContext context) { } public void Reset(ResetContext context) { }
        public long ExecuteBatch(int count) { Calls++; return FailCompleted?0:count; }
        public ValidationResult Validate()=>new(true); public void Cleanup()=>CleanupCalls++;
    }
    private sealed class Async : IAsynchronousScenario
    {
        public int Calls,Completed,CleanupCalls;
        public ScenarioDescriptor Describe()=>new("async-individual","1","input",ResetLevel.LogicalState,ThreadAffinity.Any,true,"operation",["baseline"]);
        public ValueTask PrepareAsync(ScenarioContext context)=>ValueTask.CompletedTask; public ValueTask ResetAsync(ResetContext context)=>ValueTask.CompletedTask;
        public async ValueTask<long> ExecuteBatchAsync(int count,CancellationToken token) { Calls++; await Task.Delay(2,token); Completed++; return count; }
        public ValueTask<ValidationResult> ValidateAsync()=>ValueTask.FromResult(new ValidationResult(Calls==Completed));
        public ValueTask CleanupAsync() { CleanupCalls++; return ValueTask.CompletedTask; }
    }
    [Fact,Case("UT-MET-015","FR-020,FR-033,FR-037","M2","Unit")]
    public async Task SyncIndividualTiming_RetainsOneTickDurationPerCompletedOperation()
    {
        var scenario=new Sync(); var registry=new ScenarioRegistry(); registry.Register(scenario);
        var result=await new ScenarioRunner(registry,Target,"workspace").ExecuteAsync(Request(scenario.Describe().Id),CancellationToken.None);
        Assert.Equal(5,scenario.Calls); Assert.Equal(1,scenario.CleanupCalls); Assert.Equal(5,result.CompletedOperations);
        Assert.Equal(5,result.IndividualLatencies!.ElapsedTicks.Count); Assert.All(result.IndividualLatencies.ElapsedTicks,t=>Assert.True(t>=0));
        Assert.Equal("harness_sync_individual_stopwatch/v1",result.IndividualLatencies.ObserverIdentity);
        Assert.True(result.IndividualLatencies.ElapsedTicks.Sum()<=result.EndTicks-result.StartTicks);
    }
    [Fact,Case("UT-HAR-006","FR-020,FR-037","M2","Unit")]
    public async Task AsyncIndividualTiming_AwaitsEveryOperationAndKeepsProcessAllocationScope()
    {
        var scenario=new Async(); var registry=new ScenarioRegistry(); registry.Register(scenario);
        var result=await new ScenarioRunner(registry,Target,"workspace").ExecuteAsync(Request(scenario.Describe().Id),CancellationToken.None);
        Assert.Equal(5,scenario.Completed); Assert.Equal(1,scenario.CleanupCalls); Assert.Null(result.ThreadAllocationBytes);
        Assert.Equal("harness_async_individual_stopwatch/v1",result.IndividualLatencies!.ObserverIdentity);
        Assert.Equal(5,result.IndividualLatencies.ElapsedTicks.Count); Assert.All(result.IndividualLatencies.ElapsedTicks,t=>Assert.True(t>0));
    }
    [Fact,Case("UT-MET-016","FR-037","M2","Unit")]
    public async Task IncompleteIndividualOperation_RejectsVectorAndCleansOnce()
    {
        var scenario=new Sync { FailCompleted=true }; var registry=new ScenarioRegistry(); registry.Register(scenario);
        Assert.Equal("scenario_failed",(await Assert.ThrowsAsync<LabException>(()=>new ScenarioRunner(registry,Target,"workspace").ExecuteAsync(Request(scenario.Describe().Id),CancellationToken.None))).Code);
        Assert.Equal(1,scenario.CleanupCalls);
    }
    [Fact,Case("UT-HAR-001","FR-037","M2","Unit")]
    public async Task DefaultBatchTiming_KeepsSingleBatchAndOmitsIndividualWireFields()
    {
        var scenario=new Sync(); var registry=new ScenarioRegistry(); registry.Register(scenario);
        var request=Request(scenario.Describe().Id,false); var measured=await new ScenarioRunner(registry,Target,"workspace").ExecuteAsync(request,CancellationToken.None);
        Assert.Equal(1,scenario.Calls); Assert.Null(measured.IndividualLatencies);
        Assert.DoesNotContain("retain_individual_latencies",Wire.Serialize(request)); Assert.DoesNotContain("individual_latencies",Wire.Serialize(measured));
        Assert.Equal("resource_limit",(await Assert.ThrowsAsync<LabException>(()=>new ScenarioRunner(registry,Target,"workspace").ExecuteAsync(
            request with { RetainIndividualLatencies=true,OperationCount=10001 },CancellationToken.None))).Code);
    }
    [Fact,Case("IT-HAR-001","FR-020,FR-033,FR-037","M2","Integration")]
    public async Task AuthorizedIndividualRun_SealsVectorAndRetainsBoundedIndexAcrossRestart()
    {
        using var workspace=new TestWorkspace(); string runId;
        await using(var service=new LabService(workspace.Root))
        {
            using var target=await CaptureTests.StartTarget(workspace.Root);
            try
            {
                var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                var handshake=await service.ScenariosAsync(identity.Id,CancellationToken.None); var scenario=handshake.Scenarios.Single(s=>s.Id=="cpu-sum");
                var tools=new LabTools(service); var response=await tools.Experiment(new CreateIndividualLatencyPlan(scenario.Id,scenario.Version,scenario.InputHash,"baseline",25,1));
                Assert.Equal("ok",response.Status); var plan=Wire.Deserialize<IndividualLatencyPlan>(response.Data!.Value.GetRawText());
                var grant=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,120,1);
                var operation=await service.StartIndividualLatenciesAsync(identity.Id,identity.Epoch,plan.Id,grant.Id,"individual",CancellationToken.None);
                var complete=await ServiceTests.Wait(service,operation.Id); Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); runId=complete.ResultId!;
                var run=service.Catalog.Get<IndividualLatencyRun>(runId); Assert.Null(run.Measurement.IndividualLatencies); Assert.True(service.Catalog.IsPinned(run.ArtifactId));
                var measured=Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(run.ArtifactId))));
                Assert.Equal(25,measured.IndividualLatencies!.ElapsedTicks.Count); Assert.Equal(run.ObserverIdentity,measured.IndividualLatencies.ObserverIdentity);
                var slice=await tools.Query(new QueryIndividualLatencies(runId,5,3)); Assert.Equal("ok",slice.Status); Assert.Equal(3,slice.Data!.Value.GetProperty("elapsed_ticks").GetArrayLength());
            }
            finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
        }
        await using var reopened=new LabService(workspace.Root); Assert.NotNull(await reopened.IndividualLatencySliceAsync(runId,0,10));
    }
}
