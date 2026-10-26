using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Storage;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CatalogFileSystemTests
{
    [Theory,InlineData(0xEF53L),InlineData(0x58465342L),InlineData(0x9123683EL),InlineData(0x794c7630L),
        Case("UT-LINUX-CATALOG-FILESYSTEM","FR-070,FR-072","M7","Unit")]
    public void NativeLocalFilesystemIdentityPermitsCatalogAdmission(long type)=>CatalogFileSystem.ValidateLinuxType(type);

    [Theory,InlineData(0x01021997L),InlineData(0x6969L),InlineData(0x517BL),InlineData(0xFF534D42L),InlineData(0xFE534D42L),
        InlineData(0x65735546L),InlineData(0x01021994L),InlineData(0L),InlineData(-1L),
        Case("UT-LINUX-CATALOG-FILESYSTEM","FR-070,FR-072","M7","Unit")]
    public void UnqualifiedFilesystemCannotOpenWalCatalog(long type)=>Assert.Equal("catalog_filesystem_unsupported",
        Assert.Throws<LabException>(()=>CatalogFileSystem.ValidateLinuxType(type)).Code);

    [Fact,Case("IT-LINUX-CATALOG-FILESYSTEM","FR-070,FR-072","M7","Integration")]
    public void TaskCatalogUsesItsActualFilesystemBeforeDatabaseCreation()
    {
        using var workspace=new TestWorkspace(); CatalogFileSystem.Validate(workspace.Root);
        if(OperatingSystem.IsLinux())
            Assert.Equal("catalog_filesystem_unsupported",Assert.Throws<LabException>(()=>
                CatalogFileSystem.Validate(Path.Combine(TestWorkspace.TaskRoot,"bin"))).Code);
        using var catalog=new Catalog(workspace.Root); var value=catalog.Put("filesystem","hypothesis",new { value=1 });
        Assert.Equal(value.Hash,catalog.Get("filesystem").Hash);
    }
}
