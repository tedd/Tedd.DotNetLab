using System.Diagnostics;
using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Collector.EventPipe;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class NativeStartupTests
{
    [Fact,Case("IT-NATIVE-STARTUP-ABI","FR-001,FR-002,FR-003,FR-074","M7","Integration")]
    public async Task PinnedStartupProfilerReportsActualRuntimeWithoutPromotingAnotherPlatform()
    {
        using var workspace=new TestWorkspace(); using var process=await Start(workspace.Root);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var identity=new Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity; var client=new ProfilerClient();
            for(int command=0;command<25;command++)
            {
                var reply=await client.HandshakeAsync(identity,timeout.Token);
                Assert.Equal(identity.Id,reply.TargetId); Assert.Equal(identity.StartTimeUtcTicks,reply.StartTimeUtcTicks);
                Assert.Equal(process.Id,reply.ProcessId); Assert.Equal(RuntimeSupport.Current,reply.Runtime); Assert.Equal("x64",reply.Architecture);
                Assert.Equal("ICorProfilerInfo12",reply.Interface); Assert.True(reply.Startup); Assert.Equal(workspace.Root,reply.Workspace);
                if(OperatingSystem.IsLinux())
                {
                    Assert.Equal("experimental",reply.NativeInspection); Assert.Equal("experimental",reply.Rejit);
                    Assert.Equal("experimental",reply.Probes); Assert.Equal("experimental",reply.Restore); Assert.Equal("experimental",reply.InlinerHandling);
                }
            }
            await using(var service=new LabService(workspace.Root))
            {
                var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
                await service.ProfilerAsync(target.Id,timeout.Token);
                Assert.Equal(CapabilityState.Supported,service.Target(target.Id).Capabilities["native_inspection"].State);
                if(OperatingSystem.IsLinux())
                {
                    foreach(string capability in new[]{"rejit","timing_probes","restore","inliner_handling"})
                        Assert.Equal(CapabilityState.Supported,service.Target(target.Id).Capabilities[capability].State);
                    var handshake=await service.ScenariosAsync(target.Id,timeout.Token); var scenario=handshake.Scenarios.Single(s=>s.Id=="cpu-sum");
                    var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,1,10,0,5,"quiet-validation",false,"Startup qualification only.",false,30,new()));
                    var grant=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,60,2);
                    var execution=await ServiceTests.Wait(service,(await service.StartExecutionAsync(target.Id,0,plan.Id,grant.Id,"configured-profiler",timeout.Token)).Id);
                    Assert.Equal(OperationState.Completed,execution.State);
                    var runs=service.Catalog.List("run").Rows; Assert.Equal(scenario.Variants.Count,runs.Count);
                    Assert.All(runs,run=>Assert.Contains("original_normal_compile",run.Json));
                }
            }
            process.StandardInput.Close(); await process.WaitForExitAsync(timeout.Token); Assert.Equal(0,process.ExitCode);
            if(OperatingSystem.IsLinux()) Assert.False(File.Exists(Path.Combine(Path.GetTempPath(),"CoreFxPipe_"+ProfilerClient.PipeName(identity))));
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("UT-NATIVE-STARTUP-IDENTITY","FR-002,FR-063,FR-070,FR-074","M7","Integration")]
    public async Task StartupEndpointRefreshesHandshakeEpochAndRejectsWrongIdentitySchemaAndUnqualifiedAction()
    {
        using var workspace=new TestWorkspace(); using var process=await Start(workspace.Root);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var identity=new Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity; var client=new ProfilerClient();
            var request=new ProfilerRequest(Wire.Version,"handshake",identity.Id,identity.ProcessId,identity.StartTimeUtcTicks,0);
            foreach(var entry in new[]{("schema_unsupported",request with { SchemaVersion="unsupported" }),
                ("target_identity_changed",request with { StartTimeUtcTicks=identity.StartTimeUtcTicks-1 }),
                ("target_identity_changed",request with { ProcessId=identity.ProcessId+1 }),
                ("capability_unsupported",request with { Action="unqualified_action" })})
                Assert.Equal(entry.Item1,(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(identity,entry.Item2,timeout.Token))).Code);
            if(OperatingSystem.IsLinux())
            {
                var current=await client.SendAsync(identity,request with { ExpectedTargetEpoch=1 },timeout.Token);
                Assert.Equal("0",current.GetProperty("epoch").GetString());
                Assert.Equal("stale_epoch",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(identity,
                    request with { Action="snapshot",ExpectedTargetEpoch=1 },timeout.Token))).Code);
                string nested="{\"nested\":"+new string('[',40)+"0"+new string(']',40)+"}";
                await using var pipe=new NamedPipeClientStream(".",ProfilerClient.PipeName(identity),PipeDirection.InOut,
                    PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(timeout.Token); PeerIdentity.VerifyServer(pipe,identity);
                byte[] body=Encoding.UTF8.GetBytes(nested),header=new byte[4]; BinaryPrimitives.WriteInt32LittleEndian(header,body.Length);
                await pipe.WriteAsync(header,timeout.Token); await pipe.WriteAsync(body,timeout.Token);
                var rejected=await Frames.ReadAsync<ProfilerReply>(pipe,timeout.Token);
                Assert.Equal("error",rejected.Status); Assert.Equal("resource_limit",rejected.Code); Assert.Null(rejected.Data);
                await pipe.WriteAsync(new byte[]{1},timeout.Token);
            }
            else
            {
                var current=await client.SendAsync(identity,request with { ExpectedTargetEpoch=1 },timeout.Token);
                Assert.Equal("0",current.GetProperty("epoch").GetString());
            }
            Assert.Equal(RuntimeSupport.Current,(await client.HandshakeAsync(identity,timeout.Token)).Runtime);
            process.StandardInput.Close(); await process.WaitForExitAsync(timeout.Token); Assert.Equal(0,process.ExitCode);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    private static async Task<Process> Start(string workspace)
    {
        string profiler=OperatingSystem.IsWindows()?Path.Combine(TestWorkspace.TaskRoot,"native","Tedd.DotNetLab.Profiler.dll"):
            Environment.GetEnvironmentVariable("DOTNETLAB_LINUX_PROFILER_PATH")??"/mnt/d/Workspaces/AI/dotnetlab-implementation/linux-native-build/Tedd.DotNetLab.Profiler.so";
        Assert.True(File.Exists(profiler),"Build the platform's native startup adapter before this qualification.");
        var start=new ProcessStartInfo("dotnet") { RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,UseShellExecute=false };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"));
        start.ArgumentList.Add("--harness"); start.ArgumentList.Add(workspace);
        start.Environment["CORECLR_ENABLE_PROFILING"]="1"; start.Environment["CORECLR_PROFILER"]="{63EA6B80-5694-4B6B-8D4A-754CB224AE37}";
        start.Environment["CORECLR_PROFILER_PATH"]=profiler; start.Environment["DOTNETLAB_WORKSPACE"]=workspace;
        start.Environment["DOTNETLAB_DISPOSABLE"]="1"; start.Environment["DOTNETLAB_EXIT_ON_STDIN"]="1";
        var process=Process.Start(TestWorkspace.PinRuntime(start))!;
        try
        {
            if(await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) is null)
                throw new InvalidOperationException("Startup fixture failed: "+await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await CaptureTests.WaitForDiagnosticPublicationAsync(process);
            return process;
        }
        catch { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); throw; }
    }
}
