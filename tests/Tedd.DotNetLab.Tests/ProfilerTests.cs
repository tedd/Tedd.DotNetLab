using System.Text.Json;
using System.Diagnostics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ProfilerTests
{
    public const string ProfilerId="{63ea6b80-5694-4b6b-8d4a-754cb224ae37}";
    public static string ProfilerPath=>OperatingSystem.IsWindows()?Path.Combine(TestWorkspace.TaskRoot,"native","Tedd.DotNetLab.Profiler.dll"):
        Environment.GetEnvironmentVariable("DOTNETLAB_LINUX_PROFILER_PATH")??"/mnt/d/Workspaces/AI/dotnetlab-implementation/linux-native-build/Tedd.DotNetLab.Profiler.so";
    [Fact, Case("IT-NAT-000", "FR-001,FR-003,FR-074", "M0", "Integration")]
    public async Task Profiler_Startup_NegotiatesPinnedInterfaceAndIdentity()
    {
        using var workspace=new TestWorkspace();
        using var process=await StartNativeTarget(workspace.Root);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var result=await new ProfilerClient().HandshakeAsync(target,CancellationToken.None);
            Assert.Equal(target.Id,result.TargetId);
            Assert.Equal(target.StartTimeUtcTicks,result.StartTimeUtcTicks);
            Assert.Equal("ICorProfilerInfo12",result.Interface);
            Assert.Equal(workspace.Root,result.Workspace);
            Assert.True(result.Startup);
            Assert.Equal("experimental",result.NativeInspection);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    public static async Task<Process> StartNativeTarget(string workspace,bool inspectionStress=false,bool disposable=true,bool budgetGrowthStimulus=false,bool activeFrameStimulus=false,string? runtimeVersion=null,bool applicationThread=false,bool startupProfiler=true)
    {
        string dll=TestWorkspace.Workload(runtimeVersion);
        var start=new ProcessStartInfo("dotnet")
        { RedirectStandardOutput=true,RedirectStandardError=true,RedirectStandardInput=true,UseShellExecute=false };
        if(runtimeVersion is not null) { start.FileName=RuntimeSupport.StartInfo(runtimeVersion).FileName; RuntimeSupport.AddRuntimeArguments(start,runtimeVersion); }
        start.ArgumentList.Add(dll);
        start.ArgumentList.Add(applicationThread?"--application-thread-harness":"--harness"); start.ArgumentList.Add(workspace);
        start.Environment["CORECLR_ENABLE_PROFILING"]=startupProfiler?"1":"0";
        start.Environment["DOTNET_ENABLE_PROFILING"]=startupProfiler?"1":"0";
        start.Environment["CORECLR_PROFILER"]=ProfilerId;
        start.Environment["CORECLR_PROFILER_PATH_64"]=ProfilerPath;
        start.Environment["DOTNETLAB_WORKSPACE"]=workspace; start.Environment["DOTNETLAB_EXIT_ON_STDIN"]="1";
        start.Environment["DOTNETLAB_DISPOSABLE"]=disposable?"1":"0";
        if(inspectionStress) start.Environment["DOTNETLAB_INSPECTION_STRESS"]="1";
        start.Environment["DOTNETLAB_BUDGET_GROWTH_STIMULUS"]=budgetGrowthStimulus?"1":"0";
        start.Environment["DOTNETLAB_ACTIVE_FRAME_STIMULUS"]=activeFrameStimulus?"1":"0";
        var process=Process.Start(TestWorkspace.PinRuntime(start))!;
        try
        {
        var ready=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        if(ready is null) throw new InvalidOperationException("Native target failed: "+await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10)));
        await CaptureTests.WaitForDiagnosticPublicationAsync(process);
        return process;
        }
        catch { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); throw; }
    }

    [Fact, Case("UT-ID-NATIVE", "FR-002,FR-074", "M0", "Integration")]
    public async Task Profiler_WrongStartIdentityAndSchema_AreRejected()
    {
        using var workspace=new TestWorkspace();
        using var process=await StartNativeTarget(workspace.Root);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var client=new ProfilerClient();
            var wrong=new ProfilerRequest(Wire.Version,"handshake",target.Id,target.ProcessId,target.StartTimeUtcTicks+1,0);
            Assert.Equal("target_identity_changed",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,wrong,CancellationToken.None))).Code);
            Assert.Equal("schema_unsupported",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,wrong with { SchemaVersion="99" },CancellationToken.None))).Code);
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(0,process.ExitCode);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Fact, Case("IT-NAT-001", "FR-039,FR-040,FR-041,FR-043,FR-074", "M4", "Integration")]
    public async Task Snapshot_CopiesVerifiedVersionsWithoutInvokingOrRewriting()
    {
        using var workspace=new TestWorkspace();
        using var process=await StartNativeTarget(workspace.Root);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            string dll=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
            var method=Assert.Single(Tedd.DotNetLab.Analysis.Native.PeInspector.Methods(dll,"fixture","NativeFixture.Value").Rows);
            var grant=new Grant("native-grant",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,
                DateTimeOffset.UtcNow.AddMinutes(1),10);
            var client=new ProfilerClient();
            object Request(Grant supplied,long epoch=0)=>new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,
                process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=epoch,grant=supplied,
                selector=new { module_mvid=method.ModuleMvid,method_token=method.Token } };
            Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,
                Request(grant with { Workspace=workspace.Root+"-other" }),CancellationToken.None))).Code);
            Assert.Equal("stale_epoch",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,
                Request(grant,1),CancellationToken.None))).Code);
            var reply=(await client.SendAsync(target,Request(grant),CancellationToken.None))
                .Deserialize<Tedd.DotNetLab.Analysis.Native.NativeSnapshotReply>(Wire.Json)!;
            Assert.False(reply.CodeRewrittenForInspection);
            Assert.False(reply.Perturbation.RuntimeSuspended);
            Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(reply.OriginalBody)),reply.OriginalBodyHash);
            Assert.NotEmpty(reply.Snapshots);
            foreach(var snapshot in reply.Snapshots)
            {
                Assert.Equal(method.Token,snapshot.MethodToken);
                Assert.Equal(method.ModuleMvid,snapshot.ModuleMvid);
                Assert.Equal(snapshot.OriginalIlHash,snapshot.CurrentIlHash);
                Tedd.DotNetLab.Analysis.Native.NativeDecoder.Validate(snapshot);
                Assert.NotEmpty(Tedd.DotNetLab.Analysis.Native.NativeDecoder.Decode(snapshot).Instructions);
            }
            await process.StandardInput.WriteLineAsync("exit");
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            foreach(var snapshot in reply.Snapshots)
                Assert.NotEmpty(Tedd.DotNetLab.Analysis.Native.NativeDecoder.Decode(snapshot).Instructions);
            Assert.Equal(0,process.ExitCode);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }}
