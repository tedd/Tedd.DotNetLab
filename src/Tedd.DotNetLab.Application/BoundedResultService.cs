using System.Text.Json;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    private readonly SemaphoreSlim resultArtifacts=new(1,1);
    public async Task<Artifact> RetainBoundedResultAsync(byte[] json)
    {
        const int maximumBytes=16777216;
        if(json.Length>maximumBytes||!DiskSpace.Fits(Catalog.Root,json.Length,Limits.DiskReserveBytes))
            throw new LabException("resource_limit","Response artifact exceeds the byte or disk reserve budget.");
        using(var document=JsonDocument.Parse(json)) { }
        await resultArtifacts.WaitAsync();
        try
        {
            string incoming=Artifacts.CreateIncoming("bounded-result");
            await File.WriteAllBytesAsync(incoming,json);
            var artifact=await Artifacts.SealAsync(incoming,"application/json","bounded-result/v1",true,maximumBytes);
            try { Catalog.Put(artifact.Id,"artifact",artifact); }
            catch(LabException error) when(error.Code=="revision_conflict")
            {
                var retained=Catalog.Get<Artifact>(artifact.Id);
                if(retained.Hash!=artifact.Hash||retained.Length!=artifact.Length||retained.MediaType!=artifact.MediaType||!retained.Complete)
                    throw new LabException("artifact_corrupt","Response artifact metadata differs from the sealed content.");
                artifact=retained;
            }
            string owner="response-"+Guid.NewGuid().ToString("N");
            DateTimeOffset expires=DateTimeOffset.UtcNow.AddMinutes(30);
            Catalog.Put(owner,"bounded_result",new { artifact_id=artifact.Id,retain_until=expires });
            Catalog.RetainArtifact(artifact.Id,owner,expires);
            return artifact;
        }
        finally { resultArtifacts.Release(); }
    }
}
