using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Collector.EventPipe;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class DiscoveryPagingTests
{
    private sealed class Inventory : ITargetRegistry
    {
        public IReadOnlyList<TargetInfo> Discover()=>Enumerable.Range(1,137).Select(pid=>new TargetInfo(
            new("target-"+pid.ToString("d4"),pid,pid,"fixture","unknown","unknown"),"fixture",
            new Dictionary<string,Capability> { ["eventpipe"]=new(CapabilityState.Unknown,"fixture"),
                ["harness"]=new(CapabilityState.Unknown,"fixture"),["native_inspection"]=new(CapabilityState.Unsupported,"fixture") })).ToArray();
        public void Verify(TargetIdentity target,long epoch) { }
    }
    [Fact, Case("UT-ID-INVENTORY", "FR-001,FR-063", "M3", "Contract")]
    public async Task Discovery_SnapshotsEveryTargetAndPagesBeyondFirstHundred()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root,targetRegistry:new Inventory());
        Assert.Equal(137,service.Discover().Count);
        var tools=new Tedd.DotNetLab.Mcp.LabTools(service);
        var first=await tools.Discover(new Tedd.DotNetLab.Mcp.DiscoverTargets());
        Assert.Equal("ok",first.Status); var data=first.Data!.Value;
        Assert.Equal(100,data.GetProperty("rows").GetArrayLength()); Assert.True(data.GetProperty("truncated").GetBoolean());
        var second=await tools.Discover(new Tedd.DotNetLab.Mcp.DiscoverTargets(Cursor:data.GetProperty("next_cursor").GetString()));
        Assert.Equal("ok",second.Status); Assert.Equal(37,second.Data!.Value.GetProperty("rows").GetArrayLength());
        Assert.Contains(second.Data.Value.GetProperty("rows").EnumerateArray(),r=>r.GetProperty("target_id").GetString()=="target-0137");
    }
}
