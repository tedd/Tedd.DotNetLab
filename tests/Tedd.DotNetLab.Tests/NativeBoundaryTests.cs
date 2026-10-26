using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class NativeBoundaryTests
{
    [Fact, Case("IT-NAT-BOUNDARIES", "FR-040,FR-041,FR-074", "M4", "Integration")]
    public async Task Snapshot_UnsupportedAndUncompiledMethodsFailClosed()
    {
        using var workspace=new TestWorkspace();
        using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var methods=PeInspector.Methods(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"),"fixture","NativeFixture").Rows;
            var grant=new Grant("boundary",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.InspectLiveCode,
                DateTimeOffset.UtcNow.AddMinutes(1),10);
            object Request(string name,string? hash=null)=>new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,
                process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L,grant,
                selector=hash is null ? (object)new { module_mvid=methods.Single(m=>m.Name==name).ModuleMvid,
                    method_token=methods.Single(m=>m.Name==name).Token } : new { module_mvid=methods.Single(m=>m.Name==name).ModuleMvid,
                    method_token=methods.Single(m=>m.Name==name).Token,module_hash=hash } };
            var client=new ProfilerClient();
            Assert.Equal("capability_unsupported",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,
                Request("Generic"),CancellationToken.None))).Code);
            Assert.Equal("not_jitted",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,
                Request("NeverInvoked"),CancellationToken.None))).Code);
            Assert.Equal("module_identity_changed",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,
                Request("Value",new string('0',64)),CancellationToken.None))).Code);
            Assert.Equal("invalid_request",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,
                new { schema_version=Wire.Version,action="handshake",target_id=target.Id,process_id=target.ProcessId,
                    start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0,unknown=true },CancellationToken.None))).Code);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
