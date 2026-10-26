using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public async Task<BundleImportPlan> PrepareBundleImportAsync(string artifactId,long maximumDecompressedBytes=1073741824,int maximumDurationSeconds=300)
    {
        if(maximumDecompressedBytes is <1024 or >2147483648||maximumDurationSeconds is <1 or >600||maximumDurationSeconds>Limits.MaximumDurationSeconds)
            throw new LabException("invalid_plan","Bundle import byte or duration policy exceeds its bounds.");
        var artifact=Record<Artifact>(artifactId,"artifact");
        if(!artifact.Complete||artifact.MediaType!="application/zip"||artifact.Length>2147483648)
            throw new LabException("invalid_request","Bundle import requires a complete retained ZIP artifact within its input budget.");
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(maximumDurationSeconds));
        string path=await Artifacts.VerifiedPathAsync(artifact,deadline.Token);
        BundleArchiveReader.ValidateDirectory(path); using var zip=ZipFile.OpenRead(path);
        var manifest=await BundleArchiveReader.ManifestAsync(zip,maximumDecompressedBytes,deadline.Token);
        var plan=new BundleImportPlan(Wire.Id("bundle-import-plan"),artifact,Wire.Hash(manifest),maximumDecompressedBytes,maximumDurationSeconds);
        Catalog.Put(plan.Id,"bundle_import_plan",plan); return plan;
    }
    private Artifact ImportedArtifactManifest(Artifact incoming)
    {
        try
        {
            var existing=Record<Artifact>(incoming.Id,"artifact");
            if(existing.Hash!=incoming.Hash||existing.Length!=incoming.Length||!existing.Complete)
                throw new LabException("artifact_corrupt","Imported bytes conflict with an existing content manifest.");
            return existing;
        }
        catch(LabException error) when(error.Code=="record_not_found") { return incoming; }
    }
    public Operation StartBundleImport(string planId,string key)
    {
        var plan=Record<BundleImportPlan>(planId,"bundle_import_plan");
        if(plan.MaximumDurationSeconds>Limits.MaximumDurationSeconds) throw new LabException("resource_limit","Bundle import exceeds workspace duration policy.");
        var target=new TargetIdentity(plan.Id,0,0,plan.Id,"retained-evidence","local");
        return Start("bundle_import",target,key,Wire.Hash(new { planId,plan_hash=Wire.Hash(plan) }),async(operation,cancellation)=>
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
            string path=await Artifacts.VerifiedPathAsync(plan.Bundle,deadline.Token); BundleArchiveReader.ValidateDirectory(path);
            using var zip=ZipFile.OpenRead(path); var manifest=await BundleArchiveReader.ManifestAsync(zip,plan.MaximumDecompressedBytes,deadline.Token);
            if(Wire.Hash(manifest)!=plan.ManifestHash) throw new LabException("artifact_corrupt","Bundle manifest differs from its frozen import plan.");
            if(!DiskSpace.Fits(Catalog.Root,plan.MaximumDecompressedBytes,Limits.DiskReserveBytes))
                throw new LabException("resource_limit","Bundle staging would violate workspace disk reserve.");
            string id=Wire.Id("bundle-import"); var members=new List<ImportedEvidenceMember>(); var artifacts=new Dictionary<string,Artifact>();
            artifacts[plan.Bundle.Id]=ImportedArtifactManifest(plan.Bundle);
            foreach(var item in manifest.Entries)
            {
                deadline.Token.ThrowIfCancellationRequested(); var entry=zip.GetEntry(item.Path)!;
                string incoming=Artifacts.CreateIncoming(operation.Id);
                if(item.Path.StartsWith("records/",StringComparison.Ordinal))
                {
                    byte[] bytes=await BundleArchiveReader.ReadSmallAsync(entry,1048576,deadline.Token);
                    if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=item.ExportedHash)
                        throw new LabException("artifact_corrupt","Imported record member checksum differs.");
                    BundleArchiveReader.VerifyRecord(item,bytes); await File.WriteAllBytesAsync(incoming,bytes,deadline.Token);
                }
                else
                {
                    await using var output=new FileStream(incoming,FileMode.CreateNew,FileAccess.Write,FileShare.None);
                    await BundleArchiveReader.CopyVerifiedAsync(entry,output,item.Length,item.ExportedHash,deadline.Token); output.Flush(true);
                }
                var sealedArtifact=await Artifacts.SealAsync(incoming,item.MediaType,"verified-bundle-member/origin-unverified/v1",true,item.Length,deadline.Token);
                var artifact=ImportedArtifactManifest(sealedArtifact); artifacts[artifact.Id]=artifact;
                var member=new ImportedEvidenceMember("imported-member-"+Wire.Hash(new { import_id=id,item.Path }),id,item.Path,item.SourceId,item.Kind,item.SourceRevision,
                    item.SourceHash,item.ExportedHash,item.Length,item.MediaType,item.Redacted,artifact.Id,artifact.Hash);
                members.Add(member);
            }
            var receipt=await Artifacts.WriteJsonAsync(operation.Id,new { plan,source_manifest=manifest,members,
                qualification="imported_hash_verified_origin_unverified" },"verified-bundle-import/v1",16777216,deadline.Token);
            receipt=ImportedArtifactManifest(receipt); artifacts[receipt.Id]=receipt;
            var result=new BundleImportResult(id,operation.Id,plan.Id,Wire.Hash(plan),plan.Bundle.Id,plan.Bundle.Hash,receipt.Id,receipt.Hash,
                members.Count(m=>m.SourcePath.StartsWith("records/",StringComparison.Ordinal)),members.Count(m=>m.SourcePath.StartsWith("artifacts/",StringComparison.Ordinal)),
                manifest.Omissions.Count,manifest.PrivacyFiltered);
            NotebookRecord RecordFor<T>(string recordId,string kind,T value)=>new(recordId,kind,1,Wire.Hash(value),Wire.Serialize(value),DateTimeOffset.UtcNow);
            var records=members.Select(m=>RecordFor(m.Id,"imported_evidence_member",m)).Append(RecordFor(result.Id,"bundle_import_result",result)).ToArray();
            await Artifacts.VerifiedPathAsync(plan.Bundle,deadline.Token);
            deadline.Token.ThrowIfCancellationRequested(); Catalog.PublishBundleImport(operation.Id,result.Id,records,artifacts.Values.ToArray()); return true;
        });
    }
    public async Task<BundleImportResult> VerifyBundleImportAsync(string resultId,CancellationToken cancellation=default)
    {
        var result=Record<BundleImportResult>(resultId,"bundle_import_result"); var plan=Record<BundleImportPlan>(result.PlanId,"bundle_import_plan");
        var operation=Catalog.GetOperation(result.OperationId);
        if(operation.State!=OperationState.Completed||operation.ResultId!=result.Id||result.PlanHash!=Wire.Hash(plan)||
            result.BundleArtifactId!=plan.Bundle.Id||result.BundleHash!=plan.Bundle.Hash)
            throw new LabException("artifact_corrupt","Import result lacks its exact completed plan/bundle evidence.");
        var artifact=Record<Artifact>(result.ManifestArtifactId,"artifact");
        if(artifact.Hash!=result.ManifestHash||artifact.Length>16777216) throw new LabException("artifact_corrupt","Import receipt differs from its retained index.");
        using var receipt=JsonDocument.Parse(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact,cancellation),cancellation));
        var source=receipt.RootElement.GetProperty("source_manifest").Deserialize<ExportManifest>(Wire.Json)!;
        var members=receipt.RootElement.GetProperty("members").Deserialize<ImportedEvidenceMember[]>(Wire.Json)!;
        if(Wire.Hash(receipt.RootElement.GetProperty("plan").Deserialize<BundleImportPlan>(Wire.Json))!=result.PlanHash||Wire.Hash(source)!=plan.ManifestHash||
            members.Length!=result.Records+result.Artifacts||members.Select(m=>m.Id).Distinct().Count()!=members.Length||
            result.PrivacyFiltered!=source.PrivacyFiltered||result.OmittedPayloads!=source.Omissions.Count)
            throw new LabException("artifact_corrupt","Import receipt plan, source manifest, or complete member count differs.");
        await Artifacts.VerifiedPathAsync(plan.Bundle,cancellation);
        foreach(var member in members)
        {
            var entry=source.Entries.SingleOrDefault(e=>e.Path==member.SourcePath);
            if(entry is null||member.SourceId!=entry.SourceId||member.SourceKind!=entry.Kind||member.SourceRevision!=entry.SourceRevision||member.SourceHash!=entry.SourceHash||
                member.ExportedHash!=entry.ExportedHash||member.Redacted!=entry.Redacted||member.Length!=entry.Length||member.MediaType!=entry.MediaType||
                member.Qualification!="imported_hash_verified_origin_unverified"||member.Id!="imported-member-"+Wire.Hash(new { import_id=result.Id,Path=entry.Path }))
                throw new LabException("artifact_corrupt","Imported member differs from its verified source declaration.");
            if(member.ImportId!=result.Id||Wire.Hash(Record<ImportedEvidenceMember>(member.Id,"imported_evidence_member"))!=Wire.Hash(member))
                throw new LabException("artifact_corrupt","Imported member differs from its receipt or catalog identity.");
            var payload=Record<Artifact>(member.ArtifactId,"artifact");
            if(payload.Hash!=member.ArtifactHash||payload.Hash!=member.ExportedHash||payload.Length!=member.Length)
                throw new LabException("artifact_corrupt","Imported payload manifest differs from its source member.");
            await Artifacts.VerifiedPathAsync(payload,cancellation);
        }
        return result;
    }
    public Page<NotebookRecord> ImportedMembers(string importId,int limit=100,string? cursor=null)
    {
        Record<BundleImportResult>(importId,"bundle_import_result");
        // The immutable ID contains the import scope; page filtering never depends on caller-provided paths.
        return Catalog.ImportedMembers(importId,limit,cursor);
    }
    public Operation StartImportedCaptureAnalysis(string memberId,string key)
    {
        var member=Record<ImportedEvidenceMember>(memberId,"imported_evidence_member");
        if(member.Redacted||member.SourceKind!="capture"||!member.SourcePath.StartsWith("records/",StringComparison.Ordinal))
            throw new LabException("not_collected","Imported capture payload was omitted or is not a capture record.");
        var result=Record<BundleImportResult>(member.ImportId,"bundle_import_result"); var plan=Record<BundleImportPlan>(result.PlanId,"bundle_import_plan");
        var target=new TargetIdentity(member.Id,0,0,member.Id,"retained-evidence","analysis");
        return Start("imported_capture_analysis",target,key,Wire.Hash(new { member_id=member.Id,member_hash=Wire.Hash(member),import_hash=Wire.Hash(result),parser=TraceAnalyzer.Version }),async(operation,cancellation)=>
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
            using var permit=await activityScheduler.EnterParserAsync(deadline.Token);
            await VerifyBundleImportAsync(result.Id,deadline.Token);
            var payload=Record<Artifact>(member.ArtifactId,"artifact");
            if(payload.Length>1048576) throw new LabException("resource_limit","Imported capture metadata exceeds its read budget.");
            var source=Wire.Deserialize<Capture>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(payload,deadline.Token),deadline.Token));
            if(source.Id!=member.SourceId||source.ArtifactId is null||source.State is not (CaptureState.Sealed or CaptureState.Incomplete)||
                Wire.Hash(source.Profile)!=Wire.Hash(Profiles.Resolve(source.Profile.Name)))
                throw new LabException("not_comparable","Imported capture identity, profile, or retained-byte state is unsupported.");
            var receiptArtifact=Record<Artifact>(result.ManifestArtifactId,"artifact");
            using var receipt=JsonDocument.Parse(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(receiptArtifact,deadline.Token),deadline.Token));
            var raw=receipt.RootElement.GetProperty("members").Deserialize<ImportedEvidenceMember[]>(Wire.Json)!
                .SingleOrDefault(m=>m.SourceId==source.ArtifactId&&m.SourcePath.StartsWith("artifacts/",StringComparison.Ordinal));
            if(raw is null) throw new LabException("not_collected","Capture raw bytes were not included in this bundle.");
            if(raw.MediaType!="application/vnd.microsoft.nettrace") throw new LabException("invalid_request","Imported capture requires a retained NetTrace member.");
            var trace=Record<Artifact>(raw.ArtifactId,"artifact");
            if(trace.Length>Limits.MaximumCaptureBytes) throw new LabException("resource_limit","Imported raw trace exceeds this workspace's analysis byte budget.");
            string path=await Artifacts.VerifiedPathAsync(trace,deadline.Token);
            string captureId="imported-capture-"+Wire.Hash(new { import_id=result.Id,member_id=member.Id });
            var capture=source with { Id=captureId,ArtifactId=trace.Id,Reason="imported_source_origin_unverified" };
            Upsert(capture.Id,"capture",capture);
            Upsert(capture.Id+"-import","imported_capture_link",new { capture_id=capture.Id,import_id=result.Id,source_member_id=member.Id,source_hash=member.SourceHash,
                qualification="imported_hash_verified_origin_unverified",meaning="Offline source process identity; no live target capability is established." });
            deadline.Token.ThrowIfCancellationRequested(); var parsed=await ParseTraceAsync(capture,trace,operation.Id,deadline.Token);
            var summary=parsed with { Quality=parsed.Quality with { Coverage="partial",Warnings=parsed.Quality.Warnings.Append("imported_source_origin_unverified").Distinct().ToArray() } };
            deadline.Token.ThrowIfCancellationRequested();
            var artifact=await Artifacts.WriteJsonAsync(operation.Id,summary,TraceAnalyzer.Version,16777216,deadline.Token); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"imported_trace_analysis");
            var index=new AnalysisIndex(summary.Id,capture.Id,artifact.Id,summary.ParserVersion,summary.Quality); Catalog.Put(index.Id,"analysis",index);
            Catalog.Put(index.Id+"-capture","capture_analysis",new { capture_id=captureId,analysis_id=index.Id });
            Catalog.Transition(operation.Id,OperationState.Completed,index.Id); return true;
        });
    }
}
