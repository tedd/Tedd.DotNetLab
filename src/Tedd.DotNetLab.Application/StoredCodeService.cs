using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public async Task<Artifact> ImportAsync(string sourcePath,string mediaType)
    {
        if(!new[]{"application/vnd.microsoft.portable-executable","application/vnd.dotnetlab.native-snapshot+json",
            "application/json","text/x-diff","application/vnd.microsoft.nettrace","application/zip"}.Contains(mediaType))
            throw new LabException("invalid_request","Unsupported importer media type.");
        long maximum=mediaType=="application/zip"?Math.Min(2147483648,Limits.MaximumCaptureBytes):67108864;
        string incoming=Artifacts.CreateIncoming(Wire.Id("import"));
        await using(var source=new FileStream(Path.GetFullPath(sourcePath),FileMode.Open,FileAccess.Read,FileShare.Read))
        {
            if(source.Length>maximum) throw new LabException("resource_limit","Input exceeds its media-specific importer byte budget.");
            if(!DiskSpace.Fits(Catalog.Root,source.Length,Limits.DiskReserveBytes))
                throw new LabException("resource_limit","Local import would violate the workspace disk reserve.");
            await using var destination=new FileStream(incoming,FileMode.CreateNew,FileAccess.Write,FileShare.None);
            byte[] buffer=new byte[65536]; long length=0;
            while(true)
            {
                int read=await source.ReadAsync(buffer);
                if(read==0) break;
                if(length>maximum-read) throw new LabException("resource_limit","Input grew beyond the import cap.");
                await destination.WriteAsync(buffer.AsMemory(0,read)); length+=read;
            }
            destination.Flush(true);
        }
        var artifact=await Artifacts.SealAsync(incoming,mediaType,"human-local-import/1",true,maximum);
        Upsert(artifact.Id,"artifact",artifact);
        return artifact;
    }
    private async Task<(Artifact Artifact,string Path)> CodeArtifactAsync(string artifactId,string mediaType)
    {
        var artifact=Catalog.Get<Artifact>(artifactId);
        if(artifact.MediaType!=mediaType) throw new LabException("invalid_request","Artifact media type does not match the operation.");
        return(artifact,await Artifacts.VerifiedPathAsync(artifact));
    }
    public async Task<Page<StoredMethod>> MethodsAsync(string artifactId,string filter,int limit,string? cursor)
    {
        var source=await CodeArtifactAsync(artifactId,"application/vnd.microsoft.portable-executable");
        var page=PeInspector.Methods(source.Path,source.Artifact.Hash,filter,limit,cursor);
        foreach(var method in page.Rows) Upsert(method.Id,"method",method);
        return page;
    }
    public async Task<object> StoredIlAsync(string artifactId,string methodToken)
    {
        var source=await CodeArtifactAsync(artifactId,"application/vnd.microsoft.portable-executable");
        var il=PeInspector.Il(source.Path,source.Artifact.Hash,methodToken);
        var artifact=await Artifacts.WriteJsonAsync(Wire.Id("inspection"),il,"pe-metadata/1");
        Upsert(artifact.Id,"artifact",artifact);
        return new { method=il.Method,il_hash=il.IlHash,byte_count=il.Bytes.Length,max_stack=il.MaxStack,
            local_signature=il.LocalSignature,init_locals=il.InitLocals,qualification=il.Qualification,
            artifact_id=artifact.Id,preview_hex=Convert.ToHexStringLower(il.Bytes.AsSpan(0,Math.Min(64,il.Bytes.Length))) };
    }
    private async Task<NativeSnapshot> NativeSnapshotAsync(string artifactId)
    {
        var source=await CodeArtifactAsync(artifactId,"application/vnd.dotnetlab.native-snapshot+json");
        if(source.Artifact.Length>4194304) throw new LabException("resource_limit","Native manifest exceeds 4 MiB.");
        var snapshot=Wire.Deserialize<NativeSnapshot>(await File.ReadAllTextAsync(source.Path));
        NativeDecoder.Validate(snapshot); return snapshot;
    }
    public async Task<object> DecodeAsync(string artifactId)
    {
        var snapshot=await NativeSnapshotAsync(artifactId);
        var listing=NativeDecoder.Decode(snapshot);
        var artifact=await Artifacts.WriteJsonAsync(Wire.Id("disassembly"),listing,NativeDecoder.Version);
        Upsert(artifact.Id,"artifact",artifact);
        return new { artifact_id=artifact.Id,decoder=listing.DecoderVersion,qualification=listing.SourceQualification,
            instruction_count=listing.Instructions.Count,region_count=snapshot.Regions.Count,warnings=listing.Warnings,
            preview=listing.Instructions.Take(20).ToArray(),annotations=listing.Annotations.Take(20).ToArray() };
    }
    public async Task<NativeDiff> DiffAsync(string original,string candidate,int limit)=>
        NativeDecoder.Diff(await NativeSnapshotAsync(original),await NativeSnapshotAsync(candidate),limit);
}
