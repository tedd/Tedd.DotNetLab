using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Microsoft.Data.Sqlite;

namespace Tedd.DotNetLab.Storage;

public record ArtifactRetention(string ArtifactId,bool Pinned,int RetainedOwners,string? DeletionState);
public record ArtifactDeletion(string ArtifactId,string PlanId,string State);
public record ArtifactReferenceSnapshot(string ArtifactId,string OwnerId,DateTimeOffset? RetainUntil,long ReferenceRevision,long OwnerRevision,string OwnerHash);
public record ReferenceReleaseResult(string Id,string PlanId,string PlanHash,int ReleasedReferences,string Meaning="Selected user references released; artifact bytes and system pins preserved.");

public sealed partial class Catalog
{
    private static void EnsureArtifactRetained(SqliteConnection db,SqliteTransaction tx,string id)
    {
        using var artifact=Command(db,tx,"SELECT 1 FROM Record WHERE Id=$id AND Kind='artifact'",("$id",id));
        if(artifact.ExecuteScalar() is null) throw new LabException("record_not_found","Artifact manifest does not exist.");
        using var deletion=Command(db,tx,"SELECT 1 FROM ArtifactDeletion WHERE ArtifactId=$id",("$id",id));
        if(deletion.ExecuteScalar() is not null) throw new LabException("artifact_expired","Artifact deletion has been committed.");
    }
    public void RetainArtifact(string artifactId,string ownerId,DateTimeOffset? retainUntil=null)=>Write((db,tx)=>
    {
        EnsureArtifactRetained(db,tx,artifactId);
        using var owner=Command(db,tx,"SELECT 1 FROM Record WHERE Id=$owner",("$owner",ownerId));
        if(owner.ExecuteScalar() is null) throw new LabException("record_not_found","Retention owner does not exist.");
        if(retainUntil<=DateTimeOffset.UtcNow) throw new LabException("invalid_request","Retention must expire in the future or be indefinite.");
        // An existing owner's promise cannot be shortened by a later request.
        using var reference=Command(db,tx,"""
            INSERT INTO ArtifactReference VALUES($id,$owner,$until)
            ON CONFLICT(ArtifactId,OwnerId) DO UPDATE SET RetainUntil=
            CASE WHEN RetainUntil IS NULL OR excluded.RetainUntil IS NULL THEN NULL
                 WHEN RetainUntil>excluded.RetainUntil THEN RetainUntil ELSE excluded.RetainUntil END
            """,("$id",artifactId),("$owner",ownerId),("$until",retainUntil?.ToUniversalTime().ToString("O")));
        reference.ExecuteNonQuery();
        using var revision=Command(db,tx,"""
            INSERT INTO ArtifactReferenceState VALUES($id,$owner,1,0)
            ON CONFLICT(ArtifactId,OwnerId) DO UPDATE SET Revision=Revision+1,Released=0
            WHERE Revision<9223372036854775807 RETURNING Revision
            """,("$id",artifactId),("$owner",ownerId));
        if(revision.ExecuteScalar() is null) throw new LabException("resource_limit","Retention reference revision is exhausted.");
        Audit(db,tx,"artifact_reference_retained",artifactId,ownerId); return true;
    });
    public ArtifactReferenceSnapshot ReferenceSnapshot(string artifactId,string ownerId)
    {
        using var db=Open(); return ReferenceSnapshotCore(db,null,artifactId,ownerId);
    }
    private static ArtifactReferenceSnapshot ReferenceSnapshotCore(SqliteConnection db,SqliteTransaction? tx,string artifactId,string ownerId)
    {
        using var query=Command(db,tx,"""
            SELECT r.RetainUntil,s.Revision,o.Revision,o.Hash FROM ArtifactReference r
            JOIN ArtifactReferenceState s ON s.ArtifactId=r.ArtifactId AND s.OwnerId=r.OwnerId AND s.Released=0
            JOIN Record o ON o.Id=r.OwnerId AND o.Revision=(SELECT MAX(Revision) FROM Record WHERE Id=r.OwnerId)
            WHERE r.ArtifactId=$id AND r.OwnerId=$owner
            """,("$id",artifactId),("$owner",ownerId));
        using var reader=query.ExecuteReader();
        if(!reader.Read()) throw new LabException("retention_reference_not_found","Selected active retention reference does not exist.");
        return new(artifactId,ownerId,reader.IsDBNull(0)?null:DateTimeOffset.Parse(reader.GetString(0)),reader.GetInt64(1),reader.GetInt64(2),reader.GetString(3));
    }
    public Page<ArtifactReferenceSnapshot> References(string artifactId,int limit=100,string? cursor=null)
    {
        if(limit is <1 or >100) throw new LabException("resource_limit","Reference page limit must be 1..100.");
        string prefix=artifactId+":";
        if(cursor is not null&&!cursor.StartsWith(prefix,StringComparison.Ordinal)) throw new LabException("invalid_cursor","Reference cursor belongs to another artifact.");
        using var db=Open(); using var snapshot=db.BeginTransaction(deferred:true);
        using var query=Command(db,snapshot,"""
            SELECT r.OwnerId FROM ArtifactReference r WHERE r.ArtifactId=$id AND r.OwnerId>$after ORDER BY r.OwnerId LIMIT $limit
            """,("$id",artifactId),("$after",cursor is null?"":cursor[prefix.Length..]),("$limit",limit+1));
        var owners=new List<string>(); using(var reader=query.ExecuteReader()) while(reader.Read()) owners.Add(reader.GetString(0));
        bool truncated=owners.Count>limit; if(truncated) owners.RemoveAt(owners.Count-1);
        var rows=owners.Select(owner=>ReferenceSnapshotCore(db,snapshot,artifactId,owner)).ToArray();
        return new(rows,truncated,truncated?prefix+owners[^1]:null);
    }
    public ReferenceReleaseResult ReleaseArtifactReferences(string planId,string planHash,IReadOnlyList<ArtifactReferenceSnapshot> references,Grant grant,string operationId)=>Write((db,tx)=>
    {
        Policies.Authorize(grant,Root,new(planId,0,0,planId,"workspace","local"),Permission.DeleteEvidence,DateTimeOffset.UtcNow,planHash);
        if(references.Count is <1 or >100||references.Select(r=>(r.ArtifactId,r.OwnerId)).Distinct().Count()!=references.Count)
            throw new LabException("invalid_plan","Reference release requires a bounded distinct selection.");
        using var frozen=Command(db,tx,"SELECT Json FROM Record WHERE Id=$id AND Kind='reference_release_plan' AND Revision=1",("$id",planId));
        if(frozen.ExecuteScalar() is not string json) throw new LabException("permission_denied","Reference release requires a frozen catalog plan.");
        using var document=System.Text.Json.JsonDocument.Parse(json);
        if(document.RootElement.GetProperty("content_hash").GetString()!=planHash||Wire.Hash(document.RootElement.GetProperty("references"))!=Wire.Hash(references))
            throw new LabException("permission_denied","Reference release selection differs from the reviewed plan.");
        var operation=ReadOperation(db,tx,operationId);
        if(operation.Kind!="reference_release"||operation.TargetId!=planId||operation.State is not (OperationState.Running or OperationState.CancellationRequested))
            throw new LabException("invalid_transition","Reference release requires its active durable operation.");
        foreach(var expected in references)
        {
            ArtifactReferenceSnapshot current;
            try { current=ReferenceSnapshotCore(db,tx,expected.ArtifactId,expected.OwnerId); }
            catch(LabException error) when(error.Code=="retention_reference_not_found")
            { throw new LabException("stale_retention_reference","A selected retention reference was already released."); }
            if(current!=expected) throw new LabException("stale_retention_reference","Reference or owner revision changed after planning.");
            if(current.ReferenceRevision==long.MaxValue) throw new LabException("resource_limit","Retention reference revision is exhausted.");
        }
        using var use=Command(db,tx,"""
            INSERT INTO GrantUse VALUES($id,1) ON CONFLICT(GrantId) DO UPDATE SET Count=Count+1
            WHERE Count<$maximum RETURNING Count
            """,("$id",grant.Id),("$maximum",grant.MaximumOperations));
        if(use.ExecuteScalar() is null) throw new LabException("permission_denied","Grant operation budget exhausted.");
        Audit(db,tx,"grant_used",grant.Id,grant.Permission.ToString());
        foreach(var reference in references)
        {
            using var release=Command(db,tx,"""
                DELETE FROM ArtifactReference WHERE ArtifactId=$id AND OwnerId=$owner;
                UPDATE ArtifactReferenceState SET Released=1,Revision=Revision+1 WHERE ArtifactId=$id AND OwnerId=$owner;
                """,("$id",reference.ArtifactId),("$owner",reference.OwnerId));
            release.ExecuteNonQuery();
            Audit(db,tx,"artifact_reference_released",reference.ArtifactId,Wire.Serialize(new { reference.OwnerId,reference.ReferenceRevision,planId }));
        }
        var result=new ReferenceReleaseResult(Wire.Id("reference-release"),planId,planHash,references.Count);
        string resultJson=Wire.Serialize(result),resultHash=Wire.Hash(result),at=DateTimeOffset.UtcNow.ToString("O");
        using var publish=Command(db,tx,"""
            INSERT INTO Record VALUES($result,'reference_release_result',1,$hash,$json,$at);
            UPDATE Operation SET State='Completed',Revision=Revision+1,ResultId=$result,ErrorCode=NULL WHERE Id=$operation;
            INSERT INTO OperationEvent VALUES($operation,$revision,'Completed',$at);
            """,("$result",result.Id),("$hash",resultHash),("$json",resultJson),("$at",at),("$operation",operationId),("$revision",operation.Revision+1));
        publish.ExecuteNonQuery(); Audit(db,tx,"record_created",result.Id,resultHash);
        ReleaseLeaseCore(db,tx,planId,operationId,true);
        return result;
    });
    public ArtifactRetention Retention(string artifactId)
    {
        using var db=Open(); return RetentionCore(db,null,artifactId,DateTimeOffset.UtcNow);
    }
    private static ArtifactRetention RetentionCore(SqliteConnection db,SqliteTransaction? tx,string id,DateTimeOffset now)
    {
        using var pin=Command(db,tx,"SELECT 1 FROM ArtifactPin WHERE ArtifactId=$id",("$id",id));
        bool pinned=pin.ExecuteScalar() is not null;
        using var owners=Command(db,tx,"SELECT COUNT(*) FROM ArtifactReference WHERE ArtifactId=$id AND (RetainUntil IS NULL OR RetainUntil>$now)",
            ("$id",id),("$now",now.ToUniversalTime().ToString("O")));
        int count=Convert.ToInt32(owners.ExecuteScalar());
        using var state=Command(db,tx,"SELECT State FROM ArtifactDeletion WHERE ArtifactId=$id",("$id",id));
        return new(id,pinned,count,state.ExecuteScalar() as string);
    }
    public void BeginArtifactDeletion(string planId,string planHash,IReadOnlyList<string> artifactIds,Grant grant)=>Write((db,tx)=>
    {
        Policies.Authorize(grant,Root,new(planId,0,0,planId,"workspace","local"),Permission.DeleteEvidence,DateTimeOffset.UtcNow,planHash);
        if(artifactIds.Count is <1 or >1000||artifactIds.Distinct().Count()!=artifactIds.Count)
            throw new LabException("invalid_plan","Deletion requires a distinct bounded selection.");
        using var frozen=Command(db,tx,"SELECT Json FROM Record WHERE Id=$id AND Kind='deletion_plan' AND Revision=1",("$id",planId));
        if(frozen.ExecuteScalar() is not string json) throw new LabException("permission_denied","Deletion requires a frozen catalog plan.");
        using var document=System.Text.Json.JsonDocument.Parse(json);
        if(document.RootElement.GetProperty("content_hash").GetString()!=planHash||
            !document.RootElement.GetProperty("artifacts").EnumerateArray().Select(a=>a.GetProperty("id").GetString()).SequenceEqual(artifactIds))
            throw new LabException("permission_denied","Deletion selection differs from its issued plan.");
        using var use=Command(db,tx,"""
            INSERT INTO GrantUse VALUES($id,1) ON CONFLICT(GrantId) DO UPDATE SET Count=Count+1
            WHERE Count<$maximum RETURNING Count
            """,("$id",grant.Id),("$maximum",grant.MaximumOperations));
        if(use.ExecuteScalar() is null) throw new LabException("permission_denied","Grant operation budget exhausted.");
        Audit(db,tx,"grant_used",grant.Id,grant.Permission.ToString());
        foreach(string id in artifactIds)
        {
            EnsureArtifactRetained(db,tx,id);
            var protection=RetentionCore(db,tx,id,DateTimeOffset.UtcNow);
            if(protection.Pinned||protection.RetainedOwners>0)
                throw new LabException("evidence_protected","Retained owners or pins prevent deletion.");
        }
        foreach(string id in artifactIds)
        {
            using var intent=Command(db,tx,"INSERT INTO ArtifactDeletion VALUES($id,$plan,'pending',$at,NULL)",
                ("$id",id),("$plan",planId),("$at",DateTimeOffset.UtcNow.ToString("O")));
            intent.ExecuteNonQuery(); Audit(db,tx,"artifact_deletion_committed",id,planId);
        }
        return true;
    });
    public IReadOnlyList<ArtifactDeletion> PendingArtifactDeletions()
    {
        using var db=Open(); using var query=Command(db,null,"SELECT ArtifactId,PlanId,State FROM ArtifactDeletion WHERE State='pending' ORDER BY ArtifactId");
        using var reader=query.ExecuteReader(); var result=new List<ArtifactDeletion>();
        while(reader.Read()) result.Add(new(reader.GetString(0),reader.GetString(1),reader.GetString(2)));
        return result;
    }
    public void CompleteArtifactDeletion(string artifactId)=>Write((db,tx)=>
    {
        using var complete=Command(db,tx,"UPDATE ArtifactDeletion SET State='deleted',CompletedAt=$at WHERE ArtifactId=$id AND State='pending'",
            ("$id",artifactId),("$at",DateTimeOffset.UtcNow.ToString("O")));
        if(complete.ExecuteNonQuery()>0) Audit(db,tx,"artifact_deleted",artifactId,"raw_bytes_expired_manifest_retained");
        return true;
    });
}
