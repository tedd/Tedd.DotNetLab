using System.Security.Cryptography;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class RejitTests
{
    [Fact,Case("IT-REJIT-DISPOSABLE-SUBSET","FR-005,FR-052,FR-070","M6","Integration")]
    public async Task NonDisposableStartupCannotAdmitMutation()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root,disposable:false);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var client=new ProfilerClient(); var handshake=await client.HandshakeAsync(target,timeout.Token);
            Assert.Equal("unsupported",handshake.Rejit); Assert.Equal("unsupported",handshake.Probes); Assert.Equal("unsupported",handshake.Restore);
            Assert.Equal("capability_unsupported",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,
                new ProfilerRequest(Wire.Version,"apply",target.Id,target.ProcessId,target.StartTimeUtcTicks,0),timeout.Token))).Code);
            Assert.Equal(0,(await client.HandshakeAsync(target,timeout.Token)).Epoch);
            process.StandardInput.Close(); await process.WaitForExitAsync(timeout.Token); Assert.Equal(0,process.ExitCode);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Case("IT-REJIT-LEAF", "FR-052,FR-053,FR-054", "M6", "Integration")]
    public async Task Rejit_RequiresObservedCompilationAndOracle_ThenRestoresOriginal(bool correctnessFailure)
    {
        using var workspace=new TestWorkspace();
        using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            string dll=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
            string buildHash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dll)));
            var method=Assert.Single(PeInspector.Methods(dll,buildHash,"NativeFixture.Value").Rows);
            var client=new ProfilerClient(); var harness=new HarnessClient();
            var observe=new Grant("inspect",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,DateTimeOffset.UtcNow.AddMinutes(2),10);
            object Snapshot(long epoch)=>new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=epoch,grant=observe,
                selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=method.ModuleHash,signature=method.SignatureHex } };
            var original=(await client.SendAsync(target,Snapshot(0),CancellationToken.None)).Deserialize<NativeSnapshotReply>(Wire.Json)!;
            byte[] body=correctnessFailure?[0x0e,0x1f,43,0x2a]:[0x16,0x1f,40,0x18,0x58,0x2a];
            string hash=Convert.ToHexStringLower(SHA256.HashData(body));
            string scope=method.ModuleMvid+"/"+method.Token+"/"+method.ModuleHash;
            var mutate=new Grant("replace",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ReplaceIl,
                DateTimeOffset.UtcNow.AddMinutes(2),20,hash,scope);
            object Command(string action,long epoch,Grant? authority=null)=>new { schema_version=Wire.Version,action,target_id=target.Id,process_id=target.ProcessId,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=epoch,grant=authority??mutate,
                selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=method.ModuleHash,signature=method.SignatureHex },
                transaction_id="leaf-transaction",body,base_il_hash=original.OriginalBodyHash };
            foreach(var denied in new[]{mutate with {Id="wrong-body",ContentHash=new string('0',64)},mutate with {Id="wrong-scope",MethodScope=scope+"-foreign"},
                mutate with {Id="wrong-permission",Permission=Permission.InspectLiveCode}})
                Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,Command("preflight",0,denied),CancellationToken.None))).Code);
            if(OperatingSystem.IsLinux())
            {
                var inline=Assert.Single(PeInspector.Methods(dll,buildHash,"NativeFixture.InlineLeaf").Rows);
                var inlineGrant=mutate with {Id="inline-boundary",MethodScope=inline.ModuleMvid+"/"+inline.Token+"/"+inline.ModuleHash};
                Assert.Equal("inliner_coverage_changed",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,new {
                    schema_version=Wire.Version,action="preflight",target_id=target.Id,process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,
                    expected_target_epoch=0L,grant=inlineGrant,transaction_id="inline-boundary",body,base_il_hash=original.OriginalBodyHash,
                    selector=new {module_mvid=inline.ModuleMvid,method_token=inline.Token,module_hash=inline.ModuleHash,signature=inline.SignatureHex}
                },CancellationToken.None))).Code);
            }
            var submitted=await client.SendAsync(target,Command("apply",0),CancellationToken.None);
            Assert.True(submitted.GetProperty("pending").GetBoolean());
            long epoch=long.Parse(submitted.GetProperty("epoch").GetString()!);
            var execute=new Grant("execute",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddMinutes(2),20);
            Tedd.DotNetLab.Contracts.RunRequest Run(long current)=>new("exec","run","block","native-value","candidate",100,0,true,false,current,
                target with { Epoch=current },execute,DateTimeOffset.UtcNow.AddSeconds(30),scope);
            Assert.Equal("lease_conflict",(await Assert.ThrowsAsync<LabException>(()=>harness.ExecuteAsync(Run(epoch),CancellationToken.None))).Code);
            Assert.Equal("mutation_unverified",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,Command("verify_mutation",epoch),CancellationToken.None))).Code);
            Assert.Equal("scenario_scope_mismatch",(await Assert.ThrowsAsync<LabException>(()=>harness.SetupAsync(
                Run(epoch) with { ScenarioId="cpu-sum" },CancellationToken.None))).Code);
            Assert.Equal(!correctnessFailure,(await harness.SetupAsync(Run(epoch),CancellationToken.None)).Correct);
            var status=await client.SendAsync(target,new ProfilerRequest(Wire.Version,"mutation_status",target.Id,target.ProcessId,target.StartTimeUtcTicks,epoch),CancellationToken.None);
            Assert.True(status.GetProperty("compilation_observed").GetBoolean());
            Assert.NotEqual("0",status.GetProperty("rejit_id").GetString());
            if(correctnessFailure)
                Assert.Equal("mutation_unverified",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,Command("verify_mutation",epoch),CancellationToken.None))).Code);
            else
            {
                Assert.False((await client.SendAsync(target,Command("verify_mutation",epoch),CancellationToken.None)).GetProperty("pending").GetBoolean());
                Assert.True((await harness.ExecuteAsync(Run(epoch),CancellationToken.None)).Correct);
                var active=(await client.SendAsync(target,Snapshot(epoch),CancellationToken.None)).Deserialize<NativeSnapshotReply>(Wire.Json)!;
                Assert.Contains(active.Snapshots,s=>s.RejitId!="0"&&s.CurrentIlHash==hash);
                Assert.Contains(active.Snapshots,s=>s.RejitId!="0"&&s.CurrentIlBody is not null&&s.CurrentIlBody.SequenceEqual(body));
                Assert.Equal(original.OriginalBodyHash,active.OriginalBodyHash);
            }
            var restored=await client.SendAsync(target,Command("restore",epoch),CancellationToken.None);
            long restoredEpoch=long.Parse(restored.GetProperty("epoch").GetString()!);
            Assert.True(restoredEpoch>epoch);
            Assert.True((await harness.SetupAsync(Run(restoredEpoch),CancellationToken.None)).Correct);
            Assert.False((await client.SendAsync(target,Command("verify_mutation",restoredEpoch),CancellationToken.None)).GetProperty("pending").GetBoolean());
            Assert.True((await harness.ExecuteAsync(Run(restoredEpoch),CancellationToken.None)).Correct);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
