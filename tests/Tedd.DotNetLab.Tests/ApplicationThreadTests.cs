using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ApplicationThreadTests
{
    [Fact,Case("IT-HAR-002","FR-004,FR-024,FR-031,FR-037,FR-062","M2","Integration")]
    public async Task PublicExecutionRunsLifecycleOnTheRealApplicationOwningThread()
    {
        using var workspace=new TestWorkspace();
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"));
        start.ArgumentList.Add("--application-thread-harness"); start.ArgumentList.Add(workspace.Root); start.Environment["DOTNETLAB_EXIT_ON_STDIN"]="1";
        using var target=Process.Start(TestWorkspace.PinRuntime(start))!;
        string executionId;
        try
        {
            string? ready=await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
            if(ready is null) throw new InvalidOperationException(await target.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(2)));
            using var announcement=JsonDocument.Parse(ready!); int owningThread=announcement.RootElement.GetProperty("application_thread_id").GetInt32();
            Assert.True(owningThread>0); await CaptureTests.WaitForDiagnosticPublicationAsync(target);
            await using(var service=new LabService(workspace.Root))
            {
                var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                var handshake=await service.ScenariosAsync(identity.Id,CancellationToken.None);
                var scenario=handshake.Scenarios.Single(s=>s.Id=="application-thread"); Assert.Equal(ThreadAffinity.ApplicationThread,scenario.Affinity);
                var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,2,1000,1,99,"quiet-validation",false,"",false,30,new()));
                var execute=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,90,4); var tools=new LabTools(service);
                var reply=await tools.Run(new StartExecution(identity.Id,"0",plan.Id,execute.Id,"application-thread")); Assert.True(reply.Status=="ok",Wire.Serialize(reply));
                var completed=await ServiceTests.Wait(service,reply.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
                Assert.True(completed.State==OperationState.Completed,Wire.Serialize(completed)); executionId=completed.ResultId!;
                var execution=service.Catalog.Get<ExecutionRecord>(executionId); Assert.Equal(4,execution.RunIds.Count);
                foreach(var run in execution.RunIds.Select(service.Catalog.Get<RunRecord>))
                {
                    Assert.Equal(ThreadAffinity.ApplicationThread,run.Scenario.Affinity);
                    Assert.Equal(owningThread,run.Measurement.StartThreadId); Assert.Equal(owningThread,run.Measurement.EndThreadId);
                    Assert.Equal(1000,run.Measurement.CompletedOperations); Assert.True(run.Measurement.Correct&&run.Measurement.Quiescent);
                    Assert.NotNull(run.Measurement.ThreadAllocationBytes); Assert.Equal(handshake.Target,run.Target);
                }
            }
            target.StandardInput.Close(); await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(0,target.ExitCode);
            await using var reopened=new LabService(workspace.Root);
            Assert.All(reopened.Catalog.Get<ExecutionRecord>(executionId).RunIds.Select(reopened.Catalog.Get<RunRecord>),run=>Assert.Equal(owningThread,run.Measurement.StartThreadId));
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
    [Fact,Case("UT-HAR-DISPATCH-001","FR-016,FR-037","M2","Unit")]
    public async Task BoundedQueueCancellationAndShutdownCannotExecutePendingCallbacks()
    {
        using var dispatcher=new ApplicationThreadDispatcher(1); int calls=0; using var cancel=new CancellationTokenSource();
        var pending=dispatcher.DispatchAsync(()=>++calls,cancel.Token);
        Assert.Equal("resource_limit",(await Assert.ThrowsAsync<LabException>(()=>dispatcher.DispatchAsync(()=>++calls,CancellationToken.None))).Code);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        dispatcher.Dispose(); Assert.Equal(0,calls);
        Assert.Equal("dispatcher_stopped",(await Assert.ThrowsAsync<LabException>(()=>dispatcher.DispatchAsync(()=>++calls,CancellationToken.None))).Code);
        using var stopped=new ApplicationThreadDispatcher(); var abandoned=stopped.DispatchAsync(()=>++calls,CancellationToken.None); stopped.Dispose();
        Assert.Equal("dispatcher_stopped",(await Assert.ThrowsAsync<LabException>(()=>abandoned)).Code); Assert.Equal(0,calls);
    }
    [Fact,Case("UT-HAR-DISPATCH-002","FR-004,FR-037,FR-053","M2","Unit")]
    public async Task CancellationAndShutdownDoNotAcknowledgeAnExecutingCallbackEarly()
    {
        using var entered=new ManualResetEventSlim(); using var release=new ManualResetEventSlim();
        using var stop=new CancellationTokenSource(); using var cancel=new CancellationTokenSource();
        var ready=new TaskCompletionSource<ApplicationThreadDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=> { using var dispatcher=new ApplicationThreadDispatcher(); ready.SetResult(dispatcher); dispatcher.Pump(stop.Token); }) { IsBackground=true };
        thread.Start(); ApplicationThreadDispatcher? owner=null;
        try
        {
            owner=await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var running=owner.DispatchAsync(()=> { entered.Set(); release.Wait(); return Environment.CurrentManagedThreadId; },cancel.Token);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5))); cancel.Cancel(); Assert.False(running.IsCompleted);
            var queued=owner.DispatchAsync(()=>0,CancellationToken.None); owner.Dispose();
            Assert.Equal("dispatcher_stopped",(await Assert.ThrowsAsync<LabException>(()=>queued)).Code); Assert.False(running.IsCompleted);
            release.Set(); Assert.Equal(owner.OwningThreadId,await running.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { release.Set(); stop.Cancel(); owner?.Dispose(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); }
    }
    [Fact,Case("UT-HAR-DISPATCH-003","FR-037","M2","Unit")]
    public void AForeignThreadCannotPumpTheApplicationsQueue()
    {
        using var dispatcher=new ApplicationThreadDispatcher(); LabException? rejected=null;
        var thread=new Thread(()=> { try { dispatcher.Pump(CancellationToken.None); } catch(LabException error) { rejected=error; } });
        thread.Start(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); Assert.Equal("dispatcher_thread_mismatch",rejected?.Code);
    }
    [Fact,Case("UT-HAR-DISPATCH-004","FR-037","M2","Unit")]
    public async Task AFailedOrReentrantCallbackDoesNotTerminateTheOwningPump()
    {
        var ready=new TaskCompletionSource<ApplicationThreadDispatcher>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=> { using var dispatcher=new ApplicationThreadDispatcher(); ready.SetResult(dispatcher); dispatcher.Pump(CancellationToken.None); }) { IsBackground=true };
        thread.Start(); ApplicationThreadDispatcher? owner=null;
        try
        {
            owner=await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.ThrowsAsync<InvalidOperationException>(()=>owner.DispatchAsync<int>(()=>throw new InvalidOperationException("fixture"),CancellationToken.None));
            Assert.Equal("lease_conflict",(await Assert.ThrowsAsync<LabException>(()=>owner.DispatchAsync(()=> { owner.Pump(CancellationToken.None); return 0; },CancellationToken.None))).Code);
            Assert.Equal(owner.OwningThreadId,await owner.DispatchAsync(()=>Environment.CurrentManagedThreadId,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { owner?.Dispose(); Assert.True(thread.Join(TimeSpan.FromSeconds(5))); }
    }
}
