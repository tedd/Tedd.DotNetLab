using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Artifacts;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ArtifactRecoveryTests
{
    private static async Task<Artifact> Create(LabService service,byte value,bool link=true)
    {
        string path=service.Artifacts.CreateIncoming(Wire.Id("recovery-fixture")); await File.WriteAllBytesAsync(path,Enumerable.Repeat(value,32).ToArray());
        var artifact=await service.Artifacts.SealAsync(path,"application/json","recovery-fixture/v1",true,1024);
        if(link) service.Catalog.Put(artifact.Id,"artifact",artifact); return artifact;
    }
    private static async Task<ArtifactRecoveryReport> Recover(LabService service,string key="inventory",int entries=10000,long bytes=1048576)
    {
        var operation=service.StartArtifactRecovery(key,entries,bytes,60);
        var completed=await ServiceTests.Wait(service,operation.Id);
        Assert.True(completed.State==OperationState.Completed,Wire.Serialize(completed));
        return await service.ArtifactRecoveryAsync(completed.ResultId!);
    }
    [Theory,InlineData(SealStage.BeforeFlush),InlineData(SealStage.ReceiptFlushed),InlineData(SealStage.Moved),Case("UT-ART-002,UT-ART-003","FR-011,FR-072","M3","Integration")]
    public async Task SealingInterruption_RecoversOnlyMovedBytesWithFlushedMetadata(SealStage fault)
    {
        using var workspace=new TestWorkspace();
        var store=new ArtifactStore(workspace.Root,sealStage:stage=> { if(stage==fault) throw new IOException("injected seal boundary interruption"); });
        string incoming=store.CreateIncoming("interrupted"); await File.WriteAllBytesAsync(incoming,[1,2,3,4]);
        await Assert.ThrowsAsync<IOException>(()=>store.SealAsync(incoming,"application/json","original-producer/v1",false,1024));
        await using var service=new LabService(workspace.Root);
        var report=await Recover(service); var manifests=service.Catalog.List("artifact").Rows.Select(r=>Wire.Deserialize<Artifact>(r.Json)).Where(a=>a.Producer=="original-producer/v1").ToArray();
        if(fault==SealStage.Moved)
        {
            var recovered=Assert.Single(manifests); Assert.False(recovered.Complete); Assert.Equal("application/json",recovered.MediaType);
            await service.Artifacts.VerifiedPathAsync(recovered); Assert.Contains(report.Findings,f=>f.Integrity=="recovered_manifest");
            Assert.False(File.Exists(incoming));
        }
        else
        {
            Assert.Empty(manifests); Assert.True(File.Exists(incoming)); Assert.Contains(report.Findings,f=>f.Kind=="partial_write");
            if(fault==SealStage.ReceiptFlushed) Assert.Contains(report.Findings,f=>f.Integrity=="receipt_before_move");
        }
        Assert.Empty(service.Catalog.List("capture").Rows); Assert.Empty(service.Catalog.List("run").Rows);
    }
    [Fact,Case("UT-ART-003","FR-072","M3","Integration")]
    public async Task BareOrphan_IsVerifiedWithoutInventingMetadataOrOwnership()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var orphan=await Create(service,5,false);
        var receipt=Assert.Single(service.Artifacts.RecoveryFiles(100),e=>e.Kind=="seal_receipt");
        File.Delete(Path.Combine(workspace.Root,receipt.RelativePath));
        var report=await Recover(service);
        Assert.Contains(report.Findings,f=>f.Kind=="orphan_file"&&f.Integrity=="orphan_unclaimed");
        Assert.Equal("record_not_found",Assert.Throws<LabException>(()=>service.Catalog.Get(orphan.Id)).Code);
        Assert.True(File.Exists(await service.Artifacts.VerifiedPathAsync(orphan)));
    }
    [Fact,Case("UT-ART-004,UT-ART-005,UT-ART-009","FR-021,FR-072","M3","Integration")]
    public async Task Inventory_DistinguishesMissingCorruptAndAuthorizedExpiry()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var missing=await Create(service,7); var corrupt=await Create(service,8); var expired=await Create(service,9);
        service.Artifacts.Delete(missing); await File.WriteAllBytesAsync(await service.Artifacts.VerifiedPathAsync(corrupt),[0]);
        var deletion=service.PrepareDeletion([expired.Id],"fixture expiry"); var grant=service.IssueDeletionGrant(deletion.Id,120,1);
        Assert.Equal(OperationState.Completed,(await ServiceTests.Wait(service,service.StartDeletion(deletion.Id,grant.Id,"expiry").Id)).State);
        var report=await Recover(service);
        Assert.Contains(report.Findings,f=>f.Identity==missing.Id&&f.Integrity=="artifact_missing");
        Assert.Contains(report.Findings,f=>f.Identity==corrupt.Id&&f.Integrity=="artifact_corrupt");
        Assert.Contains(report.Findings,f=>f.Identity==expired.Id&&f.Integrity=="artifact_expired");
        Assert.DoesNotContain(report.Findings,f=>f.Integrity=="recovered_manifest");
        Assert.Equal("deleted",service.Catalog.Retention(expired.Id).DeletionState);
    }
    [Fact,Case("UT-ART-005","FR-072","M3","Integration")]
    public async Task InvalidReceipt_CannotPromoteAnUnclaimedFile()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var orphan=await Create(service,10,false);
        var entry=Assert.Single(service.Artifacts.RecoveryFiles(100),e=>e.Kind=="seal_receipt");
        var receipt=await service.Artifacts.ReadSealReceiptAsync(entry,CancellationToken.None);
        await File.WriteAllTextAsync(Path.Combine(workspace.Root,entry.RelativePath),Wire.Serialize(receipt with { ManifestHash=Wire.Hash("tampered") }));
        var report=await Recover(service);
        Assert.Contains(report.Findings,f=>f.Integrity=="receipt_invalid");
        Assert.Equal("record_not_found",Assert.Throws<LabException>(()=>service.Catalog.Get(orphan.Id)).Code);
    }
    [Fact,Case("UT-ART-003","FR-072","M3","Integration")]
    public async Task RecoveredManifestAndReport_PersistAcrossControllerRestart()
    {
        using var workspace=new TestWorkspace(); Artifact artifact; string reportId;
        await using(var service=new LabService(workspace.Root))
        {
            artifact=await Create(service,12,false); var report=await Recover(service); reportId=report.Id;
            Assert.Equal(artifact,service.Catalog.Get<Artifact>(artifact.Id));
            Assert.Contains(report.Findings,f=>f.Integrity=="recovered_manifest");
        }
        await using var reopened=new LabService(workspace.Root);
        Assert.Equal(artifact,reopened.Catalog.Get<Artifact>(artifact.Id));
        Assert.Contains((await reopened.ArtifactRecoveryAsync(reportId)).Findings,f=>f.Integrity=="recovered_manifest");
        Assert.True(reopened.Catalog.IsPinned(reopened.Catalog.Get<ArtifactRecoveryIndex>(reportId).ArtifactId));
        var second=await Recover(reopened,"second-inventory");
        Assert.DoesNotContain(second.Findings,f=>f.Integrity=="recovered_manifest");
        Assert.Contains(second.Findings,f=>f.Identity==artifact.Id&&f.Integrity=="verified");
    }
    [Fact,Case("UT-ART-007","FR-016,FR-072","M3","Integration")]
    public async Task VerificationBudgets_ProduceExplicitIncompleteCoverageAndIdempotentResult()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var artifact=await Create(service,11);
        var report=await Recover(service,"byte-budget",bytes:1);
        Assert.True(report.Truncated); Assert.Equal(0,report.VerifiedBytes);
        Assert.Contains(report.Findings,f=>f.Identity==artifact.Id&&f.Integrity=="not_verified_budget");
        var first=service.StartArtifactRecovery("byte-budget",10000,1,60);
        Assert.Equal(report.Id,first.ResultId);
        var tools=new LabTools(service); Assert.Equal("ok",(await tools.Query(new QueryArtifactRecovery(report.Id,1))).Status);
        var bounded=await Recover(service,"entry-budget",entries:1);
        Assert.True(bounded.Truncated); Assert.Equal(1,bounded.VisitedEntries);
        Assert.Equal("resource_limit",Assert.Throws<LabException>(()=>service.StartArtifactRecovery("invalid",maximumEntries:10001)).Code);
    }
    [Fact,Case("UT-ART-006","FR-070,FR-072","M3","Unit")]
    public async Task ReceiptReader_RejectsCallerSuppliedTraversalAndOversizedMetadata()
    {
        using var workspace=new TestWorkspace(); var store=new ArtifactStore(workspace.Root);
        var escape=new RecoveryFile("../outside.partial.seal.json","seal_receipt",100);
        Assert.Equal("invalid_path",(await Assert.ThrowsAsync<LabException>(()=>store.ReadSealReceiptAsync(escape,CancellationToken.None))).Code);
        Assert.Equal("invalid_path",Assert.Throws<LabException>(()=>store.ReceiptHasPartial(escape)).Code);
        string incoming=store.CreateIncoming("oversized"); await File.WriteAllBytesAsync(incoming+".seal.json",new byte[16385]);
        var entry=Assert.Single(store.RecoveryFiles(100),e=>e.Kind=="seal_receipt");
        Assert.Equal("artifact_corrupt",(await Assert.ThrowsAsync<LabException>(()=>store.ReadSealReceiptAsync(entry,CancellationToken.None))).Code);
    }
}
