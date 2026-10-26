using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Storage;

public sealed partial class Catalog
{
    public void PublishBundleImport(string operationId,string resultId,IReadOnlyList<NotebookRecord> records,IReadOnlyList<Artifact> artifacts)
    {
        if(records.Count is <1 or >1001||artifacts.Count is <1 or >1002||records.Select(r=>r.Id).Distinct().Count()!=records.Count||
            artifacts.Select(a=>a.Id).Distinct().Count()!=artifacts.Count||records.Count(r=>r.Id==resultId&&r.Kind=="bundle_import_result")!=1||
            records.Any(r=>r.Revision!=1||r.Kind is not ("imported_evidence_member" or "bundle_import_result")||r.Json.Length>1048576||
                Wire.Hash(Wire.Deserialize<System.Text.Json.JsonElement>(r.Json))!=r.Hash))
            throw new LabException("invalid_request","Invalid bounded imported evidence publication.");
        Write((db,tx)=>
        {
            var operation=ReadOperation(db,tx,operationId);
            if(operation.Kind!="bundle_import"||operation.State!=OperationState.Running)
                throw new LabException("cancelled","Bundle import is no longer eligible for publication.");
            void Insert(NotebookRecord record)
            {
                using var insert=Command(db,tx,"INSERT INTO Record VALUES($id,$kind,1,$hash,$json,$at)",("$id",record.Id),("$kind",record.Kind),
                    ("$hash",record.Hash),("$json",record.Json),("$at",record.CreatedAt.ToString("O")));
                insert.ExecuteNonQuery(); Audit(db,tx,"record_created",record.Id,record.Hash);
            }
            foreach(var artifact in artifacts)
            {
                using var existing=Command(db,tx,"SELECT Kind,Hash FROM Record WHERE Id=$id ORDER BY Revision DESC LIMIT 1",("$id",artifact.Id));
                bool exists;
                using(var reader=existing.ExecuteReader())
                {
                    exists=reader.Read();
                    if(exists&&(reader.GetString(0)!="artifact"||reader.GetString(1)!=Wire.Hash(artifact)))
                        throw new LabException("artifact_corrupt","Imported content collides with another retained manifest.");
                }
                if(!exists) Insert(new(artifact.Id,"artifact",1,Wire.Hash(artifact),Wire.Serialize(artifact),DateTimeOffset.UtcNow));
                EnsureArtifactRetained(db,tx,artifact.Id);
                using var pin=Command(db,tx,"INSERT OR IGNORE INTO ArtifactPin VALUES($id,'verified_bundle_import')",("$id",artifact.Id)); pin.ExecuteNonQuery();
            }
            foreach(var record in records) Insert(record);
            using var terminal=Command(db,tx,"""
                UPDATE Operation SET State='Completed',Revision=Revision+1,ResultId=$result,ErrorCode=NULL WHERE Id=$id;
                INSERT INTO OperationEvent VALUES($id,$rev,'Completed',$at);
                """,("$id",operation.Id),("$result",resultId),("$rev",operation.Revision+1),("$at",DateTimeOffset.UtcNow.ToString("O")));
            terminal.ExecuteNonQuery();
            using var lease=Command(db,tx,"SELECT TargetId FROM TargetLease WHERE OperationId=$op",("$op",operation.Id));
            var targets=new List<string>(); using(var reader=lease.ExecuteReader()) while(reader.Read()) targets.Add(reader.GetString(0));
            foreach(string target in targets) ReleaseLeaseCore(db,tx,target,operation.Id,true);
            Audit(db,tx,"evidence_bundle_imported",resultId,operation.Id); return true;
        });
    }
}
