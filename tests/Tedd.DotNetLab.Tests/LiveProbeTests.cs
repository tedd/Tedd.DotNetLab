using System.Text.Json;
using System.Security.Cryptography;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Probes;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class LiveProbeTests
{
    [Theory,InlineData(false),InlineData(true)]
    [Case("IT-PROBE-LIVE", "FR-050,FR-051,FR-053", "M5", "Integration")]
    public async Task SelectedProbe_UsesApprovedHelperAndExactCounts_ThenRestores(bool timing)
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var harness=new HarnessClient(); var handshake=await harness.HandshakeAsync(target,CancellationToken.None);
            Assert.NotNull(handshake.ProbeHelper);
            string dll=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
            string moduleHash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(dll)));
            var method=Assert.Single(PeInspector.Methods(dll,moduleHash,"NativeFixture.Value").Rows);
            var references=PeInspector.ProbeReferences(dll);
            var native=new ProfilerClient();
            var inspectionGrant=new Grant("inspect",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,DateTimeOffset.UtcNow.AddMinutes(2),2);
            var source=(await native.SendAsync(target,new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L,grant=inspectionGrant,
                selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=method.ModuleHash,signature=method.SignatureHex } },CancellationToken.None))
                .Deserialize<NativeSnapshotReply>(Wire.Json)!;
            var policy=new CilPolicy(method.ModuleMvid,method.Token,method.SignatureHex,true,false,false,0,
                Calls:[new(references.EnterToken,1,false,true),new(references.ExitToken,1,false,true)]);
            byte[] body=CilProbeRewriter.Rewrite(source.OriginalBody,policy,0,references.EnterToken,references.ExitToken).Body;
            string hash=Convert.ToHexStringLower(SHA256.HashData(body)); string scope=method.ModuleMvid+"/"+method.Token+"/"+moduleHash;
            var grant=new Grant("probe",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InstallProbes,DateTimeOffset.UtcNow.AddMinutes(2),20,hash,scope);
            object Command(string action,long epoch,string? helperHash=null)=>new { schema_version=Wire.Version,action,target_id=target.Id,process_id=target.ProcessId,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=epoch,grant,transaction_id="probe-transaction",body,
                base_il_hash=source.OriginalBodyHash,probe=true,probe_id=0,helper_hash=helperHash??handshake.ProbeHelper!.ModuleHash,
                enter_token=references.EnterToken,exit_token=references.ExitToken,
                selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=moduleHash,signature=method.SignatureHex } };
            Assert.Equal("helper_identity_changed",(await Assert.ThrowsAsync<LabException>(()=>native.SendAsync(target,Command("preflight",0,new string('0',64)),CancellationToken.None))).Code);
            await native.SendAsync(target,Command("preflight",0),CancellationToken.None);
            await harness.ProbeAsync(target,new("begin",0,grant,hash,scope,Timing:timing),CancellationToken.None);
            var submitted=await native.SendAsync(target,Command("apply",0),CancellationToken.None);
            long epoch=long.Parse(submitted.GetProperty("epoch").GetString()!);
            Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>harness.ProbeAsync(target,
                new("stop",epoch,grant with { MaximumOperations=21 },hash,scope),CancellationToken.None))).Code);
            var execute=new Grant("execute",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddMinutes(2),10);
            Tedd.DotNetLab.Contracts.RunRequest Run(long current,int count)=>new("exec","run","block","native-value","candidate",count,0,true,false,current,
                target with { Epoch=current },execute,DateTimeOffset.UtcNow.AddSeconds(30),scope);
            Assert.True((await harness.SetupAsync(Run(epoch,5),CancellationToken.None)).Correct);
            await native.SendAsync(target,Command("verify_mutation",epoch),CancellationToken.None);
            var setup=(await harness.ProbeAsync(target,new("stop",epoch,grant,hash,scope),CancellationToken.None)).Deserialize<ProbeSnapshot>(Wire.Json)!;
            Assert.True(setup.Complete); Assert.Equal(5,setup.Rows.Sum(r=>r.Entered));
            await harness.ProbeAsync(target,new("begin",epoch,grant,hash,scope,Timing:timing),CancellationToken.None);
            Assert.True((await harness.ExecuteAsync(Run(epoch,1000),CancellationToken.None)).Correct);
            var snapshot=(await harness.ProbeAsync(target,new("stop",epoch,grant,hash,scope),CancellationToken.None)).Deserialize<ProbeSnapshot>(Wire.Json)!;
            Assert.True(snapshot.Complete); Assert.Equal(1000,snapshot.Rows.Sum(r=>r.Entered)); Assert.Equal(1000,snapshot.Rows.Sum(r=>r.NormallyCompleted));
            Assert.Equal(0,snapshot.Rows.Sum(r=>r.InFlight)); Assert.Equal(0,snapshot.FirstUseThreads);
            if(timing) Assert.True(snapshot.Rows.Sum(r=>r.NormalElapsedTicks)>0); else Assert.All(snapshot.Rows,r=>Assert.Null(r.NormalElapsedTicks));
            var restored=await native.SendAsync(target,Command("restore",epoch),CancellationToken.None);
            epoch=long.Parse(restored.GetProperty("epoch").GetString()!);
            Assert.True((await harness.SetupAsync(Run(epoch,5),CancellationToken.None)).Correct);
            await native.SendAsync(target,Command("verify_mutation",epoch),CancellationToken.None);
            Assert.False((await harness.HandshakeAsync(target,CancellationToken.None)).ProbeHelper!.Active);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
