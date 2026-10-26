using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Storage;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class SnapshotAcquisitionTests
{
    [Fact,Case("UT-NAT-007,IT-NAT-004","FR-040,FR-041,FR-043","M4","Unit")]
    public async Task TransientVersionRejection_ReturnsOnlyTheFinalConsistentRead()
    {
        int reads=0; var rejected=new List<int>();
        var reply=new NativeSnapshotReply([],"method","type",[],"hash",new(false,true,true),false);
        var actual=await NativeSnapshotAcquirer.AcquireAsync(_=>
        {
            reads++; if(reads<3) throw new LabException("version_changed","Rejected unstable acquisition.");
            return Task.FromResult(reply);
        },rejected:(attempt,_)=>rejected.Add(attempt));
        Assert.Same(reply,actual); Assert.Equal(3,reads); Assert.Equal(new[]{1,2},rejected);
    }
    [Fact,Case("UT-NAT-007","FR-040,FR-041","M4","Unit")]
    public async Task PersistentVersionChanges_ExhaustTheBoundWithoutInventingAResult()
    {
        int reads=0;
        Assert.Equal("version_changed",(await Assert.ThrowsAsync<LabException>(()=>NativeSnapshotAcquirer.AcquireAsync(_=>
        { reads++; throw new LabException("version_changed","Still unstable."); }))).Code);
        Assert.Equal(3,reads);
    }
    [Theory,InlineData("target_identity_changed"),InlineData("stale_epoch"),InlineData("permission_denied"),Case("UT-ID-002,UT-SEC-001","FR-002,FR-040,FR-070","M4","Unit")]
    public async Task OtherFailures_AreNotRetried(string code)
    {
        int reads=0;
        Assert.Equal(code,(await Assert.ThrowsAsync<LabException>(()=>NativeSnapshotAcquirer.AcquireAsync(_=>
        { reads++; throw new LabException(code,"Rejected."); }))).Code);
        Assert.Equal(1,reads);
    }
    [Fact,Case("UT-OP-011","FR-038,FR-043","M4","Unit")]
    public async Task CancellationBetweenAcquisitions_PreventsAnotherRead()
    {
        using var cancellation=new CancellationTokenSource(); int reads=0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>NativeSnapshotAcquirer.AcquireAsync(_=>
        { reads++; throw new LabException("version_changed","Rejected."); },cancellation.Token,(_,_)=>cancellation.Cancel()));
        Assert.Equal(1,reads);
    }
    [Fact,Case("UT-SEC-001,UT-SEC-004","FR-040,FR-070","M4","Integration")]
    public async Task RejectedAttemptsConsumeTheGrantedBudget_AndCannotExtendIt()
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root);
        var grant=new Grant("one-read",workspace.Root,"target",1,Permission.InspectLiveCode,DateTimeOffset.UtcNow.AddMinutes(1),1);
        catalog.Put(grant.Id,"grant",grant); int sent=0;
        var failure=await Assert.ThrowsAsync<LabException>(()=>NativeSnapshotAcquirer.AcquireAsync(_=>
        {
            catalog.UseGrant(grant); sent++;
            throw new LabException("version_changed","Rejected read still consumed one attempt.");
        }));
        Assert.Equal("permission_denied",failure.Code); Assert.Equal(1,sent);
    }
}
