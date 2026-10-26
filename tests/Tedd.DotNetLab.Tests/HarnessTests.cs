using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class HarnessTests
{
    private static readonly TargetIdentity Target = new("test",1,1,"test","10.0.12","x64");
    private static RunRequest Request(ScenarioDescriptor descriptor,DateTimeOffset? deadline=null) =>
        new("exec","run","block",descriptor.Id,"baseline",3,1,true,!descriptor.IsAsync,0,Target,
            new("grant","workspace",Target.Id,1,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddMinutes(1),100),
            deadline ?? DateTimeOffset.UtcNow.AddMinutes(1));
    [Fact, Case("UT-HAR-001", "FR-037", "M2", "Unit")]
    public async Task Sync_Lifecycle_ResetsAfterWarmupAndCleansOnce()
    {
        var scenario = new SyncFake();
        var registry = new ScenarioRegistry();
        registry.Register(scenario);
        var result = await new ScenarioRunner(registry,Target,"workspace").ExecuteAsync(Request(scenario.Describe()),CancellationToken.None);
        Assert.Equal(new[]{"prepare","reset","execute","reset","execute","validate","cleanup"},scenario.Calls);
        Assert.True(result.Correct);
        Assert.Equal(3,result.CompletedOperations);
        Assert.NotNull(result.ThreadAllocationBytes);
        Assert.Equal(result.StartThreadId,result.EndThreadId);
    }
    [Fact, Case("UT-HAR-004", "FR-037", "M2", "Unit")]
    public async Task Sync_CorrectnessFailure_CleansAndRejects()
    {
        var scenario = new SyncFake { Correct=false };
        var registry = new ScenarioRegistry(); registry.Register(scenario);
        await Assert.ThrowsAsync<LabException>(() => new ScenarioRunner(registry,Target,"workspace").ExecuteAsync(Request(scenario.Describe()),CancellationToken.None));
        Assert.Equal(1,scenario.Calls.Count(c => c=="cleanup"));
    }
    [Fact, Case("UT-HAR-005", "FR-037", "M2", "Unit")]
    public async Task Sync_ApplicationAffinity_RequiresExplicitDispatcher()
    {
        var scenario = new SyncFake { Affinity=ThreadAffinity.ApplicationThread };
        var registry = new ScenarioRegistry(); registry.Register(scenario);
        Assert.Equal("capability_unsupported",(await Assert.ThrowsAsync<LabException>(() =>
            new ScenarioRunner(registry,Target,"workspace").ExecuteAsync(Request(scenario.Describe()),CancellationToken.None))).Code);
        var dispatcher = new RecordingDispatcher();
        await new ScenarioRunner(registry,Target,"workspace",dispatcher).ExecuteAsync(Request(scenario.Describe()),CancellationToken.None);
        Assert.True(dispatcher.Called);
    }
    [Fact, Case("UT-HAR-006", "FR-024,FR-037", "M2", "Unit")]
    public async Task Async_Completion_IsAwaitedWithoutThreadAllocation()
    {
        var scenario = new AsyncFake();
        var registry = new ScenarioRegistry(); registry.Register(scenario);
        var result = await new ScenarioRunner(registry,Target,"workspace").ExecuteAsync(Request(scenario.Describe()),CancellationToken.None);
        Assert.True(scenario.Completed);
        Assert.Null(result.ThreadAllocationBytes);
        Assert.True(result.EndTicks>result.StartTicks);
    }
    [Fact, Case("UT-HAR-011", "FR-037", "M2", "Unit")]
    public async Task Async_IgnoredCancellation_TaintsTarget()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = new AsyncFake { CompletionGate=completion.Task };
        var registry = new ScenarioRegistry(); registry.Register(scenario);
        var runner = new ScenarioRunner(registry,Target,"workspace");
        var request = Request(scenario.Describe()) with { WarmupBatches=0 };
        using var cancellation = new CancellationTokenSource();
        var run = runner.ExecuteAsync(request,cancellation.Token);
        try
        {
            await scenario.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancellation.Cancel();
            Assert.Equal("target_tainted",(await Assert.ThrowsAsync<LabException>(() => run)).Code);
            Assert.True(runner.Tainted);
            Assert.False(scenario.Cleaned.Task.IsCompleted);
            Assert.Equal("target_tainted",(await Assert.ThrowsAsync<LabException>(() => runner.ExecuteAsync(Request(scenario.Describe()),CancellationToken.None))).Code);
        }
        finally
        {
            completion.TrySetResult();
            await scenario.Cleaned.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }
    [Fact, Case("UT-HAR-DEADLINE", "FR-037,FR-038", "M2", "Unit")]
    public async Task ExpiredRequest_IsRejectedBeforeScenarioEntryWithoutTaint()
    {
        var scenario = new AsyncFake();
        var registry = new ScenarioRegistry(); registry.Register(scenario);
        var runner = new ScenarioRunner(registry,Target,"workspace");
        var request = Request(scenario.Describe(),DateTimeOffset.UtcNow.AddSeconds(-1));
        Assert.Equal("invalid_plan",(await Assert.ThrowsAsync<LabException>(() => runner.ExecuteAsync(request,CancellationToken.None))).Code);
        Assert.False(scenario.Entered.Task.IsCompleted);
        Assert.False(runner.Tainted);
    }
    [Fact, Case("IT-HAR-001", "FR-037", "M2", "Integration")]
    public async Task Pipe_RegisteredVariants_ValidateInPersistentProcess()
    {
        using var workspace = new TestWorkspace();
        using var process = await CaptureTests.StartTarget(workspace.Root);
        try
        {
            var identity = new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t => t.Identity.ProcessId==process.Id).Identity;
            var client = new HarnessClient();
            var handshake = await client.HandshakeAsync(identity,CancellationToken.None);
            Assert.Equal(RuntimeSupport.Current,handshake.Target.RuntimeVersion);
            var scenario = handshake.Scenarios.Single(s => s.Id=="cpu-sum");
            foreach (string variant in scenario.Variants)
            {
                var request = Request(scenario) with { Target=handshake.Target,Variant=variant,OperationCount=100,
                    Grant=new("local-grant",workspace.Root,handshake.Target.Id,handshake.Target.StartTimeUtcTicks,
                        Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddMinutes(1),10) };
                var result = await client.ExecuteAsync(request,CancellationToken.None);
                Assert.True(result.Correct);
                Assert.Equal(100,result.CompletedOperations);
                Assert.NotNull(result.ThreadAllocationBytes);
            }
        }
        finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Fact, Case("UT-HAR-007", "FR-037", "M2", "Unit")]
    public async Task Async_UnjoinedWork_TaintsTarget()
    {
        var scenario = new AsyncFake { Quiescent=false };
        var registry = new ScenarioRegistry(); registry.Register(scenario);
        var runner = new ScenarioRunner(registry,Target,"workspace");
        Assert.Equal("target_tainted",(await Assert.ThrowsAsync<LabException>(() => runner.ExecuteAsync(Request(scenario.Describe()),CancellationToken.None))).Code);
        Assert.True(runner.Tainted);
    }
    [Fact,Case("UT-CONF-001","FR-037,FR-055","M6","Unit")]
    public async Task FreshProcess_CannotBeClaimedByOrdinaryInProcessRunner()
    {
        var scenario=new SyncFake { ResetLevel=ResetLevel.FreshProcess };
        var registry=new ScenarioRegistry(); registry.Register(scenario);
        var runner=new ScenarioRunner(registry,Target,"workspace");
        Assert.Equal("capability_unsupported",(await Assert.ThrowsAsync<LabException>(()=>runner.ExecuteAsync(Request(scenario.Describe()),CancellationToken.None))).Code);
        Assert.Empty(scenario.Calls);
    }
    private sealed class SyncFake : ISynchronousScenario
    {
        public List<string> Calls { get; } = [];
        public bool Correct { get; init; } = true;
        public ThreadAffinity Affinity { get; init; }
        public ResetLevel ResetLevel { get; init; }=ResetLevel.LogicalState;
        public ScenarioDescriptor Describe() => new("sync","1","input",ResetLevel,Affinity,false,"op",["baseline","candidate"]);
        public void Prepare(ScenarioContext c) => Calls.Add("prepare");
        public void Reset(ResetContext c) => Calls.Add("reset");
        public long ExecuteBatch(int n) { Calls.Add("execute"); return n; }
        public ValidationResult Validate() { Calls.Add("validate"); return new(Correct); }
        public void Cleanup() => Calls.Add("cleanup");
    }
    private sealed class AsyncFake : IAsynchronousScenario
    {
        public bool Completed; public bool Quiescent=true;
        public int DelayMilliseconds=5;
        public Task? CompletionGate;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Cleaned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ScenarioDescriptor Describe() => new("async","1","input",ResetLevel.LogicalState,ThreadAffinity.Any,true,"op",["baseline","candidate"]);
        public ValueTask PrepareAsync(ScenarioContext c) => ValueTask.CompletedTask;
        public ValueTask ResetAsync(ResetContext c) { Completed=false; return ValueTask.CompletedTask; }
        public async ValueTask<long> ExecuteBatchAsync(int n,CancellationToken token)
        {
            Entered.TrySetResult();
            if(CompletionGate is not null) await CompletionGate;
            else await Task.Delay(DelayMilliseconds,CancellationToken.None);
            Completed=true; return n;
        }
        public ValueTask<ValidationResult> ValidateAsync() => ValueTask.FromResult(new ValidationResult(Completed,Quiescent:Quiescent));
        public ValueTask CleanupAsync() { Cleaned.TrySetResult(); return ValueTask.CompletedTask; }
    }
    private sealed class RecordingDispatcher : IScenarioDispatcher
    {
        public bool Called;
        public Task<T> DispatchAsync<T>(Func<T> action,CancellationToken cancellation)
        { Called=true; return Task.FromResult(action()); }
    }
}
