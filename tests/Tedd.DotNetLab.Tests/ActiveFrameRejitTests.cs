using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Transport.Local;
using Xunit;
using Xunit.Abstractions;

namespace Tedd.DotNetLab.Tests;

public sealed class ActiveFrameRejitTests(ITestOutputHelper output)
{
    private static int Expected(int iterations)=>unchecked((int)(16L*iterations*(iterations-1)+496L*iterations));
    private static bool Contains(NativeRegion region,ulong pointer)
    {
        ulong start=ulong.Parse(region.StartAddress[2..],NumberStyles.AllowHexSpecifier,CultureInfo.InvariantCulture);
        return pointer>=start&&pointer-start<(ulong)region.Bytes.Length;
    }
    [SupportedOSPlatform("windows")]
    private static async Task<ulong> Observe(OwnedWindowsFrameObserver observer,uint thread,IEnumerable<NativeRegion> source,CancellationToken cancellation)
    {
        var regions=source.ToArray(); ulong pointer=0;
        for(int attempt=0;attempt<100;attempt++)
        {
            pointer=observer.InstructionPointer(thread); if(regions.Any(region=>Contains(region,pointer))) return pointer;
            await Task.Delay(5,cancellation);
        }
        Assert.Contains(regions,region=>Contains(region,pointer)); return pointer;
    }
    [Theory,InlineData(false),InlineData(true),Case("UT-TXN-008","FR-052,FR-053,FR-070","M6","Integration")]
    public async Task RegisteredActiveFrameBlocksMutationUntilDeclaredExecutionIsQuiescent(bool attach)
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var process=attach?await NativeAttachTests.Start(workspace.Root,activeFrameStimulus:true):await ProfilerTests.StartNativeTarget(workspace.Root,activeFrameStimulus:true);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            using var observer=new OwnedWindowsFrameObserver(process,target);
            if(attach)
            {
                byte[] metadata=Encoding.UTF8.GetBytes(Wire.Serialize(new { schema_version=Wire.Version,target_id=target.Id,
                    process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,workspace=workspace.Root }));
                new Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient(target.ProcessId).AttachProfiler(TimeSpan.FromSeconds(10),Guid.Parse(ProfilerTests.ProfilerId),ProfilerTests.ProfilerPath,metadata);
            }
            var client=new ProfilerClient(); var harness=new HarnessClient(); await client.HandshakeAsync(target,timeout.Token);
            string dll=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
            string hash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dll,timeout.Token)));
            var method=Assert.Single(PeInspector.Methods(dll,hash,"NativeFixture.ActiveLoop").Rows);
            var inspect=new Grant("frame-read",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,DateTimeOffset.UtcNow.AddMinutes(2),4);
            var original=(await client.SendAsync(target,new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L,grant=inspect,
                selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=method.ModuleHash,signature=method.SignatureHex } },timeout.Token)).Deserialize<NativeSnapshotReply>(Wire.Json)!;
            byte[] body=original.OriginalBody; string bodyHash=Convert.ToHexStringLower(SHA256.HashData(body));
            string scope=method.ModuleMvid+"/"+method.Token+"/"+method.ModuleHash;
            var mutation=new Grant("frame-quiescence",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ReplaceIl,DateTimeOffset.UtcNow.AddMinutes(2),12,bodyHash,scope);
            object Command(string action,long epoch)=>new { schema_version=Wire.Version,action,target_id=target.Id,process_id=target.ProcessId,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=epoch,grant=mutation,transaction_id="frame-quiescence",body,
                base_il_hash=original.OriginalBodyHash,selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=method.ModuleHash,signature=method.SignatureHex } };
            var execute=new Grant("frame-execute",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddMinutes(2),8);
            var request=new Tedd.DotNetLab.Contracts.RunRequest("frame-execution","frame-run","frame-block","native-active-frame","baseline",1,0,true,false,0,target,execute,DateTimeOffset.UtcNow.AddSeconds(45));
            var running=harness.ExecuteAsync(request,timeout.Token);
            var entered=await Frames.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,timeout.Token);
            Assert.Equal("registered-active-frame",entered.GetProperty("phase").GetString());
            uint thread=entered.GetProperty("thread_id").GetUInt32();
            ulong instruction=await Observe(observer,thread,original.Snapshots.Where(snapshot=>snapshot.RejitId=="0").SelectMany(snapshot=>snapshot.Regions),timeout.Token);
            Assert.True((await client.HandshakeAsync(target,timeout.Token)).ExecutionActive); Assert.False(running.IsCompleted);
            foreach(string action in new[]{"preflight","apply"})
                Assert.Equal("lease_conflict",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,Command(action,0),timeout.Token))).Code);
            var before=await client.SendAsync(target,new ProfilerRequest(Wire.Version,"mutation_status",target.Id,target.ProcessId,target.StartTimeUtcTicks,0),timeout.Token);
            Assert.Equal(0,before.GetProperty("mutation_count").GetInt32()); Assert.Equal("0",before.GetProperty("epoch").GetString());
            Assert.False(before.GetProperty("pending").GetBoolean());
            var completed=await running; Assert.True(completed.Correct&&completed.Quiescent);
            Assert.False((await client.HandshakeAsync(target,timeout.Token)).ExecutionActive);
            await client.SendAsync(target,Command("preflight",0),timeout.Token);
            var submitted=await client.SendAsync(target,Command("apply",0),timeout.Token); long epoch=Wire.Deserialize<long>(submitted.GetProperty("epoch").GetRawText());
            Tedd.DotNetLab.Contracts.RunRequest Setup(long current)=>request with { RunId="zero-setup",ScenarioId="native-active-zero",ExpectedEpoch=current,
                Target=target with { Epoch=current },ExpectedMethodScope=scope,Deadline=DateTimeOffset.UtcNow.AddSeconds(15) };
            Assert.True((await harness.SetupAsync(Setup(epoch),timeout.Token)).Correct);
            var verified=await client.SendAsync(target,Command("verify_mutation",epoch),timeout.Token); Assert.False(verified.GetProperty("pending").GetBoolean());
            Assert.True(verified.GetProperty("compilation_observed").GetBoolean());
            var restore=await client.SendAsync(target,Command("restore",epoch),timeout.Token); long restored=Wire.Deserialize<long>(restore.GetProperty("epoch").GetRawText());
            Assert.True((await harness.SetupAsync(Setup(restored),timeout.Token)).Correct);
            Assert.False((await client.SendAsync(target,Command("verify_mutation",restored),timeout.Token)).GetProperty("pending").GetBoolean());
            output.WriteLine(Wire.Serialize(new { target,attach,old_thread_id=thread,observed_old_ip="0x"+instruction.ToString("x"),
                old_call_correct=completed.Correct,old_call_joined=completed.Quiescent,mutation_after_join=epoch,restored_epoch=restored,
                external_thread_suspension=true,performance_evidence=false }));
            process.StandardInput.Close(); await process.WaitForExitAsync(timeout.Token); Assert.Equal(0,process.ExitCode);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Theory,InlineData(false),InlineData(true),Case("IT-REJIT-002","FR-052,FR-053","M6","Integration")]
    public async Task ObservedOldNativeFrameRetainsOriginalBodyWhileFutureCallsUseReplacement(bool attach)
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(90));
        string dll=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(dll); start.ArgumentList.Add("--active-frame-fixture"); start.ArgumentList.Add(workspace.Root);
        start.Environment["CORECLR_ENABLE_PROFILING"]=attach?"0":"1"; start.Environment["DOTNET_ENABLE_PROFILING"]=attach?"0":"1";
        start.Environment["CORECLR_PROFILER"]=ProfilerTests.ProfilerId; start.Environment["CORECLR_PROFILER_PATH_64"]=ProfilerTests.ProfilerPath;
        start.Environment["DOTNETLAB_WORKSPACE"]=workspace.Root; start.Environment["DOTNETLAB_DISPOSABLE"]="1";
        using var process=Process.Start(TestWorkspace.PinRuntime(start))!; Task<string> error=process.StandardError.ReadToEndAsync();
        try
        {
            var ready=await Frames.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,timeout.Token); Assert.Equal("ready",ready.GetProperty("phase").GetString());
            Assert.Equal(Expected(ready.GetProperty("calibration_iterations").GetInt32()),ready.GetProperty("calibration").GetInt32());
            await CaptureTests.WaitForDiagnosticPublicationAsync(process);
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            using var observer=new OwnedWindowsFrameObserver(process,target);
            if(attach)
            {
                byte[] metadata=Encoding.UTF8.GetBytes(Wire.Serialize(new { schema_version=Wire.Version,target_id=target.Id,
                    process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,workspace=workspace.Root }));
                new Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient(target.ProcessId).AttachProfiler(TimeSpan.FromSeconds(10),Guid.Parse(ProfilerTests.ProfilerId),ProfilerTests.ProfilerPath,metadata);
            }
            string build=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dll,timeout.Token)));
            var method=Assert.Single(PeInspector.Methods(dll,build,"NativeFixture.ActiveLoop").Rows);
            var client=new ProfilerClient(); var authority=new Grant("frame-inspect",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,DateTimeOffset.UtcNow.AddMinutes(2),20);
            object Snapshot(long epoch)=>new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=epoch,grant=authority,
                selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=method.ModuleHash,signature=method.SignatureHex } };
            async Task<NativeSnapshotReply> Capture(long epoch)
            {
                for(int attempt=0;;attempt++)
                {
                    try { return (await client.SendAsync(target,Snapshot(epoch),timeout.Token)).Deserialize<NativeSnapshotReply>(Wire.Json)!; }
                    catch(LabException failure) when(failure.Code=="version_changed"&&attempt<2) { await Task.Delay(25,timeout.Token); }
                }
            }
            var original=await Capture(0); Assert.False(original.CodeRewrittenForInspection);
            byte[] body=[0x0e,0x1f,42,0x2a]; string hash=Convert.ToHexStringLower(SHA256.HashData(body));
            string scope=method.ModuleMvid+"/"+method.Token+"/"+method.ModuleHash;
            var mutate=new Grant("frame-replace",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ReplaceIl,DateTimeOffset.UtcNow.AddMinutes(2),20,hash,scope);
            object Command(string action,long epoch)=>new { schema_version=Wire.Version,action,target_id=target.Id,process_id=target.ProcessId,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=epoch,grant=mutate,transaction_id="active-frame",
                body,base_il_hash=original.OriginalBodyHash,selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=method.ModuleHash,signature=method.SignatureHex } };
            await client.SendAsync(target,Command("preflight",0),timeout.Token);
            long ticks=Wire.Deserialize<long>(ready.GetProperty("calibration_ticks").GetRawText()),frequency=Wire.Deserialize<long>(ready.GetProperty("frequency").GetRawText());
            int iterations=(int)Math.Clamp(frequency*8*ready.GetProperty("calibration_iterations").GetInt32()/ticks,20_000_000,400_000_000);
            await Frames.WriteAsync(process.StandardInput.BaseStream,new { action="start",iterations },timeout.Token);
            var started=await Frames.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,timeout.Token); uint thread=started.GetProperty("thread_id").GetUInt32();
            ulong before=await Observe(observer,thread,original.Snapshots.Where(s=>s.RejitId=="0").SelectMany(s=>s.Regions),timeout.Token);
            var submitted=await client.SendAsync(target,Command("apply",0),timeout.Token); long epoch=Wire.Deserialize<long>(submitted.GetProperty("epoch").GetRawText());
            await Frames.WriteAsync(process.StandardInput.BaseStream,new { action="future" },timeout.Token);
            var future=await Frames.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,timeout.Token);
            Assert.Equal(42,future.GetProperty("value").GetInt32()); Assert.False(future.GetProperty("old_frame_completed").GetBoolean());
            var status=await client.SendAsync(target,new ProfilerRequest(Wire.Version,"mutation_status",target.Id,target.ProcessId,target.StartTimeUtcTicks,epoch),timeout.Token);
            Assert.True(status.GetProperty("compilation_observed").GetBoolean()); Assert.True(status.GetProperty("pending").GetBoolean());
            Assert.Equal("mutation_unverified",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,Command("verify_mutation",epoch),timeout.Token))).Code);
            var active=await Capture(epoch); ulong after=await Observe(observer,thread,active.Snapshots.Where(s=>s.RejitId=="0").SelectMany(s=>s.Regions),timeout.Token);
            Assert.Contains(active.Snapshots.Where(s=>s.RejitId=="0"&&s.CurrentIlHash==original.OriginalBodyHash).SelectMany(s=>s.Regions),r=>Contains(r,after));
            Assert.DoesNotContain(active.Snapshots.Where(s=>s.RejitId!="0").SelectMany(s=>s.Regions),r=>Contains(r,after));
            Assert.Contains(active.Snapshots,s=>s.RejitId!="0"&&s.CurrentIlHash==hash);
            await Frames.WriteAsync(process.StandardInput.BaseStream,new { action="join" },timeout.Token);
            var joined=await Frames.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,timeout.Token); Assert.Equal(Expected(iterations),joined.GetProperty("result").GetInt32());
            var restored=await client.SendAsync(target,Command("restore",epoch),timeout.Token); Assert.True(restored.GetProperty("restore_api_succeeded").GetBoolean());
            await Frames.WriteAsync(process.StandardInput.BaseStream,new { action="future" },timeout.Token);
            var reverted=await Frames.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,timeout.Token); Assert.Equal(0,reverted.GetProperty("value").GetInt32());
            output.WriteLine(Wire.Serialize(new { target,attach,iterations,old_checksum=joined.GetProperty("result"),new_value=future.GetProperty("value"),
                restored_value=reverted.GetProperty("value"),old_thread_id=thread,old_ip_before="0x"+before.ToString("x"),old_ip_after="0x"+after.ToString("x"),
                external_thread_suspension=true,fixture_method_flags="NoInlining|AggressiveOptimization",measurable=false,promotion_eligible=false }));
            await Frames.WriteAsync(process.StandardInput.BaseStream,new { action="exit" },timeout.Token);
            Assert.Equal("exited",(await Frames.ReadAsync<JsonElement>(process.StandardOutput.BaseStream,timeout.Token)).GetProperty("phase").GetString());
            await process.WaitForExitAsync(timeout.Token); Assert.True(process.ExitCode==0,await error);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
