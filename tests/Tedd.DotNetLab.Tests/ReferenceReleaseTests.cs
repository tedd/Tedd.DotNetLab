using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Storage;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ReferenceReleaseTests
{
    private static async Task<Artifact> Create(LabService service,byte value)
    {
        string incoming=service.Artifacts.CreateIncoming(Wire.Id("release-fixture")); await File.WriteAllBytesAsync(incoming,[value]);
        var artifact=await service.Artifacts.SealAsync(incoming,"application/vnd.microsoft.nettrace","release-fixture/v1",true,1024);
        service.Catalog.Put(artifact.Id,"artifact",artifact); return artifact;
    }
    private static async Task<Operation> Release(LabService service,ReferenceReleasePlan plan,string key)
    {
        var grant=service.IssueReferenceReleaseGrant(plan.Id,120,1);
        return await ServiceTests.Wait(service,service.StartReferenceRelease(plan.Id,grant.Id,key).Id);
    }
    [Fact,Case("UT-ART-011","FR-070,FR-072","M3","Integration")]
    public async Task ReleasingOneOwner_PreservesOtherOwnerAndRequiresSeparateByteDeletion()
    {
        using var workspace=new TestWorkspace(); string resultId,artifactId;
        await using(var service=new LabService(workspace.Root))
        {
            var artifact=await Create(service,1); artifactId=artifact.Id;
            var first=service.Hypothesis("first owner","retain",[]); var second=service.Hypothesis("second owner","retain",[]);
            service.PinEvidence(artifact.Id,first.Id); service.PinEvidence(artifact.Id,second.Id);
            var tools=new LabTools(service); var response=await tools.Experiment(new PrepareReferenceRelease([new(artifact.Id,first.Id)],"first owner consent"));
            Assert.Equal("ok",response.Status); var plan=Wire.Deserialize<ReferenceReleasePlan>(response.Data!.Value.GetRawText());
            var grant=service.IssueReferenceReleaseGrant(plan.Id,120,1); var operation=service.StartReferenceRelease(plan.Id,grant.Id,"first-release");
            var completed=await ServiceTests.Wait(service,operation.Id); Assert.Equal(OperationState.Completed,completed.State); resultId=completed.ResultId!;
            Assert.Equal(operation.Id,service.StartReferenceRelease(plan.Id,grant.Id,"first-release").Id);
            Assert.Equal(1,service.Catalog.Retention(artifact.Id).RetainedOwners); Assert.Equal(second.Id,Assert.Single(service.Catalog.References(artifact.Id).Rows).OwnerId);
            await service.Artifacts.VerifiedPathAsync(artifact);
            Assert.Equal("evidence_protected",Assert.Throws<LabException>(()=>service.PrepareDeletion([artifact.Id],"bytes")).Code);
            var last=service.PrepareReferenceRelease([new(artifact.Id,second.Id)],"last user reference");
            Assert.Equal(OperationState.Completed,(await Release(service,last,"second-release")).State);
            Assert.Equal(0,service.Catalog.Retention(artifact.Id).RetainedOwners); await service.Artifacts.VerifiedPathAsync(artifact);
            var deletion=service.PrepareDeletion([artifact.Id],"review raw removal");
            Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartDeletion(deletion.Id,grant.Id,"wrong-grant")).Code);
        }
        await using var reopened=new LabService(workspace.Root); var result=reopened.Catalog.Get<ReferenceReleaseResult>(resultId);
        Assert.Equal(1,result.ReleasedReferences); Assert.Empty(reopened.Catalog.References(artifactId).Rows);
        await reopened.Artifacts.VerifiedPathAsync(reopened.Catalog.Get<Artifact>(artifactId));
    }
    [Fact,Case("UT-SEC-011","FR-031,FR-070,FR-072","M3","Integration")]
    public async Task UserReferenceRelease_CannotReleaseSystemBaselinePin()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var artifact=await Create(service,2); var owner=service.Hypothesis("owner","retain",[]);
        service.Catalog.Pin(artifact.Id,"baseline_input");
        Assert.Equal("retention_reference_not_found",Assert.Throws<LabException>(()=>service.PrepareReferenceRelease([new(artifact.Id,owner.Id)],"system pin removal")).Code);
        service.PinEvidence(artifact.Id,owner.Id);
        var plan=service.PrepareReferenceRelease([new(artifact.Id,owner.Id)],"user reference only");
        Assert.Equal(OperationState.Completed,(await Release(service,plan,"user-reference")).State);
        Assert.True(service.Catalog.IsPinned(artifact.Id)); Assert.Equal(0,service.Catalog.Retention(artifact.Id).RetainedOwners);
        Assert.Equal("evidence_protected",Assert.Throws<LabException>(()=>service.PrepareDeletion([artifact.Id],"cannot remove baseline")).Code);
        await service.Artifacts.VerifiedPathAsync(artifact);
    }
    [Theory,InlineData("repin"),InlineData("extend"),InlineData("owner_revision"),Case("UT-ART-011","FR-070,FR-072","M3","Integration")]
    public async Task ChangedReferenceOrOwner_InvalidatesEntireReviewedBatch(string change)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var a=await Create(service,3); var b=await Create(service,4); var owner=service.Hypothesis("owner","retain",[]);
        service.Catalog.RetainArtifact(a.Id,owner.Id); service.Catalog.RetainArtifact(b.Id,owner.Id,DateTimeOffset.UtcNow.AddDays(1));
        var plan=service.PrepareReferenceRelease([new(a.Id,owner.Id),new(b.Id,owner.Id)],"batch consent"); var grant=service.IssueReferenceReleaseGrant(plan.Id,120,1);
        if(change=="repin") service.PinEvidence(b.Id,owner.Id);
        else if(change=="extend") service.Catalog.RetainArtifact(b.Id,owner.Id,DateTimeOffset.UtcNow.AddDays(2));
        else service.Hypothesis("revised owner","retain",[],owner.Id,1);
        var failed=await ServiceTests.Wait(service,service.StartReferenceRelease(plan.Id,grant.Id,"stale-batch").Id);
        Assert.Equal(OperationState.Failed,failed.State); Assert.Equal("stale_retention_reference",failed.ErrorCode);
        Assert.Equal(1,service.Catalog.Retention(a.Id).RetainedOwners); Assert.Equal(1,service.Catalog.Retention(b.Id).RetainedOwners);
        Assert.Empty(service.Catalog.List("reference_release_result").Rows);
        service.Catalog.UseGrant(grant); // A rolled-back batch did not consume its operation budget.
    }
    [Fact,Case("UT-ART-011","FR-070,FR-072","M3","Integration")]
    public async Task ReleasedThenRecreatedReference_CannotBeRemovedByItsOldGrant()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var artifact=await Create(service,5); var owner=service.Hypothesis("owner","retain",[]); service.PinEvidence(artifact.Id,owner.Id);
        var old=service.PrepareReferenceRelease([new(artifact.Id,owner.Id)],"old consent"); var oldGrant=service.IssueReferenceReleaseGrant(old.Id,120,1);
        var current=service.PrepareReferenceRelease([new(artifact.Id,owner.Id)],"current consent"); Assert.Equal(OperationState.Completed,(await Release(service,current,"current")).State);
        service.PinEvidence(artifact.Id,owner.Id); Assert.Equal(3,service.Catalog.ReferenceSnapshot(artifact.Id,owner.Id).ReferenceRevision);
        var stale=await ServiceTests.Wait(service,service.StartReferenceRelease(old.Id,oldGrant.Id,"old-grant").Id);
        Assert.Equal("stale_retention_reference",stale.ErrorCode); Assert.Equal(1,service.Catalog.Retention(artifact.Id).RetainedOwners);
    }
    [Fact,Case("UT-SEC-007","FR-070,FR-072","M3","Integration")]
    public async Task ReleaseGrant_RejectsWrongPermissionHashExpiryAndChangedSelection()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var artifact=await Create(service,6); var owner=service.Hypothesis("owner","retain",[]); service.PinEvidence(artifact.Id,owner.Id);
        var plan=service.PrepareReferenceRelease([new(artifact.Id,owner.Id)],"exact selection"); var grant=service.IssueReferenceReleaseGrant(plan.Id,120,1);
        foreach(var changed in new[]{grant with { Permission=Permission.Observe },grant with { ContentHash=Wire.Hash("other") },grant with { ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1) }})
        {
            var wrong=changed with { Id=Wire.Id("grant") }; service.Catalog.Put(wrong.Id,"grant",wrong);
            Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartReferenceRelease(plan.Id,wrong.Id,wrong.Id)).Code);
        }
        var altered=plan.References.Select(r=>r with { OwnerHash=Wire.Hash("tampered") }).ToArray();
        Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.Catalog.ReleaseArtifactReferences(plan.Id,plan.ContentHash,altered,grant,"irrelevant-operation")).Code);
        Assert.Equal(1,service.Catalog.Retention(artifact.Id).RetainedOwners);
    }
    [Fact,Case("UT-ART-011","FR-062,FR-072","M3","Integration")]
    public async Task ReferencePaging_IsBoundToArtifactAndFreezesEachPageRead()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var artifact=await Create(service,7); var other=await Create(service,8);
        for(int i=0;i<3;i++) { var owner=service.Hypothesis("owner "+i,"retain",[]); service.PinEvidence(artifact.Id,owner.Id); }
        var first=service.Catalog.References(artifact.Id,1); Assert.True(first.Truncated);
        var second=service.Catalog.References(artifact.Id,2,first.NextCursor); Assert.False(second.Truncated); Assert.Equal(2,second.Rows.Count);
        Assert.DoesNotContain(second.Rows,r=>r.OwnerId==first.Rows[0].OwnerId);
        Assert.Equal("invalid_cursor",Assert.Throws<LabException>(()=>service.Catalog.References(other.Id,1,first.NextCursor)).Code);
        Assert.Equal("ok",(await new LabTools(service).Query(new QueryArtifactReferences(artifact.Id,1))).Status);
    }
    [Fact,Case("UT-ART-014","FR-072,FR-074","M3","Integration")]
    public void SchemaThreeReferences_ReceivePersistentRevisionsDuringMigration()
    {
        using var workspace=new TestWorkspace();
        var legacyArtifact=new Artifact("art-"+new string('a',64),new string('a',64),1,"application/json",Wire.Version,"legacy",true);
        var legacyOwner=new Hypothesis("legacy-owner","legacy mechanism","retain","proposed",[]);
        // All schema and fixture writes resolve to this unique local task-owned SQLite database.
        using(var db=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(workspace.Root,"catalog.sqlite"),Pooling=false }.ToString()))
        {
            db.Open(); for(int version=1;version<=3;version++)
            {
                using var migrate=db.CreateCommand(); migrate.CommandText=CatalogMigrations.Script(version); migrate.ExecuteNonQuery();
                using var history=db.CreateCommand(); history.CommandText="INSERT INTO SchemaMigration VALUES($version,$at)";
                history.Parameters.AddWithValue("$version",version); history.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); history.ExecuteNonQuery();
            }
            foreach(var record in new[]{(Id:legacyArtifact.Id,Kind:"artifact",Json:Wire.Serialize(legacyArtifact),Hash:Wire.Hash(legacyArtifact)),
                (Id:legacyOwner.Id,Kind:"hypothesis",Json:Wire.Serialize(legacyOwner),Hash:Wire.Hash(legacyOwner))})
            {
                using var insert=db.CreateCommand(); insert.CommandText="INSERT INTO Record VALUES($id,$kind,1,$hash,$json,$at)";
                insert.Parameters.AddWithValue("$id",record.Id); insert.Parameters.AddWithValue("$kind",record.Kind); insert.Parameters.AddWithValue("$hash",record.Hash);
                insert.Parameters.AddWithValue("$json",record.Json); insert.Parameters.AddWithValue("$at",DateTimeOffset.UtcNow.ToString("O")); insert.ExecuteNonQuery();
            }
            using var seed=db.CreateCommand(); seed.CommandText="INSERT INTO ArtifactReference VALUES($artifact,'legacy-owner',NULL)";
            seed.Parameters.AddWithValue("$artifact",legacyArtifact.Id); seed.ExecuteNonQuery();
        }
        using var catalog=new Catalog(workspace.Root); Assert.Equal(3,catalog.MigrationBackup!.SchemaVersion);
        using var read=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(workspace.Root,"catalog.sqlite"),Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()); read.Open();
        using var query=read.CreateCommand(); query.CommandText="SELECT Revision,Released FROM ArtifactReferenceState WHERE ArtifactId=$artifact AND OwnerId='legacy-owner'";
        query.Parameters.AddWithValue("$artifact",legacyArtifact.Id);
        using var row=query.ExecuteReader(); Assert.True(row.Read()); Assert.Equal(1,row.GetInt64(0)); Assert.Equal(0,row.GetInt64(1));
    }
    [Fact,Case("UT-ART-013","FR-062,FR-072","M3","Integration")]
    public async Task BusyBeforeCommit_RollsBackReferencesGrantReceiptAndTerminalStateTogether()
    {
        using var workspace=new TestWorkspace(); ReferenceReleasePlan plan; Grant grant; string artifactId,ownerId;
        await using(var service=new LabService(workspace.Root))
        {
            var artifact=await Create(service,9); var owner=service.Hypothesis("owner","retain",[]); artifactId=artifact.Id; ownerId=owner.Id;
            service.PinEvidence(artifact.Id,owner.Id); plan=service.PrepareReferenceRelease([new(artifact.Id,owner.Id)],"atomic release");
            grant=service.IssueReferenceReleaseGrant(plan.Id,120,1);
        }
        bool armed=false;
        using var catalog=new Catalog(workspace.Root,beforeWriteCommit:()=> { if(armed) throw new SqliteException("injected busy",5); });
        var operation=catalog.Accept("fixture","release","request-hash","reference_release",plan.Id);
        catalog.AcquireLease(plan.Id,operation.Id); catalog.Transition(operation.Id,OperationState.Running); armed=true;
        Assert.Equal("catalog_busy",Assert.Throws<LabException>(()=>catalog.ReleaseArtifactReferences(plan.Id,plan.ContentHash,plan.References,grant,operation.Id)).Code);
        Assert.Equal(1,catalog.Retention(artifactId).RetainedOwners); Assert.Equal(1,catalog.ReferenceSnapshot(artifactId,ownerId).ReferenceRevision);
        Assert.Equal(OperationState.Running,catalog.GetOperation(operation.Id).State); Assert.Empty(catalog.List("reference_release_result").Rows);
        armed=false; var result=catalog.ReleaseArtifactReferences(plan.Id,plan.ContentHash,plan.References,grant,operation.Id);
        Assert.Equal(OperationState.Completed,catalog.GetOperation(operation.Id).State); Assert.Equal(result.Id,catalog.GetOperation(operation.Id).ResultId);
        Assert.Equal(0,catalog.Retention(artifactId).RetainedOwners);
        var successor=catalog.Accept("fixture","successor","hash","reference_release",plan.Id); catalog.AcquireLease(plan.Id,successor.Id);
    }
}
