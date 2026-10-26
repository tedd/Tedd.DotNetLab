using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Storage;

public sealed partial class Catalog
{
    public void CompleteProfilerAttachmentReconciliation(string operationId,string proofId)=>Write((db,tx)=>
    {
        string marker=operationId+"-attachment-reconciled";
        using var prior=Command(db,tx,"SELECT 1 FROM Record WHERE Id=$id",("$id",marker));
        if(prior.ExecuteScalar() is not null) return true; // Replays never clear a successor's taint.
        var source=ReadOperation(db,tx,operationId);
        if(source.Kind!="profiler_attach"||source.State is not (OperationState.Tainted or OperationState.Interrupted))
            throw new LabException("recovery_unverified","Attachment source is not unresolved.");
        using var evidence=Command(db,tx,"SELECT Kind,Hash,Json FROM Record WHERE Id=$id AND Revision=1",("$id",proofId));
        ProfilerAttachmentReconciliation proof;
        using(var reader=evidence.ExecuteReader())
        {
            if(!reader.Read()||reader.GetString(0)!="profiler_attachment_reconciliation") throw new LabException("recovery_unverified","Attachment proof is absent.");
            proof=Wire.Deserialize<ProfilerAttachmentReconciliation>(reader.GetString(2));
            if(reader.GetString(1)!=Wire.Hash(proof)) throw new LabException("artifact_corrupt","Attachment proof hash differs.");
        }
        using var intentQuery=Command(db,tx,"SELECT Kind,Hash,Json FROM Record WHERE Id=$id AND Revision=1",("$id",operationId+"-intent"));
        System.Text.Json.JsonElement intent;
        using(var reader=intentQuery.ExecuteReader())
        {
            if(!reader.Read()||reader.GetString(0)!="profiler_attachment_intent") throw new LabException("recovery_unverified","Attachment intent is absent.");
            intent=Wire.Deserialize<System.Text.Json.JsonElement>(reader.GetString(2));
            if(reader.GetString(1)!=Wire.Hash(intent)) throw new LabException("artifact_corrupt","Attachment intent hash differs.");
        }
        if(proof.Id!=proofId||proof.SourceOperationId!=operationId||proof.Target.Id!=source.TargetId||
            proof.State is not ("attached" or "target_gone")||proof.PlanId!=intent.GetProperty("plan_id").GetString()||
            proof.PlanHash!=intent.GetProperty("plan_hash").GetString()||
            intent.GetProperty("operation_id").GetString()!=operationId||
            proof.Target!=Wire.Deserialize<TargetIdentity>(intent.GetProperty("target").GetRawText()))
            throw new LabException("recovery_unverified","Attachment proof does not match its source intent.");
        using var active=Command(db,tx,"SELECT 1 FROM TargetLease WHERE TargetId=$id",("$id",source.TargetId));
        using var taint=Command(db,tx,"SELECT Reason FROM TaintedTarget WHERE TargetId=$id",("$id",source.TargetId));
        if(active.ExecuteScalar() is not null||taint.ExecuteScalar() as string!="Profiler attachment unresolved: "+operationId)
            throw new LabException("recovery_unverified","Attachment source does not own the unresolved target.");
        using var clear=Command(db,tx,"DELETE FROM TaintedTarget WHERE TargetId=$id",("$id",source.TargetId)); clear.ExecuteNonQuery();
        var commit=new { source_operation_id=operationId,proof_id=proofId };
        using var insert=Command(db,tx,"INSERT INTO Record VALUES($id,$kind,1,$hash,$json,$at)",("$id",marker),
            ("$kind","profiler_attachment_reconciliation_commit"),("$hash",Wire.Hash(commit)),("$json",Wire.Serialize(commit)),("$at",DateTimeOffset.UtcNow.ToString("O")));
        insert.ExecuteNonQuery(); Audit(db,tx,"profiler_attachment_reconciled",operationId,proofId); return true;
    });
}
