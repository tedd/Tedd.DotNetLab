using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Storage;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class TerminalLeaseTests
{
    [Theory,InlineData(OperationState.Completed),InlineData(OperationState.Failed),Case("UT-OP-004","FR-004,FR-038,FR-062","M6","Integration")]
    public void TerminalStateAndLeaseRelease_AreOneCatalogCommit(OperationState terminal)
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root);
        var first=catalog.Accept("test","first","first-hash","mutation","target");
        catalog.AcquireLease("target",first.Id); catalog.Transition(first.Id,OperationState.Running);
        catalog.Transition(first.Id,terminal,"evidence");
        Assert.Equal(terminal,catalog.GetOperation(first.Id).State);
        var second=catalog.Accept("test","second","second-hash","execution","target");
        catalog.AcquireLease("target",second.Id);
        catalog.ReleaseLease("target",first.Id,true); // Delayed cleanup cannot release the successor lease.
        catalog.ReleaseLease("target",first.Id,false); // Nor can a stale cleanup taint it.
        var third=catalog.Accept("test","third","third-hash","capture","target");
        Assert.Equal("lease_conflict",Assert.Throws<LabException>(()=>catalog.AcquireLease("target",third.Id)).Code);
        catalog.ReleaseLease("target",second.Id,true);
    }
    [Fact,Case("UT-OP-007","FR-004,FR-053","M6","Integration")]
    public void TaintedTerminal_AtomicallyExcludesLaterExecution()
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root);
        var first=catalog.Accept("test","first","first-hash","mutation","target");
        catalog.AcquireLease("target",first.Id); catalog.Transition(first.Id,OperationState.Running);
        catalog.Transition(first.Id,OperationState.Tainted,error:"restore_unknown");
        var later=catalog.Accept("test","later","later-hash","execution","target");
        Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>catalog.AcquireLease("target",later.Id)).Code);
    }
}
