using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public async Task<ExportPlan> PrepareExportAsync(IReadOnlyList<string> recordIds,IReadOnlyList<string> artifactIds,bool privacyFiltered=true,
        long maximumBundleBytes=1073741824,int maximumDurationSeconds=300)
    {
        if(recordIds.Count+artifactIds.Count is <1 or >1000||recordIds.Distinct().Count()!=recordIds.Count||artifactIds.Distinct().Count()!=artifactIds.Count||
            maximumBundleBytes is <1024 or >2147483648||maximumDurationSeconds is <1 or >600)
            throw new LabException("invalid_plan","Export requires a distinct bounded inclusion list, byte budget, and duration.");
        var records=new List<FrozenRecordReference>(); var artifacts=new List<Artifact>(); long estimated=0;
        foreach(string id in recordIds.Order(StringComparer.Ordinal))
        {
            var record=Catalog.Get(id);
            if(record.Kind is "grant" or "export_plan" or "discovery_snapshot")
                throw new LabException("permission_denied","Control authority and discovery snapshots cannot be exported as evidence.");
            records.Add(new(record.Id,record.Revision,record.Hash));
            estimated=checked(estimated+Encoding.UTF8.GetByteCount(privacyFiltered?Wire.Serialize(new { record.Id,record.Kind,record.Revision,record.Hash }):record.Json)+1024);
        }
        foreach(string id in artifactIds.Order(StringComparer.Ordinal))
        {
            var artifact=Record<Artifact>(id,"artifact");
            if(!artifact.Complete||artifact.Length<0) throw new LabException("capture_incomplete","Export requires complete sealed artifact bytes.");
            await Artifacts.VerifiedPathAsync(artifact);
            artifacts.Add(artifact); estimated=checked(estimated+(privacyFiltered?1024:artifact.Length+1024));
        }
        if(estimated+65536>maximumBundleBytes) throw new LabException("resource_limit","Declared inclusion exceeds the export byte budget.");
        var plan=new ExportPlan(Wire.Id("export-plan"),records,artifacts,privacyFiltered,maximumBundleBytes,maximumDurationSeconds);
        Catalog.Put(plan.Id,"export_plan",plan); return plan;
    }
    private static TargetIdentity ExportTarget(ExportPlan plan)=>new(plan.Id,0,0,plan.Id,"workspace","local");
    // Local CLI only. Raw-trace permission is an explicit separate choice, never inferred from a selected record.
    public Grant IssueExportGrant(string planId,int seconds,int maximumOperations,bool allowRawTrace=false)
    {
        var plan=Record<ExportPlan>(planId,"export_plan");
        if(seconds is <1 or >3600||maximumOperations is <1 or >1000)
            throw new LabException("invalid_request","Invalid export grant lifetime or budget.");
        if(!plan.PrivacyFiltered&&plan.Artifacts.Count>0&&!allowRawTrace)
            throw new LabException("permission_denied","Unfiltered artifact bytes require explicit raw-evidence consent; a media-type label cannot prove that content is nonsensitive.");
        var grant=new Grant(Wire.Id("grant"),Catalog.Root,plan.Id,0,Permission.ExportEvidence,DateTimeOffset.UtcNow.AddSeconds(seconds),maximumOperations,plan.ContentHash);
        Catalog.Put(grant.Id,"grant",grant); return grant;
    }
    public Operation StartExport(string planId,string grantId,string key)
    {
        var plan=Record<ExportPlan>(planId,"export_plan"); var grant=Record<Grant>(grantId,"grant");
        Policies.Authorize(grant,Catalog.Root,ExportTarget(plan),Permission.ExportEvidence,DateTimeOffset.UtcNow,plan.ContentHash);
        if(plan.MaximumDurationSeconds>Limits.MaximumDurationSeconds) throw new LabException("resource_limit","Export exceeds workspace duration policy.");
        return Start("export",ExportTarget(plan),key,Wire.Hash(new { planId,plan_hash=plan.ContentHash,grantId }),async(operation,cancellation)=>
        {
            Policies.Authorize(grant,Catalog.Root,ExportTarget(plan),Permission.ExportEvidence,DateTimeOffset.UtcNow,plan.ContentHash); Catalog.UseGrant(grant);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
            var entries=new List<ExportEntry>(); var omissions=new List<ExportOmission>(); string incoming=Artifacts.CreateIncoming(operation.Id);
            var sourceRecords=plan.Records.Select(reference=>
            {
                var record=Catalog.Get(reference.Id,reference.Revision);
                if(record.Hash!=reference.Hash||Wire.Hash(Wire.Deserialize<JsonElement>(record.Json))!=reference.Hash)
                    throw new LabException("artifact_corrupt","Export record differs from its frozen revision/hash.");
                return record;
            }).ToArray();
            var paths=new Dictionary<string,string>();
            foreach(var artifact in plan.Artifacts) paths[artifact.Id]=await Artifacts.VerifiedPathAsync(artifact,deadline.Token);
            if(!DiskSpace.Fits(Catalog.Root,plan.MaximumBundleBytes,Limits.DiskReserveBytes))
                throw new LabException("resource_limit","Export would violate the disk reserve.");
            string exportId=Wire.Id("export"); ExportManifest manifest;
            await using(var file=new FileStream(incoming,FileMode.CreateNew,FileAccess.ReadWrite,FileShare.None))
            {
                using(var bounded=new ExportBudgetStream(file,plan.MaximumBundleBytes))
                using(var zip=new ZipArchive(bounded,ZipArchiveMode.Create,true))
                {
                    foreach(var record in sourceRecords)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        byte[] bytes=Encoding.UTF8.GetBytes(plan.PrivacyFiltered?Wire.Serialize(new { record.Id,record.Kind,record.Revision,source_hash=record.Hash,
                            payload_omitted=true,policy="metadata_only" }):record.Json);
                        string path="records/"+Wire.Hash(new { record.Id,record.Revision,record.Hash })+".json";
                        await AddAsync(zip,path,new MemoryStream(bytes,false),entries,record.Kind,record.Id,record.Hash,"application/json",plan.PrivacyFiltered,deadline.Token);
                        entries[^1]=entries[^1] with { SourceRevision=record.Revision };
                        if(plan.PrivacyFiltered) omissions.Add(new("record_payload",record.Id,"privacy_metadata_only"));
                    }
                    foreach(var artifact in plan.Artifacts)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        if(plan.PrivacyFiltered) { omissions.Add(new("artifact_payload",artifact.Id,"privacy_omits_original_diagnostic_code_and_trace_bytes")); continue; }
                        await using var source=new FileStream(paths[artifact.Id],FileMode.Open,FileAccess.Read,FileShare.Read);
                        await AddAsync(zip,"artifacts/"+artifact.Hash,source,entries,"artifact",artifact.Id,artifact.Hash,artifact.MediaType,false,deadline.Token);
                        if(entries[^1].ExportedHash!=artifact.Hash||entries[^1].Length!=artifact.Length)
                            throw new LabException("artifact_corrupt","Export source changed during bundle creation.");
                    }
                    manifest=new(exportId,plan.Id,plan.ContentHash,Wire.Version,plan.PolicyVersion,plan.PrivacyFiltered,entries,omissions);
                    byte[] manifestBytes=Encoding.UTF8.GetBytes(Wire.Serialize(manifest));
                    await using var target=zip.CreateEntry("manifest.json",CompressionLevel.NoCompression).Open(); await target.WriteAsync(manifestBytes,deadline.Token);
                }
                if(file.Length>plan.MaximumBundleBytes) throw new LabException("resource_limit","Export bundle exceeded its byte cap.");
                file.Flush(true);
            }
            await VerifyBundleAsync(incoming,manifest,plan.MaximumBundleBytes,deadline.Token);
            var sealedArtifact=await Artifacts.SealAsync(incoming,"application/zip","verified-evidence-export/v1",true,plan.MaximumBundleBytes,deadline.Token);
            Upsert(sealedArtifact.Id,"artifact",sealedArtifact); Catalog.Pin(sealedArtifact.Id,"verified_export");
            var result=new ExportResult(exportId,operation.Id,plan.Id,plan.ContentHash,sealedArtifact.Id,Wire.Hash(manifest),sourceRecords.Length,
                entries.Count(entry=>entry.Kind=="artifact"),omissions.Count,plan.PrivacyFiltered);
            Catalog.Put(result.Id,"export_result",result);
            Catalog.AppendAudit("evidence_exported",result.Id,Wire.Serialize(new { plan_id=plan.Id,plan_hash=plan.ContentHash,artifact_id=sealedArtifact.Id,privacy_filtered=plan.PrivacyFiltered }));
            Catalog.Transition(operation.Id,OperationState.Completed,result.Id); return true;
        });
    }
    private static async Task AddAsync(ZipArchive zip,string path,Stream source,List<ExportEntry> entries,string kind,string id,string hash,
        string mediaType,bool redacted,CancellationToken cancellation)
    {
        await using var target=zip.CreateEntry(path,CompressionLevel.NoCompression).Open();
        using var checksum=IncrementalHash.CreateHash(HashAlgorithmName.SHA256); byte[] buffer=new byte[65536]; long length=0; int read;
        while((read=await source.ReadAsync(buffer,cancellation))>0)
        { await target.WriteAsync(buffer.AsMemory(0,read),cancellation); checksum.AppendData(buffer,0,read); length=checked(length+read); }
        entries.Add(new(path,kind,id,hash,Convert.ToHexStringLower(checksum.GetHashAndReset()),length,mediaType,Wire.Version,redacted));
    }
    private static async Task VerifyBundleAsync(string path,ExportManifest expected,long maximumBytes,CancellationToken cancellation)
    {
        using var zip=ZipFile.OpenRead(path);
        if(zip.Entries.Count!=expected.Entries.Count+1||zip.Entries.Select(entry=>entry.FullName).Distinct().Count()!=zip.Entries.Count)
            throw new LabException("artifact_corrupt","Export archive entry count or uniqueness differs.");
        var manifestEntry=zip.GetEntry("manifest.json")??throw new LabException("artifact_corrupt","Export manifest is absent.");
        if(manifestEntry.Length>1048576) throw new LabException("resource_limit","Export manifest exceeds its bound.");
        using var reader=new StreamReader(manifestEntry.Open(),Encoding.UTF8);
        var actual=Wire.Deserialize<ExportManifest>(await reader.ReadToEndAsync(cancellation));
        if(Wire.Hash(actual)!=Wire.Hash(expected)) throw new LabException("artifact_corrupt","Export manifest differs from the frozen selection.");
        long total=manifestEntry.Length;
        foreach(var item in expected.Entries)
        {
            if(!Regex.IsMatch(item.Path,@"\A(records/[0-9a-f]{64}\.json|artifacts/[0-9a-f]{64})\z"))
                throw new LabException("invalid_path","Export member path is outside the allowed bundle layout.");
            var entry=zip.GetEntry(item.Path)??throw new LabException("artifact_corrupt","Declared export member is absent.");
            total=checked(total+entry.Length);
            if(entry.Length!=item.Length||total>maximumBytes) throw new LabException("resource_limit","Export member length or decompressed budget differs.");
            await using var stream=entry.Open();
            if(Convert.ToHexStringLower(await SHA256.HashDataAsync(stream,cancellation))!=item.ExportedHash)
                throw new LabException("artifact_corrupt","Export member hash differs from the manifest.");
        }
    }
    public async Task<ExportManifest> VerifyExportAsync(string resultId)
    {
        var result=Record<ExportResult>(resultId,"export_result"); var plan=Record<ExportPlan>(result.PlanId,"export_plan");
        string path=await Artifacts.VerifiedPathAsync(Record<Artifact>(result.ArtifactId,"artifact"));
        using var zip=ZipFile.OpenRead(path); var entry=zip.GetEntry("manifest.json")??throw new LabException("artifact_corrupt","Export manifest is absent.");
        if(entry.Length>1048576) throw new LabException("resource_limit","Export manifest exceeds its bound.");
        using var reader=new StreamReader(entry.Open(),Encoding.UTF8); var manifest=Wire.Deserialize<ExportManifest>(await reader.ReadToEndAsync());
        if(manifest.PlanId!=plan.Id||manifest.PlanHash!=plan.ContentHash||Wire.Hash(manifest)!=result.ManifestHash)
            throw new LabException("artifact_corrupt","Export manifest does not match its retained plan and result.");
        await VerifyBundleAsync(path,manifest,plan.MaximumBundleBytes,CancellationToken.None); return manifest;
    }
}
