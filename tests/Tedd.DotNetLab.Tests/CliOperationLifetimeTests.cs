using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CliOperationLifetimeTests
{
    [Fact]
    public async Task CliExecutionSurvivesCallerExitAndIdleWindowAndRemainsCancellable()
    {
        using var workspace=new TestWorkspace();
        using var target=await CaptureTests.StartTarget(workspace.Root);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        Process? controller=null;
        IDisposable? measurement=null;
        try
        {
            TargetIdentity identity;
            ExperimentPlan plan;
            Grant grant;
            await using(var service=new LabService(workspace.Root))
            {
                identity=service.Discover().Single(item=>item.Identity.ProcessId==target.Id).Identity;
                var scenario=(await service.ScenariosAsync(identity.Id,timeout.Token)).Scenarios.Single(s=>s.Id=="cpu-sum");
                plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,
                    1,10,0,1,"quiet-validation",false,"descriptive",false,60,new()));
                grant=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,120,4);
            }

            // Keep the accepted operation queued beyond the controller's idle timeout.
            measurement=await MachineMutex.AcquireAsync(MachineMutex.MeasurementName,timeout.Token);
            var request=new { action="start",target_id=identity.Id,expected_target_epoch="0",plan_id=plan.Id,
                grant_id=grant.Id,idempotency_key="cli-survives-exit" };
            var accepted=(await Call(workspace.Root,request,timeout.Token)).Deserialize<Operation>(Wire.Json)!;
            Assert.Equal(OperationState.Running,accepted.State);
            await Task.Delay(TimeSpan.FromSeconds(6),timeout.Token);
            Assert.Equal(OperationState.Running,(await Status(workspace.Root,accepted.Id,timeout.Token)).State);
            controller=Controller(workspace.Root);
            Assert.NotNull(controller);
            Assert.False(controller.HasExited);
            var replay=(await Call(workspace.Root,request,timeout.Token)).Deserialize<Operation>(Wire.Json)!;
            Assert.Equal(accepted.Id,replay.Id);

            measurement.Dispose(); measurement=null;
            var completed=await Wait(workspace.Root,accepted.Id,timeout.Token);
            Assert.True(completed.State==OperationState.Completed,Wire.Serialize(completed));
            Assert.NotNull(completed.ResultId);

            measurement=await MachineMutex.AcquireAsync(MachineMutex.MeasurementName,timeout.Token);
            var cancellable=(await Call(workspace.Root,new { action="start",target_id=identity.Id,expected_target_epoch="0",
                plan_id=plan.Id,grant_id=grant.Id,idempotency_key="cli-explicit-cancel" },timeout.Token)).Deserialize<Operation>(Wire.Json)!;
            Assert.Equal(OperationState.Running,cancellable.State);
            await Call(workspace.Root,new { action="cancel",operation_id=cancellable.Id },timeout.Token);
            var cancelled=await Wait(workspace.Root,cancellable.Id,timeout.Token);
            Assert.Equal(OperationState.Tainted,cancelled.State);
            Assert.Equal("cancelled",cancelled.ErrorCode);
            using var reused=Controller(workspace.Root);
            Assert.NotNull(reused);
            Assert.Equal(controller.Id,reused.Id);
            measurement.Dispose(); measurement=null;

            await controller.WaitForExitAsync(timeout.Token);
            Assert.Equal(0,controller.ExitCode);
            await using var reopened=new LabService(workspace.Root);
            Assert.Equal(OperationState.Completed,reopened.Status(accepted.Id).State);
            Assert.Equal(2,reopened.Catalog.List("run").Rows.Count);
        }
        finally
        {
            measurement?.Dispose();
            if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); }
            controller??=Controller(workspace.Root);
            if(controller is not null)
            {
                try { await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
                catch(TimeoutException) { controller.Kill(true); await controller.WaitForExitAsync(); }
                controller.Dispose();
            }
        }
    }

    private static async Task<JsonElement> Call(string workspace,object request,CancellationToken cancellation)
    {
        var start=RuntimeSupport.ControllerStartInfo();
        start.RedirectStandardInput=true; start.RedirectStandardOutput=true; start.RedirectStandardError=true;
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll"));
        start.ArgumentList.Add("call"); start.ArgumentList.Add(workspace); start.ArgumentList.Add("lab.run");
        using var process=Process.Start(start)!;
        var output=process.StandardOutput.ReadToEndAsync(cancellation);
        var error=process.StandardError.ReadToEndAsync(cancellation);
        try
        {
            await process.StandardInput.WriteLineAsync(Wire.Serialize(request)); process.StandardInput.Close();
            await process.WaitForExitAsync(cancellation).WaitAsync(TimeSpan.FromSeconds(20),cancellation);
            Assert.True(process.ExitCode==0,await error.WaitAsync(TimeSpan.FromSeconds(5),cancellation));
            var reply=Wire.Deserialize<LabResponse>(await output.WaitAsync(TimeSpan.FromSeconds(5),cancellation));
            Assert.True(reply.Status=="ok",Wire.Serialize(reply));
            return reply.Data!.Value;
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    private static async Task<Operation> Status(string workspace,string id,CancellationToken cancellation)=>
        (await Call(workspace,new { action="status",operation_id=id },cancellation)).Deserialize<Operation>(Wire.Json)!;

    private static async Task<Operation> Wait(string workspace,string id,CancellationToken cancellation)
    {
        while(true)
        {
            var operation=await Status(workspace,id,cancellation);
            if(Lifecycle.Terminal(operation.State)) return operation;
            await Task.Delay(100,cancellation);
        }
    }

    private static Process? Controller(string workspace)
    {
        string logs=Path.Combine(workspace,"logs");
        if(!Directory.Exists(logs)) return null;
        foreach(string path in Directory.GetFiles(logs,"shared-controller-*.jsonl"))
        {
            using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
            using var reader=new StreamReader(file);
            using var row=JsonDocument.Parse(reader.ReadLine()!);
            var root=row.RootElement;
            try
            {
                var process=Process.GetProcessById(root.GetProperty("process_id").GetInt32());
                if(process.StartTime.ToUniversalTime().Ticks.ToString()==root.GetProperty("process_start_utc_ticks").GetString()) return process;
                process.Dispose();
            }
            catch(ArgumentException) { }
        }
        return null;
    }
}
