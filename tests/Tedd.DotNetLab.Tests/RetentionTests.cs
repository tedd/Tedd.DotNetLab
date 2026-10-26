using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class RetentionTests
{
    private static async Task<Artifact> Create(LabService service,byte value,string media="application/vnd.microsoft.nettrace")
    {
        string path=service.Artifacts.CreateIncoming(Wire.Id("retention-fixture"));
        await File.WriteAllBytesAsync(path,Enumerable.Repeat(value,128).ToArray());
        var artifact=await service.Artifacts.SealAsync(path,media,"retention-fixture/v1",true,1024);
        service.Catalog.Put(artifact.Id,"artifact",artifact); return artifact;
    }
    [Fact,Case("UT-ART-008","FR-031,FR-072","M3","Integration")]
    public async Task Retention_PreservesPinsAndOwnerReferencesAndReportsQuotaPressure()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var pinned=await Create(service,1); var owned=await Create(service,2); var expired=await Create(service,3);
        var summary=await Create(service,4,"application/json");
        service.Catalog.Pin(pinned.Id,"baseline_input");
        var owner=service.Hypothesis("retained trace","reference",[]); service.PinEvidence(owned.Id,owner.Id);
        var plan=service.PrepareRetention(new(0,0));
        Assert.Equal(384,plan.RetainedRawTraceBytes); Assert.Equal(256,plan.ProjectedRawTraceBytes); Assert.True(plan.QuotaPressure);
        Assert.Equal(new[]{expired.Id},plan.Artifacts.Select(a=>a.Id)); Assert.Contains(pinned.Id,plan.ProtectedArtifactIds!); Assert.Contains(owned.Id,plan.ProtectedArtifactIds!);
        var grant=service.IssueDeletionGrant(plan.Id,120,1); var operation=service.StartDeletion(plan.Id,grant.Id,"retention");
        var complete=await ServiceTests.Wait(service,operation.Id); Assert.Equal(OperationState.Completed,complete.State);
        Assert.Equal(operation.Id,service.StartDeletion(plan.Id,grant.Id,"retention").Id);
        Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>service.Artifacts.VerifiedPathAsync(expired))).Code);
        await service.Artifacts.VerifiedPathAsync(pinned); await service.Artifacts.VerifiedPathAsync(owned); await service.Artifacts.VerifiedPathAsync(summary);
        Assert.Equal(expired,service.Catalog.Get<Artifact>(expired.Id)); Assert.Equal("deleted",service.Catalog.Retention(expired.Id).DeletionState);
        Assert.DoesNotContain(expired.Id,service.PrepareRetention(new(0,0)).Artifacts.Select(a=>a.Id));
    }
    [Fact,Case("UT-ART-011","FR-072","M3","Integration")]
    public async Task TwoOwners_AreIndependentAndRetentionPromisesCannotBeShortened()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var artifact=await Create(service,7);
        var first=service.Hypothesis("first","retention",[]); var second=service.Hypothesis("second","retention",[]);
        service.Catalog.RetainArtifact(artifact.Id,first.Id);
        service.Catalog.RetainArtifact(artifact.Id,second.Id);
        service.Catalog.RetainArtifact(artifact.Id,first.Id,DateTimeOffset.UtcNow.AddDays(1));
        Assert.Equal(2,service.Catalog.Retention(artifact.Id).RetainedOwners);
        Assert.Equal("evidence_protected",Assert.Throws<LabException>(()=>service.PrepareDeletion([artifact.Id],"explicit removal")).Code);
        Assert.Equal("record_not_found",Assert.Throws<LabException>(()=>service.PinEvidence(artifact.Id,"unknown-owner")).Code);
    }
    [Fact,Case("UT-SEC-011","FR-070,FR-072","M3","Integration")]
    public async Task PinAfterPlanning_PreventsEveryDeletionInTheBatch()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var first=await Create(service,10); var second=await Create(service,11);
        var plan=service.PrepareDeletion([first.Id,second.Id],"operator review"); var grant=service.IssueDeletionGrant(plan.Id,120,1);
        service.Catalog.Pin(second.Id,"confirmation_input");
        var failed=await ServiceTests.Wait(service,service.StartDeletion(plan.Id,grant.Id,"pin-race").Id);
        Assert.Equal(OperationState.Failed,failed.State); Assert.Equal("evidence_protected",failed.ErrorCode);
        Assert.Null(service.Catalog.Retention(first.Id).DeletionState); Assert.Null(service.Catalog.Retention(second.Id).DeletionState);
        await service.Artifacts.VerifiedPathAsync(first); await service.Artifacts.VerifiedPathAsync(second);
        Assert.Empty(service.Catalog.List("deletion_result").Rows);
    }
    [Theory,InlineData(false),InlineData(true),Case("UT-ART-003","FR-072","M3","Integration")]
    public async Task Restart_CompletesCommittedDeletionBeforeOrAfterFilesystemRemoval(bool alreadyRemoved)
    {
        using var workspace=new TestWorkspace(); Artifact artifact; string summaryId;
        await using(var service=new LabService(workspace.Root))
        {
            artifact=await Create(service,12); var summary=service.Hypothesis("stored summary","historical decision",[]); summaryId=summary.Id;
            var plan=service.PrepareDeletion([artifact.Id],"crash fixture");
            service.Catalog.BeginArtifactDeletion(plan.Id,plan.ContentHash,[artifact.Id],service.IssueDeletionGrant(plan.Id,120,1));
            Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>service.Artifacts.VerifiedPathAsync(artifact))).Code);
            Assert.Equal("artifact_expired",Assert.Throws<LabException>(()=>service.Catalog.Pin(artifact.Id,"late-pin")).Code);
            Assert.Equal("artifact_expired",Assert.Throws<LabException>(()=>service.PinEvidence(artifact.Id,summary.Id)).Code);
            if(alreadyRemoved) service.Artifacts.Delete(artifact);
        }
        await using var recovered=new LabService(workspace.Root);
        Assert.Empty(recovered.Catalog.PendingArtifactDeletions()); Assert.Equal("deleted",recovered.Catalog.Retention(artifact.Id).DeletionState);
        Assert.Equal("historical decision",recovered.Catalog.Get<Hypothesis>(summaryId).Prediction);
        string physical=Path.Combine(workspace.Root,"artifacts","sha256",artifact.Hash[..2],artifact.Hash); Assert.False(File.Exists(physical));
        Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>recovered.Artifacts.VerifiedPathAsync(artifact))).Code);
        string incoming=recovered.Artifacts.CreateIncoming("resurrection"); await File.WriteAllBytesAsync(incoming,Enumerable.Repeat((byte)12,128).ToArray());
        Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>recovered.Artifacts.SealAsync(incoming,artifact.MediaType,"fixture",true,1024))).Code);
    }
    [Fact,Case("UT-SEC-007","FR-070,FR-072","M3","Integration")]
    public async Task DeleteEvidence_RequiresExactPermissionScopeExpiryAndOperationBudget()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var artifact=await Create(service,13); var plan=service.PrepareDeletion([artifact.Id],"selected bytes");
        var grant=service.IssueDeletionGrant(plan.Id,120,1);
        foreach(var changed in new[]{grant with { Permission=Permission.Observe },grant with { ContentHash=Wire.Hash("other selection") },grant with { ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1) }})
        {
            var wrong=changed with { Id=Wire.Id("grant") }; service.Catalog.Put(wrong.Id,"grant",wrong);
            Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartDeletion(plan.Id,wrong.Id,wrong.Id)).Code);
        }
        var tools=new LabTools(service);
        var prepared=await tools.Experiment(new PrepareRetention("0",0)); Assert.Equal("ok",prepared.Status);
        var queried=await tools.Query(new QueryRetention(artifact.Id)); Assert.Equal("ok",queried.Status);
        var operation=service.StartDeletion(plan.Id,grant.Id,"authorized"); Assert.Equal(OperationState.Completed,(await ServiceTests.Wait(service,operation.Id)).State);
        var exhausted=await ServiceTests.Wait(service,service.StartDeletion(plan.Id,grant.Id,"second-use").Id);
        Assert.Equal("permission_denied",exhausted.ErrorCode);
    }
    [Fact,Case("UT-ART-011","FR-072","M3","Integration")]
    public async Task TimedOwner_ExpiresWithoutErasingItsRecord()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var artifact=await Create(service,14); var owner=service.Hypothesis("finite retention","stored record",[]);
        var until=DateTimeOffset.UtcNow.AddSeconds(1); service.Catalog.RetainArtifact(artifact.Id,owner.Id,until);
        Assert.Equal(1,service.Catalog.Retention(artifact.Id).RetainedOwners);
        await Task.Delay(until-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(100));
        Assert.Equal(0,service.Catalog.Retention(artifact.Id).RetainedOwners);
        var plan=service.PrepareDeletion([artifact.Id],"expired reference"); Assert.Single(plan.Artifacts);
        Assert.Equal(Wire.Hash(owner),service.Catalog.Get(owner.Id).Hash);
    }
}
