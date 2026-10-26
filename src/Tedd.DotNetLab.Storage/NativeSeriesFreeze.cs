using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Storage;

public record CatalogSourceReference(string Id,long Revision,string Hash);

public sealed partial class Catalog
{
    public NotebookRecord FreezeNativeSeries<T>(string id,T value,IReadOnlyList<CatalogSourceReference> trials)
    {
        if(trials.Count is <1 or >100||trials.Select(t=>t.Id).Distinct().Count()!=trials.Count)
            throw new LabException("invalid_plan","Native series requires a bounded distinct trial selection.");
        string json=Wire.Serialize(value),hash=Wire.Hash(value);
        if(json.Length>1048576) throw new LabException("resource_limit","Native series plan exceeds its catalog budget.");
        return Write((db,tx)=>
        {
            foreach(var trial in trials)
            {
                using var source=Command(db,tx,"SELECT Kind,Revision,Hash FROM Record WHERE Id=$id ORDER BY Revision DESC LIMIT 1",("$id",trial.Id));
                using(var read=source.ExecuteReader())
                    if(!read.Read()||read.GetString(0)!="native_trial_plan"||read.GetInt64(1)!=trial.Revision||read.GetString(2)!=trial.Hash)
                        throw new LabException("invalid_plan","Native series trial differs from its frozen revision/hash.");
                using var started=Command(db,tx,"SELECT 1 FROM IdempotencyRecord WHERE Caller='local' AND Key=$key",("$key","native-trial-"+trial.Id));
                if(started.ExecuteScalar() is not null) throw new LabException("invalid_plan","A native series must freeze before any selected trial acceptance.");
                using var reserved=Command(db,tx,"SELECT 1 FROM Record WHERE Id=$id",("$id","native-series-member-"+trial.Id));
                if(reserved.ExecuteScalar() is not null) throw new LabException("invalid_plan","A native trial already belongs to a frozen series.");
            }
            var at=DateTimeOffset.UtcNow;
            void Insert(string recordId,string kind,string payload,string recordHash)
            {
                using var insert=Command(db,tx,"INSERT INTO Record VALUES($id,$kind,1,$hash,$json,$at)",
                    ("$id",recordId),("$kind",kind),("$hash",recordHash),("$json",payload),("$at",at.ToString("O")));
                insert.ExecuteNonQuery(); Audit(db,tx,"record_created",recordId,recordHash);
            }
            Insert(id,"native_series_plan",json,hash);
            foreach(var trial in trials)
            {
                var member=new { series_id=id,series_hash=hash,trial };
                Insert("native-series-member-"+trial.Id,"native_series_member",Wire.Serialize(member),Wire.Hash(member));
            }
            return new NotebookRecord(id,"native_series_plan",1,hash,json,at);
        });
    }
}
