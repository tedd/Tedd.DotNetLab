using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Microsoft.Data.Sqlite;

namespace Tedd.DotNetLab.Storage;

public sealed partial class Catalog
{
    public const string WorkspacePolicyVersion="workspace-policy/v1";
    private static string PolicyHash(ResourceLimits limits)=>Wire.Hash(new { schema_version=WorkspacePolicyVersion,limits });
    private static WorkspacePolicyRevision ReadPolicy(SqliteConnection db,SqliteTransaction? tx,string id)
    {
        using var query=Command(db,tx,"SELECT Id,Revision,SchemaVersion,Hash,Json,CreatedAt FROM PolicyRevision WHERE Id=$id",("$id",id));
        using var reader=query.ExecuteReader();
        if(!reader.Read()) throw new LabException("record_not_found","Workspace policy revision is absent.");
        if(reader.GetString(2)!=WorkspacePolicyVersion) throw new LabException("schema_unsupported","Workspace policy schema is unsupported.");
        var limits=Wire.Deserialize<ResourceLimits>(reader.GetString(4)); Policies.Validate(limits);
        string hash=PolicyHash(limits);
        if(hash!=reader.GetString(3)||reader.GetString(0)!="policy-"+hash) throw new LabException("catalog_corrupt","Workspace policy content hash differs.");
        return new(reader.GetString(0),reader.GetInt64(1),reader.GetString(2),hash,limits,DateTimeOffset.Parse(reader.GetString(5)));
    }
    public WorkspaceContext ConfigureWorkspace(ResourceLimits? requested=null)
    {
        if(requested is not null) Policies.Validate(requested);
        return Write((db,tx)=>
        {
            string? identity=null,current=null; DateTimeOffset created=DateTimeOffset.UtcNow;
            using(var query=Command(db,tx,"SELECT Id,CurrentPolicyId,CreatedAt FROM Workspace WHERE Singleton=1"))
            using(var reader=query.ExecuteReader()) if(reader.Read()) { identity=reader.GetString(0); current=reader.GetString(1); created=DateTimeOffset.Parse(reader.GetString(2)); }
            var previous=current is null?null:ReadPolicy(db,tx,current);
            var limits=requested??previous?.Limits??new ResourceLimits(); Policies.Validate(limits);
            string hash=PolicyHash(limits),policyId="policy-"+hash;
            using(var exists=Command(db,tx,"SELECT COUNT(*) FROM PolicyRevision WHERE Id=$id",("$id",policyId)))
            if(Convert.ToInt64(exists.ExecuteScalar())==0)
            {
                using var insert=Command(db,tx,"""
                    INSERT INTO PolicyRevision SELECT $id,COALESCE(MAX(Revision),0)+1,$schema,$hash,$json,$at FROM PolicyRevision
                    """,("$id",policyId),("$schema",WorkspacePolicyVersion),("$hash",hash),("$json",Wire.Serialize(limits)),("$at",DateTimeOffset.UtcNow.ToString("O")));
                insert.ExecuteNonQuery();
            }
            var policy=ReadPolicy(db,tx,policyId); identity??=Wire.Id("workspace");
            using(var update=Command(db,tx,"""
                INSERT INTO Workspace VALUES(1,$id,$root,$schema,$policy,$at)
                ON CONFLICT(Singleton) DO UPDATE SET Root=excluded.Root,SchemaVersion=excluded.SchemaVersion,CurrentPolicyId=excluded.CurrentPolicyId
                """,("$id",identity),("$root",Root),("$schema",CurrentSchemaVersion),("$policy",policy.Id),("$at",created.ToString("O")))) update.ExecuteNonQuery();
            return new WorkspaceContext(new(identity,Root,CurrentSchemaVersion,policy.Id,policy.Hash,created),policy);
        });
    }
    public WorkspacePolicyRevision PolicyRevision(string id) { using var db=Open(); return ReadPolicy(db,null,id); }
    public OperationPolicyBinding OperationPolicy(string operationId)
    {
        GetOperation(operationId); using var db=Open();
        using var query=Command(db,null,"SELECT PolicyId FROM OperationPolicy WHERE OperationId=$id",("$id",operationId));
        string? id=query.ExecuteScalar() as string;
        return new(operationId,id is null?"not_collected":"available",id is null?null:ReadPolicy(db,null,id));
    }
}
