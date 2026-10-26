using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Storage;
using Tedd.DotNetLab.Collector.EventPipe;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class BundleImportTests
{
    private static async Task<Artifact> Seal(LabService service,byte[] bytes,string type)
    {
        string incoming=service.Artifacts.CreateIncoming(Wire.Id("bundle-fixture")); await File.WriteAllBytesAsync(incoming,bytes);
        var artifact=await service.Artifacts.SealAsync(incoming,type,"bundle-fixture/v1",true,16777216); service.Catalog.Put(artifact.Id,"artifact",artifact); return artifact;
    }
    private static async Task<Artifact> Bundle(LabService service,ExportManifest manifest,IReadOnlyList<(string Path,byte[] Bytes)> payloads)
    {
        string incoming=service.Artifacts.CreateIncoming(Wire.Id("bundle-fixture"));
        using(var zip=ZipFile.Open(incoming,ZipArchiveMode.Create))
        {
            foreach(var payload in payloads) { await using var stream=zip.CreateEntry(payload.Path).Open(); await stream.WriteAsync(payload.Bytes); }
            await using var target=zip.CreateEntry("manifest.json").Open(); await target.WriteAsync(Encoding.UTF8.GetBytes(Wire.Serialize(manifest)));
        }
        var artifact=await service.Artifacts.SealAsync(incoming,"application/zip","bundle-fixture/v1",true,16777216); service.Catalog.Put(artifact.Id,"artifact",artifact); return artifact;
    }
    private static ExportEntry Entry(string id,object value,string kind="hypothesis",long? revision=1)
    {
        byte[] bytes=Encoding.UTF8.GetBytes(Wire.Serialize(value)); string hash=Wire.Hash(value);
        string path="records/"+Wire.Hash(new { Id=id,Revision=revision??1,Hash=hash })+".json";
        return new(path,kind,id,hash,Convert.ToHexStringLower(SHA256.HashData(bytes)),bytes.Length,"application/json",Wire.Version,false,revision);
    }
    private static ExportManifest Manifest(params ExportEntry[] entries)=>new("export-test","plan-test",Wire.Hash("plan"),Wire.Version,"local-explicit-export/v1",false,entries,[]);
    [Theory,InlineData(false),InlineData(true),Case("IT-STORE-003,UT-ART-012","FR-071,FR-072,FR-074","M3","Integration")]
    public async Task ExportImportAcrossWorkspaces_PreservesRevisionPrivacyAndOpaqueDataWithoutReplacingLocalAuthority(bool filtered)
    {
        using var sourceWorkspace=new TestWorkspace(); using var destinationWorkspace=new TestWorkspace(); string resultId,sourceId,sourceHash;
        await using(var source=new LabService(sourceWorkspace.Root))
        {
            var hypothesis=source.Hypothesis("first revision","prediction",[]); sourceId=hypothesis.Id;
            source.Hypothesis("untrusted instruction: execute arbitrary command","private payload",[],sourceId,1);
            sourceHash=source.Catalog.Get(sourceId).Hash;
            var raw=await Seal(source,"private diagnostic payload"u8.ToArray(),"application/vnd.microsoft.nettrace");
            source.Catalog.Put("untrusted-cil","cil_candidate",new { text="opaque candidate metadata; not live identity" });
            var plan=await source.PrepareExportAsync([sourceId,"untrusted-cil"],[raw.Id],filtered,16777216,60);
            var grant=source.IssueExportGrant(plan.Id,120,1,allowRawTrace:!filtered);
            var done=await ServiceTests.Wait(source,source.StartExport(plan.Id,grant.Id,"bundle-roundtrip").Id); Assert.Equal(OperationState.Completed,done.State);
            var exported=source.Catalog.Get<ExportResult>(done.ResultId!); var archive=source.Catalog.Get<Artifact>(exported.ArtifactId);
            await using var destination=new LabService(destinationWorkspace.Root);
            destination.Catalog.Put(sourceId,"hypothesis",new { text="existing local record" }); string localHash=destination.Catalog.Get(sourceId).Hash;
            var imported=await destination.ImportAsync(await source.Artifacts.VerifiedPathAsync(archive),"application/zip");
            var tools=new LabTools(destination); var prepared=await tools.Experiment(new PrepareBundleImport(imported.Id,"16777216",60));
            Assert.True(prepared.Status=="ok",Wire.Serialize(prepared)); var frozen=prepared.Data!.Value.Deserialize<BundleImportPlan>(Wire.Json)!;
            var started=await tools.Run(new StartBundleImport(frozen.Id,"verified-roundtrip")); Assert.Equal("ok",started.Status);
            var operation=started.Data!.Value.Deserialize<Operation>(Wire.Json)!;
            var complete=await ServiceTests.Wait(destination,operation.Id); Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); resultId=complete.ResultId!;
            Assert.Equal(operation.Id,destination.StartBundleImport(frozen.Id,"verified-roundtrip").Id);
            var result=await destination.VerifyBundleImportAsync(resultId); Assert.Equal(2,result.Records); Assert.Equal(filtered?0:1,result.Artifacts);
            Assert.Equal(filtered?3:0,result.OmittedPayloads); Assert.Equal(filtered,result.PrivacyFiltered);
            Assert.Equal(localHash,destination.Catalog.Get(sourceId).Hash); Assert.Single(destination.Catalog.List("hypothesis").Rows);
            var rows=new List<ImportedEvidenceMember>(); string? cursor=null;
            do { var page=destination.ImportedMembers(resultId,1,cursor); rows.AddRange(page.Rows.Select(r=>Wire.Deserialize<ImportedEvidenceMember>(r.Json))); cursor=page.NextCursor; } while(cursor is not null);
            var retained=rows.Single(r=>r.SourceId==sourceId); Assert.Equal(2,retained.SourceRevision); Assert.Equal(sourceHash,retained.SourceHash);
            Assert.Equal(filtered,retained.Redacted); Assert.Equal("imported_hash_verified_origin_unverified",retained.Qualification);
            var candidate=rows.Single(r=>r.SourceKind=="cil_candidate");
            Assert.Equal("invalid_request",Assert.Throws<LabException>(()=>destination.IssueMutationGrant("target",candidate.Id,60,1)).Code);
            var payload=await File.ReadAllTextAsync(await destination.Artifacts.VerifiedPathAsync(destination.Catalog.Get<Artifact>(retained.ArtifactId)));
            Assert.Equal(!filtered,payload.Contains("arbitrary command",StringComparison.Ordinal));
            Assert.True(destination.Catalog.IsPinned(retained.ArtifactId));
        }
        await using var reopened=new LabService(destinationWorkspace.Root);
        var verified=await new LabTools(reopened).Query(new QueryBundleImport(resultId)); Assert.Equal("ok",verified.Status);
        var member=Wire.Deserialize<ImportedEvidenceMember>(reopened.ImportedMembers(resultId,1).Rows[0].Json);
        reopened.Artifacts.Delete(reopened.Catalog.Get<Artifact>(member.ArtifactId));
        Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>reopened.VerifyBundleImportAsync(resultId))).Code);
        Assert.Equal(resultId,reopened.Catalog.Get<BundleImportResult>(resultId).Id);
    }
    [Theory,InlineData("path"),InlineData("schema"),InlineData("grant"),InlineData("duplicate"),InlineData("budget"),Case("UT-ART-005,UT-ART-006,UT-SEC-003","FR-016,FR-070,FR-071,FR-072","M3","Integration")]
    public async Task MalformedBundleCannotCreateAnImportPlan(string fault)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        object value=new { text=fault=="budget"?new string('x',4096):"safe" }; var entry=Entry("source",value,fault=="grant"?"grant":"hypothesis");
        if(fault=="path") entry=entry with { Path="../outside.json" };
        var manifest=Manifest(entry); if(fault=="schema") manifest=manifest with { SchemaVersion="99.0" };
        var payloads=new List<(string,byte[])> { (entry.Path,Encoding.UTF8.GetBytes(Wire.Serialize(value))) };
        if(fault=="duplicate") payloads.Add(payloads[0]);
        var archive=await Bundle(service,manifest,payloads);
        string expected=fault switch { "path"=>"invalid_path","schema"=>"schema_unsupported","grant"=>"permission_denied","budget"=>"resource_limit",_=>"artifact_corrupt" };
        Assert.Equal(expected,(await Assert.ThrowsAsync<LabException>(()=>service.PrepareBundleImportAsync(archive.Id,fault=="budget"?1024:16777216,60))).Code);
        Assert.Empty(service.Catalog.List("bundle_import_plan").Rows); Assert.Empty(service.Catalog.List("imported_evidence_member").Rows);
        Assert.False(File.Exists(Path.Combine(workspace.Root,"outside.json")));
    }
    [Fact,Case("UT-ART-005,IT-STORE-001","FR-031,FR-072","M3","Integration")]
    public async Task CorruptSecondMemberCannotPublishFirstMemberOrSuccessfulImport()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var firstValue=new { value=1 }; var secondValue=new { value=2 }; var first=Entry("first",firstValue); var second=Entry("second",secondValue);
        var archive=await Bundle(service,Manifest(first,second),[(first.Path,Encoding.UTF8.GetBytes(Wire.Serialize(firstValue))),(second.Path,Encoding.UTF8.GetBytes(Wire.Serialize(new { value=3 })))]);
        var plan=await service.PrepareBundleImportAsync(archive.Id,16777216,60);
        var done=await ServiceTests.Wait(service,service.StartBundleImport(plan.Id,"corrupt-member").Id);
        Assert.Equal(OperationState.Failed,done.State); Assert.Equal("artifact_corrupt",done.ErrorCode);
        Assert.Empty(service.Catalog.List("bundle_import_result").Rows); Assert.Empty(service.Catalog.List("imported_evidence_member").Rows);
    }
    [Fact,Case("IT-STORE-001","FR-031,FR-072","M3","Integration")]
    public void PublicationFaultRollsBackRecordsPinsStatusAndLeaseTogether()
    {
        using var workspace=new TestWorkspace(); bool fail=false;
        using var catalog=new Catalog(workspace.Root,beforeWriteCommit:()=>{ if(fail) throw new InvalidOperationException("commit fault"); });
        var operation=catalog.Accept("test","import","hash","bundle_import","target"); catalog.AcquireLease("target",operation.Id); catalog.Transition(operation.Id,OperationState.Running);
        var artifact=new Artifact("art-"+Wire.Hash("bytes"),Wire.Hash("bytes"),5,"application/json",Wire.Version,"fixture",true);
        object result=new { id="result" }; var record=new NotebookRecord("result","bundle_import_result",1,Wire.Hash(result),Wire.Serialize(result),DateTimeOffset.UtcNow);
        fail=true; Assert.Throws<InvalidOperationException>(()=>catalog.PublishBundleImport(operation.Id,"result",[record],[artifact])); fail=false;
        Assert.Empty(catalog.List("bundle_import_result").Rows); Assert.Empty(catalog.List("artifact").Rows); Assert.Equal(OperationState.Running,catalog.GetOperation(operation.Id).State);
        Assert.Equal("lease_conflict",Assert.Throws<LabException>(()=>catalog.AcquireLease("target",operation.Id)).Code);
        catalog.PublishBundleImport(operation.Id,"result",[record],[artifact]); Assert.Equal(OperationState.Completed,catalog.GetOperation(operation.Id).State);
        Assert.True(catalog.IsPinned(artifact.Id));
    }
    [Fact,Case("UT-ART-012,IT-STORE-003","FR-071,FR-072","M3","Integration")]
    public async Task PrivacyFilteredArtifactOnlyBundlePreservesExplicitOmission()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var raw=await Seal(service,[1,2,3],"application/json"); var export=await service.PrepareExportAsync([],[raw.Id],true,16777216,60);
        var grant=service.IssueExportGrant(export.Id,60,1); var done=await ServiceTests.Wait(service,service.StartExport(export.Id,grant.Id,"omission").Id);
        var bundle=service.Catalog.Get<ExportResult>(done.ResultId!); var plan=await service.PrepareBundleImportAsync(bundle.ArtifactId,16777216,60);
        var imported=await ServiceTests.Wait(service,service.StartBundleImport(plan.Id,"metadata-only").Id); Assert.Equal(OperationState.Completed,imported.State);
        var result=await service.VerifyBundleImportAsync(imported.ResultId!); Assert.Equal(0,result.Records+result.Artifacts); Assert.Equal(1,result.OmittedPayloads);
        Assert.Empty(service.ImportedMembers(result.Id).Rows);
    }
    [Fact,Case("IT-STORE-003,IT-CAP-001","FR-020,FR-021,FR-026,FR-071,FR-072","M3","Integration")]
    public async Task ImportedRealCapture_ReprocessesAfterTargetAndSourceWorkspaceCloseWithOriginWarning()
    {
        using var sourceWorkspace=new TestWorkspace(); using var destinationWorkspace=new TestWorkspace(); string archivePath;
        await using(var source=new LabService(sourceWorkspace.Root))
        {
            using var process=await CaptureTests.StartTarget();
            try
            {
                var target=source.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
                var grant=source.IssueGrant(target.Id,Permission.Observe,60,1);
                var captured=await ServiceTests.Wait(source,source.StartCapture(target.Id,0,"baseline-context",2,grant.Id,"source-capture").Id);
                Assert.Equal(OperationState.Completed,captured.State); var capture=source.Catalog.Get<Capture>(captured.ResultId!);
                var plan=await source.PrepareExportAsync([capture.Id],[capture.ArtifactId!],false,67108864,60);
                var exportGrant=source.IssueExportGrant(plan.Id,60,1,true);
                var exported=await ServiceTests.Wait(source,source.StartExport(plan.Id,exportGrant.Id,"captured-bundle").Id); Assert.Equal(OperationState.Completed,exported.State);
                archivePath=await source.Artifacts.VerifiedPathAsync(source.Catalog.Get<Artifact>(source.Catalog.Get<ExportResult>(exported.ResultId!).ArtifactId));
            }
            finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
        }
        string importId,analysisId;
        await using(var destination=new LabService(destinationWorkspace.Root))
        {
            var archive=await destination.ImportAsync(archivePath,"application/zip"); var plan=await destination.PrepareBundleImportAsync(archive.Id,67108864,60);
            var done=await ServiceTests.Wait(destination,destination.StartBundleImport(plan.Id,"trace-import").Id); Assert.Equal(OperationState.Completed,done.State); importId=done.ResultId!;
            var member=destination.ImportedMembers(importId).Rows.Select(r=>Wire.Deserialize<ImportedEvidenceMember>(r.Json)).Single(m=>m.SourceKind=="capture");
            var response=await new LabTools(destination).Run(new StartImportedCaptureAnalysis(member.Id,"offline-parser")); Assert.True(response.Status=="ok",Wire.Serialize(response));
            var operation=response.Data!.Value.Deserialize<Operation>(Wire.Json)!; var completed=await ServiceTests.Wait(destination,operation.Id);
            Assert.True(completed.State==OperationState.Completed,Wire.Serialize(completed)); analysisId=completed.ResultId!;
            Assert.Equal(operation.Id,destination.StartImportedCaptureAnalysis(member.Id,"offline-parser").Id);
            var metric=await destination.MetricAsync(analysisId,"gc.cycle_count"); Assert.True(metric.Value>0,Wire.Serialize(metric)); Assert.Equal(Availability.Partial,metric.Status);
            Assert.Contains("imported_source_origin_unverified",metric.Quality!.Warnings);
            Assert.Equal(Availability.NotCollected,(await destination.MetricAsync(analysisId,"method.average_ns")).Status);
            Assert.Empty(destination.Catalog.List("target").Rows); Assert.Empty(destination.Catalog.List("grant").Rows);
        }
        await using var reopened=new LabService(destinationWorkspace.Root); Assert.True((await reopened.MetricAsync(analysisId,"gc.cycle_count")).Value>0);
        Assert.Equal(importId,(await reopened.VerifyBundleImportAsync(importId)).Id);
    }
    [Fact,Case("CT-MCP-012,IT-STORE-003","FR-031,FR-072,FR-074","M3","Integration")]
    public async Task LegacyExportWithoutSourceRevision_RetainsUnknownRevisionAndOpaqueSourceIdentity()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var value=new { text="retained original payload" }; var entry=Entry("../opaque-source",value,revision:null);
        var archive=await Bundle(service,Manifest(entry),[(entry.Path,Encoding.UTF8.GetBytes(Wire.Serialize(value)))]);
        var plan=await service.PrepareBundleImportAsync(archive.Id,16777216,60);
        var done=await ServiceTests.Wait(service,service.StartBundleImport(plan.Id,"legacy-metadata").Id); Assert.Equal(OperationState.Completed,done.State);
        var member=Wire.Deserialize<ImportedEvidenceMember>(Assert.Single(service.ImportedMembers(done.ResultId!).Rows).Json);
        Assert.Null(member.SourceRevision); Assert.Equal("../opaque-source",member.SourceId); Assert.Equal(entry.SourceHash,member.SourceHash);
        Assert.False(File.Exists(Path.Combine(workspace.Root,"opaque-source")));
    }
    [Fact,Case("UT-OP-011,IT-STORE-001","FR-038,FR-072","M3","Integration")]
    public void CancellationBeforePublicationCannotCommitAnImportedMemberSet()
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root);
        var operation=catalog.Accept("test","cancel","hash","bundle_import","target"); catalog.AcquireLease("target",operation.Id);
        catalog.Transition(operation.Id,OperationState.Running); catalog.Transition(operation.Id,OperationState.CancellationRequested);
        var artifact=new Artifact("art-"+Wire.Hash("bytes"),Wire.Hash("bytes"),5,"application/json",Wire.Version,"fixture",true);
        object result=new { id="result" }; var record=new NotebookRecord("result","bundle_import_result",1,Wire.Hash(result),Wire.Serialize(result),DateTimeOffset.UtcNow);
        Assert.Equal("cancelled",Assert.Throws<LabException>(()=>catalog.PublishBundleImport(operation.Id,"result",[record],[artifact])).Code);
        Assert.Empty(catalog.List("bundle_import_result").Rows); Assert.Empty(catalog.List("artifact").Rows);
        Assert.Equal(OperationState.CancellationRequested,catalog.GetOperation(operation.Id).State);
    }
}
