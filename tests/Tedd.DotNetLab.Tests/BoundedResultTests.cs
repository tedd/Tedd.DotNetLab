using System.Text;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Collector.EventPipe;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class BoundedResultTests
{
    private sealed class Failure : ITargetRegistry
    {
        public IReadOnlyList<TargetInfo> Discover()=>throw new LabException("permission_denied",new string('λ',12000),"specific-guidance: "+new string('"',12000));
        public void Verify(TargetIdentity target,long epoch) { }
    }
    [Fact,Case("CT-MCP-ERROR-BYTE-BUDGET","FR-060,FR-063,FR-064","M3","Contract")]
    public async Task LongEscapedError_StaysWithinMinimumInlineBudgetAndKeepsMachineCode()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root,new ResourceLimits(InlineBytes:1024),new Failure());
        var result=await new LabTools(service).Discover(new DiscoverTargets());
        Assert.Equal("error",result.Status); Assert.Equal("permission_denied",result.Code); Assert.StartsWith("specific-guidance:",result.Recovery);
        Assert.InRange(Encoding.UTF8.GetByteCount(Wire.Serialize(result)),1,1024); Assert.Empty(service.Catalog.List("artifact").Rows);
    }
    private static async Task<byte[]> Read(LabTools tools,JsonElement reference)
    {
        Assert.Equal("artifact",reference.GetProperty("representation").GetString());
        Assert.True(reference.GetProperty("truncated").GetBoolean());
        string id=reference.GetProperty("artifact_id").GetString()!;
        int count=reference.GetProperty("read").GetProperty("request").GetProperty("count").GetInt32();
        using var bytes=new MemoryStream(); long offset=0;
        while(true)
        {
            var slice=await tools.Query(new QueryArtifact(id,offset.ToString(System.Globalization.CultureInfo.InvariantCulture),count));
            Assert.Equal("ok",slice.Status); var value=slice.Data!.Value;
            byte[] chunk=Convert.FromBase64String(value.GetProperty("data").GetString()!); await bytes.WriteAsync(chunk);
            if(!value.GetProperty("truncated").GetBoolean()) break;
            long next=Wire.Deserialize<long>(value.GetProperty("next_offset").GetRawText()); Assert.True(next>offset); offset=next;
        }
        Assert.Equal(Wire.Deserialize<long>(reference.GetProperty("artifact_length").GetRawText()),bytes.Length);
        Assert.Equal(reference.GetProperty("artifact_hash").GetString(),Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.ToArray())));
        return bytes.ToArray();
    }
    [Fact,Case("CT-MCP-005","FR-063,FR-064,FR-074","M3","Contract")]
    public async Task OversizedHistoryFirstRow_PreservesJsonAndCursorThroughArtifact()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var tools=new LabTools(service);
        string content=new('x',80000); service.Catalog.Put("a","hypothesis",new { content }); service.Catalog.Put("b","hypothesis",new { content="second" });
        Assert.Equal("resource_limit",Assert.Throws<LabException>(()=>service.Catalog.List("hypothesis")).Code);
        var result=await tools.Query(new QueryHistory("hypothesis",100)); Assert.Equal("ok",result.Status);
        Assert.True(Encoding.UTF8.GetByteCount(Wire.Serialize(result))<=service.Limits.InlineBytes);
        var retained=Wire.Deserialize<LabResponse>(Encoding.UTF8.GetString(await Read(tools,result.Data!.Value)));
        var page=retained.Data!.Value; Assert.True(page.GetProperty("truncated").GetBoolean()); Assert.Equal("a",page.GetProperty("next_cursor").GetString());
        using var body=JsonDocument.Parse(page.GetProperty("rows")[0].GetProperty("json").GetString()!);
        Assert.Equal(content,body.RootElement.GetProperty("content").GetString());
        var next=await tools.Query(new QueryHistory("hypothesis",100,"a"));
        Assert.Equal("b",next.Data!.Value.GetProperty("rows")[0].GetProperty("id").GetString()); Assert.False(next.Data.Value.GetProperty("truncated").GetBoolean());
    }
    [Fact,Case("CT-MCP-010","FR-062,FR-063","M3","Contract")]
    public async Task MinimalInlineBudget_UsesToolSlicesWithoutResourceClient()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root,new ResourceLimits(InlineBytes:1024));
        var tools=new LabTools(service); service.Catalog.Put("record","hypothesis",new { text=new string('x',4096) });
        var result=await tools.Inspect(new InspectRecord("record")); Assert.Equal("ok",result.Status); Assert.InRange(Encoding.UTF8.GetByteCount(Wire.Serialize(result)),1,1024);
        var original=Wire.Deserialize<LabResponse>(Encoding.UTF8.GetString(await Read(tools,result.Data!.Value))); Assert.Equal("record",original.Data!.Value.GetProperty("id").GetString());
        Assert.Empty(service.Catalog.List("grant").Rows); Assert.Empty(service.Catalog.List("mutation").Rows);
    }
    [Fact,Case("CT-MCP-RECORD-RESOURCE","FR-063,FR-064","M3","Contract")]
    public async Task LargeRecordResource_RetainsRawRecordAndBoundedReadReference()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var tools=new LabTools(service);
        string text=new('q',80000); service.Catalog.Put("record","hypothesis",new { text });
        var reference=Wire.Deserialize<JsonElement>(await tools.ReadRecord("record")); Assert.Equal("notebook_record",reference.GetProperty("result_kind").GetString());
        using var original=JsonDocument.Parse(await Read(tools,reference)); Assert.Equal("record",original.RootElement.GetProperty("id").GetString());
        using var body=JsonDocument.Parse(original.RootElement.GetProperty("json").GetString()!); Assert.Equal(text,body.RootElement.GetProperty("text").GetString());
    }
    [Fact,Case("UT-MCP-RESPONSE-RETENTION","FR-063,FR-071","M3","Integration")]
    public async Task RepeatedLargeReads_DeduplicateContentAndRetainEachConsumerTemporarily()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var tools=new LabTools(service);
        service.Catalog.Put("record","hypothesis",new { text=new string('x',80000) });
        var results=await Task.WhenAll(Enumerable.Range(0,4).Select(_=>tools.Inspect(new InspectRecord("record")))); Assert.All(results,item=>Assert.Equal("ok",item.Status));
        string id=results[0].Data!.Value.GetProperty("artifact_id").GetString()!; Assert.All(results,item=>Assert.Equal(id,item.Data!.Value.GetProperty("artifact_id").GetString()));
        Assert.Single(service.Catalog.List("artifact").Rows); Assert.Equal(4,service.Catalog.Retention(id).RetainedOwners);
        Assert.All(service.Catalog.References(id).Rows,reference=>Assert.InRange(reference.RetainUntil!.Value,DateTimeOffset.UtcNow.AddMinutes(29),DateTimeOffset.UtcNow.AddMinutes(31)));
    }
    [Fact,Case("UT-MCP-RESPONSE-CORRUPTION","FR-064,FR-071","M3","Contract")]
    public async Task AlteredResponseArtifact_CannotReturnSuccessfulSlice()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var tools=new LabTools(service);
        service.Catalog.Put("record","hypothesis",new { text=new string('x',80000) });
        var response=await tools.Inspect(new InspectRecord("record")); string id=response.Data!.Value.GetProperty("artifact_id").GetString()!;
        var artifact=service.Catalog.Get<Artifact>(id); string path=await service.Artifacts.VerifiedPathAsync(artifact); await File.WriteAllTextAsync(path,"corrupted");
        var result=await tools.Query(new QueryArtifact(id,"0",128)); Assert.Equal("error",result.Status); Assert.Equal("artifact_corrupt",result.Code);
    }
    [Fact,Case("UT-MCP-RESPONSE-DISK-BUDGET","FR-016,FR-063,FR-071","M3","Contract")]
    public async Task UnavailableDiskReserve_RejectsFallbackBeforeWritingTemporaryContent()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root,new ResourceLimits(DiskReserveBytes:long.MaxValue));
        service.Catalog.Put("record","hypothesis",new { text=new string('x',80000) }); var tools=new LabTools(service);
        var result=await tools.Inspect(new InspectRecord("record")); Assert.Equal("resource_limit",result.Code); Assert.Empty(service.Catalog.List("artifact").Rows);
        Assert.Empty(Directory.EnumerateFiles(Path.Combine(workspace.Root,"incoming"),"*.partial",SearchOption.AllDirectories));
    }
}
