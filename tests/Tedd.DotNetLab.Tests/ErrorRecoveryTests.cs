using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Collector.EventPipe;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ErrorRecoveryTests
{
    [Fact,Case("UT-ERR-001","FR-061,FR-064","M3","Contract")]
    public async Task InvalidRequestsReturnGuidanceWithoutAcceptingAnOperation()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var tools=new LabTools(service);
        foreach(string request in new[] {"{","{\"action\":\"unknown\"}","{\"action\":\"status\",\"operation_id\":\"missing\",\"shell\":\"anything\"}"})
        {
            var response=await tools.InvokeAsync("lab.run",request); Assert.Equal("invalid_request",response.Code);
            Assert.Equal("error",response.Status); Assert.Null(response.Data); Assert.Contains("schema",response.Recovery);
            Assert.Equal(response,Wire.Deserialize<LabResponse>(Wire.Serialize(response)));
        }
        Assert.Contains("schema",(await tools.InvokeAsync("not-a-tool","{}")).Recovery);
        Assert.Empty(service.Catalog.List("operation").Rows);
    }

    [Fact,Case("CT-MCP-006,UT-ERR-002","FR-062,FR-063,FR-064","M3","Contract")]
    public async Task MissingRecordsAndWrongViewCursorsHaveDistinctRecoveries()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root); var tools=new LabTools(service);
        var missing=await tools.Inspect(new InspectRecord("absent")); Assert.Equal("record_not_found",missing.Code);
        Assert.Contains("workspace",missing.Recovery); Assert.Null(missing.Data);
        var hypothesis=service.Hypothesis("mechanism","prediction",[]);
        service.Hypothesis("second","prediction",[]);
        var other=service.Candidate("candidate","build",[]);
        foreach(string anchor in new[] {"comparison:999",other.Id})
        {
            var cursor=await tools.Query(new QueryHistory("hypothesis",1,anchor));
            Assert.Equal("invalid_cursor",cursor.Code); Assert.Contains("requested view/result",cursor.Recovery);
            Assert.NotEqual(missing.Recovery,cursor.Recovery);
        }
        var page=await tools.Query(new QueryHistory("hypothesis",1)); Assert.Equal("ok",page.Status);
        string valid=page.Data!.Value.GetProperty("next_cursor").GetString()!;
        var next=await tools.Query(new QueryHistory("hypothesis",1,valid)); Assert.Equal("ok",next.Status);
        Assert.Equal(2,service.Catalog.List("hypothesis").Rows.Count);
        Assert.Equal(1,service.Catalog.Get(hypothesis.Id).Revision);
    }

    [Fact,Case("UT-ERR-003","FR-001,FR-040,FR-064","M3","Integration")]
    public async Task MissingNativeEndpointDoesNotSuggestAttachMutationOrMethodInvocation()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var response=await new LabTools(service).Discover(new DiscoverProfiler(identity.Id));
            Assert.Equal("capability_unsupported",response.Code); Assert.Null(response.Data);
            Assert.Contains("explicitly qualified target and method",response.Recovery); Assert.Contains("Do not invoke a method",response.Recovery);
            Assert.Equal(CapabilityState.Unsupported,service.Target(identity.Id).Capabilities["native_inspection"].State);
            Assert.Equal(CapabilityState.Unsupported,service.Target(identity.Id).Capabilities["rejit"].State);
            Assert.Empty(service.Catalog.List("operation").Rows);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }

    private sealed class FailedInventory(string code,string message,string? recovery) : ITargetRegistry
    {
        public IReadOnlyList<TargetInfo> Discover()=>throw new LabException(code,message,recovery);
        public void Verify(TargetIdentity identity,long expectedEpoch)=>throw new InvalidOperationException();
    }
    [Theory,InlineData("profiler_timeout"),InlineData("profiler_disconnected"),InlineData("invalid_profiler_response"),InlineData("unrecognized_future_error")]
    [Case("UT-ERR-004","FR-052,FR-064","M3","Contract")]
    public async Task UncertainOrUnknownFailuresNeverRecommendAutomaticMutationRetry(string code)
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root,targetRegistry:new FailedInventory(code,"submission outcome unknown",null));
        var response=await new LabTools(service).Discover(new DiscoverTargets()); Assert.Equal(code,response.Code);
        Assert.Contains(code=="unrecognized_future_error"?"do not automatically repeat":"do not resend a mutation",response.Recovery);
        Assert.Contains("operation",response.Recovery); Assert.Empty(service.Catalog.List("operation").Rows);
    }

    [Fact,Case("UT-ERR-005","FR-064,FR-074","M3","Contract")]
    public async Task SpecificGuidanceIsPreservedAndExternalErrorTextIsBounded()
    {
        using var workspace=new TestWorkspace(); const string specific="Use the selected immutable artifact slice.";
        await using(var service=new LabService(workspace.Root,targetRegistry:new FailedInventory("resource_limit","too large",specific)))
        {
            var response=await new LabTools(service).Discover(new DiscoverTargets()); Assert.Equal(specific,response.Recovery);
        }
        await using var reopened=new LabService(workspace.Root,targetRegistry:new FailedInventory("external_failure",new string('m',10000),new string('r',10000)));
        var bounded=await new LabTools(reopened).Discover(new DiscoverTargets()); Assert.Equal("external_failure",bounded.Code);
        Assert.Equal(4096,bounded.Message!.Length); Assert.Equal(RecoveryHints.MaximumLength,bounded.Recovery!.Length);
        using var json=System.Text.Json.JsonDocument.Parse(Wire.Serialize(bounded)); Assert.Equal(System.Text.Json.JsonValueKind.Null,json.RootElement.GetProperty("data").ValueKind);
    }
}
