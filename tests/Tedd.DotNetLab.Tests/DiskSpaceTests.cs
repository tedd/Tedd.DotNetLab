using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Application;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class DiskSpaceTests
{
    [Fact,Case("UT-CAP-013,UT-ART-010","FR-016,FR-072","M1","Unit")]
    public void ReserveArithmeticCannotOverflowIntoAdmission()
    {
        Assert.True(DiskSpace.Fits(100,60,40)); Assert.False(DiskSpace.Fits(99,60,40));
        Assert.False(DiskSpace.Fits(100,1,long.MaxValue)); Assert.False(DiskSpace.Fits(long.MaxValue,long.MaxValue,1));
        Assert.Equal(long.MaxValue,DiskSpace.Bytes(ulong.MaxValue,4096));
        Assert.Equal(8192,DiskSpace.Bytes(2,4096));
        Assert.Throws<LabException>(()=>DiskSpace.Fits(100,-1,0)); Assert.Throws<LabException>(()=>DiskSpace.Bytes(1,0));
    }
    [Fact,Case("IT-PLAT-DISK-RESERVE","FR-016,FR-070,FR-072","M7","Integration")]
    public async Task MountedWorkspaceCapacityControlsImportAdmission()
    {
        using var workspace=new TestWorkspace(); long available=DiskSpace.AvailableBytes(workspace.Root);
        Assert.True(available>0);
        if(OperatingSystem.IsLinux()) Assert.InRange(available,1,1073741824);
        var limits=new ResourceLimits(DiskReserveBytes:available+1);
        await using var service=new LabService(workspace.Root,limits);
        string source=Path.Combine(workspace.Root,"input.json"); await File.WriteAllTextAsync(source,"{}");
        Assert.Equal("resource_limit",(await Assert.ThrowsAsync<LabException>(()=>service.ImportAsync(source,"application/json"))).Code);
        Assert.Empty(service.Catalog.List("artifact").Rows);
    }
}
