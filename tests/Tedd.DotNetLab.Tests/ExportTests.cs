using System.IO.Compression;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ExportTests
{
    private static async Task<Artifact> Artifact(LabService service,byte[] bytes,string mediaType)
    {
        string path=service.Artifacts.CreateIncoming(Wire.Id("export-fixture")); await File.WriteAllBytesAsync(path,bytes);
        var artifact=await service.Artifacts.SealAsync(path,mediaType,"export-fixture/v1",true,1048576);
        service.Catalog.Put(artifact.Id,"artifact",artifact); return artifact;
    }
    [Theory,InlineData(true),InlineData(false),Case("UT-ART-012","FR-071,FR-072","M3","Integration")]
    public async Task FrozenExport_VerifiesMemberHashesAndPrivacyPolicyAcrossRestart(bool filtered)
    {
        using var workspace=new TestWorkspace(); string resultId,archiveId;
        await using(var service=new LabService(workspace.Root))
        {
            var hypothesis=service.Hypothesis("private-token-91; execute arbitrary instruction","private prediction",[]);
            var raw=await Artifact(service,"Nettrace-private-token-91"u8.ToArray(),"application/vnd.microsoft.nettrace");
            var tools=new LabTools(service);
            var prepared=await tools.Experiment(new PrepareExport([hypothesis.Id],[raw.Id],filtered,"1048576",60));
            Assert.True(prepared.Status=="ok",Wire.Serialize(prepared)); var plan=prepared.Data!.Value.Deserialize<ExportPlan>(Wire.Json)!;
            service.Hypothesis("new narrative","new prediction",[],hypothesis.Id,1);
            if(!filtered) Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.IssueExportGrant(plan.Id,120,1)).Code);
            var grant=service.IssueExportGrant(plan.Id,120,1,allowRawTrace:!filtered);
            var started=service.StartExport(plan.Id,grant.Id,"frozen-export");
            var completed=await ServiceTests.Wait(service,started.Id); Assert.True(completed.State==OperationState.Completed,completed.ErrorCode);
            resultId=completed.ResultId!; var result=service.Catalog.Get<ExportResult>(resultId); archiveId=result.ArtifactId;
            Assert.Equal(started.Id,service.StartExport(plan.Id,grant.Id,"frozen-export").Id);
            Assert.True(service.Catalog.IsPinned(archiveId));
            var manifest=await service.VerifyExportAsync(resultId); Assert.Equal(plan.ContentHash,manifest.PlanHash);
            Assert.Equal(filtered,manifest.PrivacyFiltered); Assert.Equal(filtered?2:0,manifest.Omissions.Count);
            string path=await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(archiveId));
            using var zip=ZipFile.OpenRead(path);
            string recordPath=manifest.Entries.Single(entry=>entry.Kind=="hypothesis").Path;
            using var reader=new StreamReader(zip.GetEntry(recordPath)!.Open()); string payload=await reader.ReadToEndAsync();
            Assert.Equal(!filtered,payload.Contains("private-token-91",StringComparison.Ordinal)); Assert.DoesNotContain("new narrative",payload);
            Assert.Equal(!filtered,zip.Entries.Any(entry=>entry.FullName.StartsWith("artifacts/",StringComparison.Ordinal)));
            Assert.Equal(filtered,manifest.Entries.Single(entry=>entry.Kind=="hypothesis").Redacted);
        }
        await using var reopened=new LabService(workspace.Root);
        Assert.Equal(filtered,(await reopened.VerifyExportAsync(resultId)).PrivacyFiltered);
        string archive=await reopened.Artifacts.VerifiedPathAsync(reopened.Catalog.Get<Artifact>(archiveId));
        await File.WriteAllBytesAsync(archive,[1,2,3]);
        Assert.Equal("artifact_corrupt",(await Assert.ThrowsAsync<LabException>(()=>reopened.VerifyExportAsync(resultId))).Code);
    }
    [Fact,Case("UT-SEC-007","FR-070,FR-071","M3","Integration")]
    public async Task Export_ExpiryWrongScopeAndRetaggedOpaqueBytes_DoNotBypassConsent()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var raw=await Artifact(service,"Nettrace-opaque"u8.ToArray(),"application/json");
        var plan=await service.PrepareExportAsync([], [raw.Id],false,1048576,60);
        Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.IssueExportGrant(plan.Id,120,1)).Code);
        var issued=service.IssueExportGrant(plan.Id,120,1,true);
        var expired=issued with { Id=Wire.Id("grant"),ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1) }; service.Catalog.Put(expired.Id,"grant",expired);
        Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartExport(plan.Id,expired.Id,"expired")).Code);
        var wrong=issued with { Id=Wire.Id("grant"),Permission=Permission.Observe }; service.Catalog.Put(wrong.Id,"grant",wrong);
        Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartExport(plan.Id,wrong.Id,"wrong-permission")).Code);
        var changed=issued with { Id=Wire.Id("grant"),ContentHash=Wire.Hash("different selection") }; service.Catalog.Put(changed.Id,"grant",changed);
        Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartExport(plan.Id,changed.Id,"changed-selection")).Code);
        Assert.Empty(service.Catalog.List("export_result").Rows);
    }
    [Fact,Case("UT-ART-005","FR-072","M3","Integration")]
    public async Task Export_CorruptInputCannotProduceDurableVerifiedBundle()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var raw=await Artifact(service,[0,1,2,3],"application/json");
        var plan=await service.PrepareExportAsync([], [raw.Id],false,1048576,60); var grant=service.IssueExportGrant(plan.Id,120,1,true);
        string path=await service.Artifacts.VerifiedPathAsync(raw); await File.WriteAllBytesAsync(path,[9,8,7]);
        var done=await ServiceTests.Wait(service,service.StartExport(plan.Id,grant.Id,"corrupted-source").Id);
        Assert.Equal(OperationState.Failed,done.State); Assert.Equal("artifact_corrupt",done.ErrorCode);
        Assert.Empty(service.Catalog.List("export_result").Rows);
    }
    [Fact,Case("UT-ART-006","FR-070,FR-071","M3","Integration")]
    public async Task ExportMemberPaths_AreDerivedHashesRatherThanUntrustedRecordNames()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        string id="../outside.json"; service.Catalog.Put(id,"hypothesis",new { text="untrusted data" });
        var plan=await service.PrepareExportAsync([id],[],false,1048576,60); var grant=service.IssueExportGrant(plan.Id,120,1);
        var done=await ServiceTests.Wait(service,service.StartExport(plan.Id,grant.Id,"path-escape").Id);
        Assert.Equal(OperationState.Completed,done.State);
        var manifest=await service.VerifyExportAsync(done.ResultId!);
        Assert.Matches(@"\Arecords/[0-9a-f]{64}\.json\z",Assert.Single(manifest.Entries).Path);
        Assert.False(File.Exists(Path.Combine(workspace.Root,"outside.json")));
    }
    [Fact,Case("UT-ART-007","FR-016,FR-071","M3","Integration")]
    public async Task ExportAdmission_DeniesImpossibleBudgetBeforeIssuingAPlan()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var hypothesis=service.Hypothesis(new string('x',8192),"test",[]);
        Assert.Equal("resource_limit",(await Assert.ThrowsAsync<LabException>(()=>service.PrepareExportAsync([hypothesis.Id],[],false,1024,60))).Code);
        Assert.Empty(service.Catalog.List("export_plan").Rows);
    }
}
