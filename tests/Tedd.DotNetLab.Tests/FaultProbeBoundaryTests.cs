using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Probes;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class FaultProbeBoundaryTests
{
    [Fact, Case("IT-PROBE-FAULT-BOUNDARY", "FR-051,FR-054,FR-070", "M5", "Integration")]
    public async Task NativeFaultPreflight_RejectsAlteredRangesReturnsAndMetadataWithoutMutation()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            string image=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            string hash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(image)));
            var method=Assert.Single(PeInspector.Methods(image,hash,"NativeFixture.Divide").Rows);
            var client=new ProfilerClient();
            var inspect=new Grant("inspect",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,DateTimeOffset.UtcNow.AddMinutes(3),1);
            var selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=hash,signature=method.SignatureHex };
            var source=(await client.SendAsync(target,new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=process.Id,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L,grant=inspect,selector },CancellationToken.None)).Deserialize<NativeSnapshotReply>(Wire.Json)!;
            var references=PeInspector.ProbeReferences(image); var policy=NativeBodyPolicy.Resolve(source.OriginalBody,method,image);
            var locals=PeInspector.ProbeReturnSignature(image,policy.LocalCount);
            policy=policy with { Calls=[..policy.Calls!,new(references.EnterToken,1,false,true),new(references.ExitToken,1,false,true),
                new(references.ExceptionalExitToken,1,false,true)] };
            var fault=new FaultProbePolicy(references.ExceptionalExitToken,locals.Token,locals.Count,policy.LocalCount);
            var rewrite=CilFaultProbeRewriter.Rewrite(source.OriginalBody,policy,0,references.EnterToken,references.ExitToken,fault);
            string helperHash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(ProbeRuntime).Assembly.Location)));
            int section=(12+rewrite.Verification.CodeSize+3)&~3;
            foreach(string corruption in new[]{"fault_kind","try_range","handler_range","leave_target","return_local","local_signature","exceptional_reference"})
            {
                byte[] body=(byte[])rewrite.Body.Clone(); uint localToken=locals.Token,exceptional=references.ExceptionalExitToken;
                if(corruption=="fault_kind") body[section+4]=1;
                if(corruption=="try_range") body[section+8]=9;
                if(corruption=="handler_range") body[section+20]=12;
                if(corruption=="leave_target")
                {
                    var leave=rewrite.Verification.Instructions.First(i=>i.Leave);
                    int handler=BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(section+16));
                    BinaryPrimitives.WriteInt32LittleEndian(body.AsSpan(12+leave.Offset+1),handler-leave.Offset-leave.Length);
                }
                if(corruption=="return_local") body[12+rewrite.Verification.Instructions.First(i=>i.Opcode==0x13).Offset+1]++;
                if(corruption=="local_signature") localToken=0x1100ffff;
                if(corruption=="exceptional_reference") exceptional=references.ExitToken;
                string scope=method.ModuleMvid+"/"+method.Token+"/"+hash;
                var grant=new Grant(Wire.Id("invalid-probe"),workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InstallProbes,
                    DateTimeOffset.UtcNow.AddMinutes(3),1,Convert.ToHexStringLower(SHA256.HashData(body)),scope);
                var request=new { schema_version=Wire.Version,action="preflight",target_id=target.Id,process_id=process.Id,
                    start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L,grant,selector,transaction_id=Wire.Id("preflight"),
                    body,base_il_hash=source.OriginalBodyHash,probe=true,probe_id=0,helper_hash=helperHash,enter_token=references.EnterToken,
                    exit_token=references.ExitToken,exceptional_exit_token=exceptional,probe_local_signature=localToken,
                    probe_local_count=locals.Count,return_local=fault.ReturnLocal };
                var error=await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,request,CancellationToken.None));
                string expected=corruption=="exceptional_reference"?"invalid_request":"invalid_cil";
                Assert.True(error.Code==expected,corruption+": "+error.Code+": "+error.Message);
            }
            var status=await client.SendAsync(target,new ProfilerRequest(Wire.Version,"mutation_status",target.Id,process.Id,target.StartTimeUtcTicks,0),CancellationToken.None);
            Assert.Equal("0",status.GetProperty("epoch").GetString()); Assert.Equal(0,status.GetProperty("mutation_count").GetInt32());
            Assert.False(status.GetProperty("pending").GetBoolean()); Assert.False(status.GetProperty("tainted").GetBoolean());
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
