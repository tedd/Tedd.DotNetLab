using System.Security.Cryptography;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Collector.EventPipe;
using Tedd.DotNetLab.Transport.Local;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class PlatformSnapshotTests
{
    [Fact,Case("IT-NAT-004","FR-016,FR-041,FR-043,FR-074","M7","Integration")]
    public async Task ControlledGcAndJitChangesYieldVerifiedCopiesOrExplicitVersionRejection()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root,inspectionStress:true);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45)); int verified=0,rejected=0;
        try
        {
            var target=new Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity; var client=new ProfilerClient();
            string dll=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
            string hash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dll)));
            var method=Assert.Single(PeInspector.Methods(dll,hash,"NativeFixture.Value").Rows);
            var grant=new Grant("stress-read",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,DateTimeOffset.UtcNow.AddMinutes(1),25);
            for(int acquisition=0;acquisition<25;acquisition++)
            {
                try
                {
                    var response=(await client.SendAsync(target,new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,
                        start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L,grant,
                        selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=hash,signature=method.SignatureHex } },deadline.Token)).Deserialize<NativeSnapshotReply>(Wire.Json)!;
                    Assert.NotEmpty(response.Snapshots);
                    foreach(var snapshot in response.Snapshots) { NativeDecoder.Validate(snapshot); Assert.NotEmpty(NativeDecoder.Decode(snapshot).Instructions); }
                    verified++;
                }
                catch(LabException failure) when(failure.Code=="version_changed") { rejected++; }
            }
            Assert.Equal(25,verified+rejected); Assert.True(verified>0);
            process.StandardInput.Close(); await process.WaitForExitAsync(deadline.Token); Assert.Equal(0,process.ExitCode);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("IT-PLAT-SNAPSHOT-SUBSET","FR-005,FR-040,FR-041,FR-043,FR-070","M7","Integration")]
    public async Task GenericAndUncompiledMethodsCannotTriggerImplicitInvocationOrAcquisition()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            var target=new Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity; var client=new ProfilerClient();
            string dll=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
            string hash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dll)));
            var grant=new Grant("subset",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,DateTimeOffset.UtcNow.AddMinutes(1),4);
            foreach(var selected in new[]{("NativeFixture.Generic","capability_unsupported"),("NativeFixture.NeverInvoked","not_jitted")})
            {
                var method=Assert.Single(PeInspector.Methods(dll,hash,selected.Item1).Rows);
                Assert.Equal(selected.Item2,(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,new {
                    schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,
                    expected_target_epoch=0L,grant,selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=hash,signature=method.SignatureHex }
                },CancellationToken.None))).Code);
            }
            Assert.False((await client.HandshakeAsync(target,CancellationToken.None)).ExecutionActive);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("IT-PLAT-INSPECTION-RESTART","FR-020,FR-026,FR-040,FR-041,FR-042,FR-043,FR-070,FR-072","M7","Integration")]
    public async Task PublicInspectionSealsPinnedArtifactsForOfflineQueriesAfterRestart()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root); string inspectionId;
        try
        {
            await using(var service=new LabService(workspace.Root))
            {
                var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
                await service.ProfilerAsync(target.Id,CancellationToken.None);
                var build=await service.ImportAsync(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"),"application/vnd.microsoft.portable-executable");
                var method=Assert.Single((await service.MethodsAsync(build.Id,"NativeFixture.Value",100,null)).Rows);
                var grant=service.IssueGrant(target.Id,Permission.InspectLiveCode,60,3);
                var reply=await new LabTools(service).Inspect(new InspectNative(target.Id,method.Id,"0",grant.Id)); Assert.Equal("ok",reply.Status);
                var operation=reply.Data!.Value.Deserialize<Operation>(Wire.Json)!; var complete=await ServiceTests.Wait(service,operation.Id);
                Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); inspectionId=complete.ResultId!;
            }
            process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(0,process.ExitCode);
            await using var reopened=new LabService(workspace.Root); var inspection=reopened.Catalog.Get<JsonElement>(inspectionId);
            Assert.Equal("verified_live_acquisition",inspection.GetProperty("qualification").GetString());
            Assert.False(inspection.GetProperty("proves_earlier_run_code").GetBoolean());
            foreach(var id in inspection.GetProperty("artifact_ids").EnumerateArray())
            {
                var artifact=reopened.Catalog.Get<Artifact>(id.GetString()!); Assert.True(reopened.Catalog.IsPinned(artifact.Id));
                Assert.Equal(RuntimeSupport.Producer(RuntimeSupport.Current),artifact.Producer);
                await reopened.Artifacts.VerifyAsync(artifact.Hash,artifact.Length); Assert.NotNull(await reopened.DecodeAsync(artifact.Id));
            }
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("IT-PLAT-INSPECTION-LEASE","FR-003,FR-004,FR-043","M7","Integration")]
    public async Task NativeSnapshotCannotEnterAnActiveRegisteredMeasurement()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var target=new Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var harness=new HarnessClient(); var handshake=await harness.HandshakeAsync(target,deadline.Token); var native=new ProfilerClient();
            var grant=new Grant("execution",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddMinutes(1),1);
            var run=new Tedd.DotNetLab.Contracts.RunRequest("execution","run","block","cpu-sum","baseline",1000000,0,true,false,0,handshake.Target,grant,grant.ExpiresAt);
            var running=harness.ExecuteAsync(run,deadline.Token);
            while(!(await native.HandshakeAsync(target,deadline.Token)).ExecutionActive&&!running.IsCompleted) await Task.Delay(5,deadline.Token);
            Assert.False(running.IsCompleted);
            Assert.Equal("lease_conflict",(await Assert.ThrowsAsync<LabException>(()=>native.SendAsync(target,new {
                schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L },deadline.Token))).Code);
            Assert.True((await running).Correct); Assert.False((await native.HandshakeAsync(target,deadline.Token)).ExecutionActive);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("IT-PLAT-NATIVE-SNAPSHOT","FR-002,FR-040,FR-041,FR-042,FR-043,FR-070,FR-074","M7","Integration")]
    public async Task ExistingBodyCopiesRetainIdentityAndDecodeAfterTargetExit()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try
        {
            var target=new Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            string dll=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
            string hash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dll)));
            var method=Assert.Single(PeInspector.Methods(dll,hash,"NativeFixture.Value").Rows);
            var grant=new Grant("platform-inspection",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,DateTimeOffset.UtcNow.AddMinutes(1),4);
            var client=new ProfilerClient();
            object Request(Grant authority,string? token=null)=>new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,
                process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L,grant=authority,
                selector=new { module_mvid=method.ModuleMvid,method_token=token??method.Token,module_hash=hash,signature=method.SignatureHex } };
            Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,
                Request(grant with { Workspace=workspace.Root+"-other" }),timeout.Token))).Code);
            Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,
                Request(grant with { ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1) }),timeout.Token))).Code);
            var reply=(await client.SendAsync(target,Request(grant),timeout.Token)).Deserialize<NativeSnapshotReply>(Wire.Json)!;
            Assert.False(reply.CodeRewrittenForInspection); Assert.False(reply.Perturbation.RuntimeSuspended);
            Assert.True(reply.Perturbation.WorkerRegisteredWithRuntime); Assert.NotEmpty(reply.Snapshots);
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(reply.OriginalBody)),reply.OriginalBodyHash);
            foreach(var snapshot in reply.Snapshots)
            {
                Assert.Equal(target.Id,snapshot.TargetId); Assert.Equal(method.Token,snapshot.MethodToken); Assert.Equal(method.ModuleMvid,snapshot.ModuleMvid);
                Assert.Equal(hash,snapshot.ModuleHash); Assert.Equal(method.SignatureHex,snapshot.Signature); Assert.Equal("unknown",snapshot.Tier);
                Assert.Equal(snapshot.OriginalIlHash,snapshot.CurrentIlHash); NativeDecoder.Validate(snapshot);
                Assert.NotEmpty(NativeDecoder.Decode(snapshot).Instructions);
            }
            process.StandardInput.Close(); await process.WaitForExitAsync(timeout.Token); Assert.Equal(0,process.ExitCode);
            foreach(var snapshot in reply.Snapshots) Assert.NotEmpty(NativeDecoder.Decode(snapshot).Instructions);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
