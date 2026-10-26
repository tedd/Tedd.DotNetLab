using System.Text.Json;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Storage;

public sealed partial class Catalog
{
    public Page<NotebookRecord> ImportedMembers(string importId,int limit=100,string? cursor=null)
    {
        if(limit is <1 or >100) throw new LabException("resource_limit","Imported member page limit must be 1..100.");
        string after="";
        if(cursor is not null)
        {
            var anchor=Get(cursor);
            if(anchor.Kind!="imported_evidence_member"||Wire.Deserialize<JsonElement>(anchor.Json).GetProperty("import_id").GetString()!=importId)
                throw new LabException("invalid_cursor","Imported member cursor belongs to another bundle.");
            after=cursor;
        }
        using var db=Open(); using var query=Command(db,null,"""
            SELECT Id,Kind,Revision,Hash,Json,CreatedAt FROM Record
            WHERE Kind='imported_evidence_member' AND json_extract(Json,'$.import_id')=$import AND Id>$after ORDER BY Id LIMIT $limit
            """,("$import",importId),("$after",after),("$limit",limit+1));
        using var reader=query.ExecuteReader(); var rows=new List<NotebookRecord>(); int bytes=0; bool more=false;
        while(reader.Read())
        {
            var row=ReadRecord(reader); int size=System.Text.Encoding.UTF8.GetByteCount(row.Json)+512;
            if(rows.Count==limit||bytes+size>60000) { more=true; break; }
            rows.Add(row); bytes+=size;
        }
        return new(rows,more,more?rows[^1].Id:null);
    }
}
