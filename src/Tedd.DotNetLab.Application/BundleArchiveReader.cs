using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

internal static class BundleArchiveReader
{
    public static LabException Corrupt(Exception error)=>new("artifact_corrupt","Evidence bundle structure or encoded JSON could not be validated.","Preserve the original bundle and regenerate its export from verified source evidence.");
    private static bool Hash(string value)=>Regex.IsMatch(value??"",@"\A[0-9a-f]{64}\z");
    public static void ValidateDirectory(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(file.Length<22) throw new LabException("artifact_corrupt","ZIP end directory is absent.");
        int length=(int)Math.Min(65557,file.Length); byte[] tail=new byte[length]; file.Position=file.Length-length; file.ReadExactly(tail);
        for(int offset=length-22;offset>=0;offset--)
        {
            var data=tail.AsSpan(offset);
            if(BinaryPrimitives.ReadUInt32LittleEndian(data)!=0x06054b50||offset+22+BinaryPrimitives.ReadUInt16LittleEndian(data[20..])!=length) continue;
            ushort disk=BinaryPrimitives.ReadUInt16LittleEndian(data[4..]),directoryDisk=BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            ushort onDisk=BinaryPrimitives.ReadUInt16LittleEndian(data[8..]),count=BinaryPrimitives.ReadUInt16LittleEndian(data[10..]);
            uint size=BinaryPrimitives.ReadUInt32LittleEndian(data[12..]),start=BinaryPrimitives.ReadUInt32LittleEndian(data[16..]);
            if(disk!=0||directoryDisk!=0||onDisk!=count||count is <1 or >1001||size>262144||
                (long)start+size!=file.Length-length+offset)
                throw new LabException("resource_limit","ZIP directory, split/ZIP64 layout, or entry metadata exceeds the supported bundle bounds.");
            return;
        }
        throw new LabException("artifact_corrupt","ZIP end directory could not be validated.");
    }
    public static async Task<ExportManifest> ManifestAsync(ZipArchive zip,long maximumBytes,CancellationToken cancellation)
    {
        if(zip.Entries.Count is <1 or >1001||zip.Entries.Select(e=>e.FullName).Distinct(StringComparer.Ordinal).Count()!=zip.Entries.Count)
            throw new LabException("artifact_corrupt","Bundle ZIP members are duplicated or exceed their count budget.");
        var entry=zip.GetEntry("manifest.json")??throw new LabException("artifact_corrupt","Bundle manifest is absent.");
        byte[] bytes=await ReadSmallAsync(entry,1048576,cancellation);
        ExportManifest manifest;
        try { manifest=Wire.Deserialize<ExportManifest>(new UTF8Encoding(false,true).GetString(bytes)); }
        catch(Exception error) when(error is JsonException or DecoderFallbackException) { throw Corrupt(error); }
        if(manifest is null||manifest.Entries is null||manifest.Omissions is null||manifest.Entries.Any(e=>e is null)||manifest.Omissions.Any(o=>o is null))
            throw new LabException("artifact_corrupt","Bundle inclusion/omission lists are absent or malformed.");
        if(manifest.SchemaVersion!=Wire.Version||manifest.PolicyVersion!="local-explicit-export/v1")
            throw new LabException("schema_unsupported","Bundle schema or export privacy policy is unsupported.");
        if(manifest.Entries.Count>1000||manifest.Omissions.Count>1000||manifest.Entries.Count+manifest.Omissions.Count<1||manifest.Entries.Count+1!=zip.Entries.Count||
            !Hash(manifest.PlanHash)||string.IsNullOrWhiteSpace(manifest.ExportId)||manifest.ExportId.Length>512||
            string.IsNullOrWhiteSpace(manifest.PlanId)||manifest.PlanId.Length>512||manifest.Entries.Select(e=>e.Path).Distinct().Count()!=manifest.Entries.Count||
            manifest.Entries.Select(e=>(e.Kind,e.SourceId,e.SourceRevision)).Distinct().Count()!=manifest.Entries.Count)
            throw new LabException("artifact_corrupt","Bundle identity, inclusion budget, or member identities differ.");
        long total=bytes.Length;
        foreach(var item in manifest.Entries)
        {
            cancellation.ThrowIfCancellationRequested();
            if(item.Path is null||!Regex.IsMatch(item.Path,@"\A(records/[0-9a-f]{64}\.json|artifacts/[0-9a-f]{64})\z"))
                throw new LabException("invalid_path","Bundle member lies outside the generated hash layout.");
            if(item.SchemaVersion!=Wire.Version) throw new LabException("schema_unsupported","Bundle member schema is unsupported.");
            if(!Hash(item.SourceHash)||!Hash(item.ExportedHash)||item.Length<0||
                string.IsNullOrWhiteSpace(item.SourceId)||item.SourceId.Length>512||string.IsNullOrWhiteSpace(item.MediaType)||item.MediaType.Length>256||
                !Regex.IsMatch(item.Kind??"",@"\A[a-z0-9_]{1,64}\z")||item.SourceRevision is <1)
                throw new LabException("artifact_corrupt","Bundle member hash, schema, type, or length is invalid.");
            if(item.Kind is "grant" or "export_plan" or "discovery_snapshot")
                throw new LabException("permission_denied","Control authority cannot be imported from an evidence bundle.");
            bool artifact=item.Path.StartsWith("artifacts/",StringComparison.Ordinal);
            if(artifact&&item.Kind!="artifact"||artifact&&(item.Path!="artifacts/"+item.SourceHash||item.SourceId!="art-"+item.SourceHash||
                item.SourceHash!=item.ExportedHash||item.Redacted||item.SourceRevision is not null)||
                !artifact&&(item.MediaType!="application/json"||item.Length>1048576)||manifest.PrivacyFiltered!=item.Redacted&&!artifact)
                throw new LabException("artifact_corrupt","Bundle member role, revision, or privacy treatment differs.");
            if(!artifact&&item.SourceRevision is long revision&&item.Path!="records/"+Wire.Hash(new { Id=item.SourceId,Revision=revision,Hash=item.SourceHash })+".json")
                throw new LabException("artifact_corrupt","Record path does not bind its declared source revision/hash.");
            var member=zip.GetEntry(item.Path)??throw new LabException("artifact_corrupt","Declared bundle member is absent.");
            if(member.Length!=item.Length||total>maximumBytes-item.Length) throw new LabException("resource_limit","Bundle decompressed bytes or declared length exceeds policy.");
            total+=item.Length;
        }
        if(total>maximumBytes||manifest.PrivacyFiltered&&manifest.Entries.Any(e=>e.Kind=="artifact"))
            throw new LabException("artifact_corrupt","Bundle privacy or decompressed-byte declaration differs.");
        foreach(var omission in manifest.Omissions)
            if(omission.Kind is not ("record_payload" or "artifact_payload")||string.IsNullOrWhiteSpace(omission.Id)||omission.Id.Length>512||
                omission.Reason is not ("privacy_metadata_only" or "privacy_omits_original_diagnostic_code_and_trace_bytes")||!manifest.PrivacyFiltered)
                throw new LabException("artifact_corrupt","Bundle omission declaration differs from its privacy policy.");
        if(manifest.Omissions.Select(o=>(o.Kind,o.Id)).Distinct().Count()!=manifest.Omissions.Count||
            manifest.Entries.Where(e=>e.Redacted).Any(e=>manifest.Omissions.Count(o=>o.Kind=="record_payload"&&o.Id==e.SourceId)!=1)||
            manifest.Omissions.Any(o=>o.Kind=="record_payload"&&!manifest.Entries.Any(e=>e.Redacted&&e.SourceId==o.Id)))
            throw new LabException("artifact_corrupt","Bundle redacted payloads and omission list disagree.");
        return manifest;
    }
    public static async Task<byte[]> ReadSmallAsync(ZipArchiveEntry entry,int maximum,CancellationToken cancellation)
    {
        if(entry.Length<0||entry.Length>maximum) throw new LabException("resource_limit","Bundle JSON exceeds its read budget.");
        using var output=new MemoryStream(); await CopyVerifiedAsync(entry,output,maximum,null,cancellation); return output.ToArray();
    }
    public static async Task CopyVerifiedAsync(ZipArchiveEntry entry,Stream output,long maximum,string? expectedHash,CancellationToken cancellation)
    {
        await using var source=entry.Open(); using var checksum=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer=new byte[65536]; long length=0;
        while(true)
        {
            int read=await source.ReadAsync(buffer,cancellation); if(read==0) break;
            if(length>maximum-read||length>entry.Length-read) throw new LabException("resource_limit","Actual decompressed bytes exceed the declared member budget.");
            length+=read; checksum.AppendData(buffer,0,read); await output.WriteAsync(buffer.AsMemory(0,read),cancellation);
        }
        if(length!=entry.Length||expectedHash is not null&&Convert.ToHexStringLower(checksum.GetHashAndReset())!=expectedHash)
            throw new LabException("artifact_corrupt","Bundle member length or checksum differs.");
    }
    public static void VerifyRecord(ExportEntry item,byte[] bytes)
    {
        try { VerifyRecordCore(item,bytes); }
        catch(Exception error) when(error is JsonException or FormatException or KeyNotFoundException or InvalidOperationException) { throw Corrupt(error); }
    }
    private static void VerifyRecordCore(ExportEntry item,byte[] bytes)
    {
        using var json=JsonDocument.Parse(bytes);
        if(item.Redacted)
        {
            var value=json.RootElement;
            if(value.GetProperty("id").GetString()!=item.SourceId||value.GetProperty("kind").GetString()!=item.Kind||
                value.GetProperty("source_hash").GetString()!=item.SourceHash||!value.GetProperty("payload_omitted").GetBoolean()||value.GetProperty("policy").GetString()!="metadata_only"||
                item.SourceRevision is long revision&&long.Parse(value.GetProperty("revision").GetString()!,System.Globalization.CultureInfo.InvariantCulture)!=revision)
                throw new LabException("artifact_corrupt","Redacted record metadata differs from its member declaration.");
        }
        else if(Wire.Hash(json.RootElement)!=item.SourceHash)
            throw new LabException("artifact_corrupt","Record semantic source hash differs from the original catalog payload.");
    }
}
