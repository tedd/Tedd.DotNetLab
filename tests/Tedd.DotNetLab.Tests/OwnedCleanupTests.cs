using Tedd.DotNetLab.Artifacts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class OwnedCleanupTests
{
    [Fact,Case("IT-STORE-001","FR-072","M7","Integration")]
    public async Task TemporaryDeleteLock_RetriesUntilOwnedHandleReleases()
    {
        using var workspace=new TestWorkspace(); string directory=Path.Combine(workspace.Root,"owned-launch"); Directory.CreateDirectory(directory);
        string file=Path.Combine(directory,"locked.dll"); await File.WriteAllBytesAsync(file,[1,2,3]);
        var handle=new FileStream(file,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            var deletion=OwnedDirectoryCleanup.DeleteAsync(workspace.Root,directory);
            Assert.False(deletion.IsCompleted); Assert.True(Directory.Exists(directory));
            handle.Dispose(); await deletion.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(Directory.Exists(directory));
        }
        finally { handle.Dispose(); }
    }
    [Fact,Case("UT-SEC-006","FR-071,FR-072","M7","Unit")]
    public async Task CleanupCannotDeleteItsOwnerOrSibling()
    {
        using var workspace=new TestWorkspace();
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OwnedDirectoryCleanup.DeleteAsync(workspace.Root,workspace.Root));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>OwnedDirectoryCleanup.DeleteAsync(workspace.Root,workspace.Root+"-sibling"));
        Assert.True(Directory.Exists(workspace.Root));
    }
}
