using System.ComponentModel;
using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Tedd.DotNetLab.Mcp;

[McpServerToolType]
public sealed class McpTools(LabTools tools)
{
    [McpServerTool(Name="lab.discover",ReadOnly=true,Destructive=false,UseStructuredContent=true,OutputSchemaType=typeof(LabResponse))]
    [Description("Discover bounded local targets, capabilities, profiles, and registered scenarios.")]
    public Task<CallToolResult> Discover(DiscoverRequest request)=>McpResponse.Result(tools.Discover(request));
    [McpServerTool(Name="lab.inspect",ReadOnly=true,Destructive=false,UseStructuredContent=true,OutputSchemaType=typeof(LabResponse))]
    [Description("Read retained records and artifact manifests. Unqualified native inspection is explicitly unsupported.")]
    public Task<CallToolResult> Inspect(InspectRequest request)=>McpResponse.Result(tools.Inspect(request));
    [McpServerTool(Name="lab.experiment",Destructive=false,UseStructuredContent=true,OutputSchemaType=typeof(LabResponse))]
    [Description("Maintain hypotheses, baselines/aliases, frozen plans including individual-observation quantile designs, verified observation datasets, registered builds, candidate metadata, work items, and decisions.")]
    public Task<CallToolResult> Experiment(ExperimentRequest request)=>McpResponse.Result(tools.Experiment(request));
    [McpServerTool(Name="lab.run",Destructive=true,UseStructuredContent=true,OutputSchemaType=typeof(LabResponse))]
    [Description("Start granted capture, scenario, individual-operation timing, confirmation, export, or deletion jobs; compare retained quantiles, analyze sampled stacks, recover artifacts, or maintain the catalog; poll, cancel, or stop by operation ID.")]
    public Task<CallToolResult> Run(RunRequest request)=>McpResponse.Result(tools.Run(request));
    [McpServerTool(Name="lab.query",ReadOnly=true,Destructive=false,UseStructuredContent=true,OutputSchemaType=typeof(LabResponse))]
    [Description("Query retained metrics, individual-latency slices, sampled hotspots/method evidence, run windows, GC/suspensions, counter series and time windows, history, verified exports, availability, and bounded artifact slices.")]
    public Task<CallToolResult> Query(QueryRequest request)=>McpResponse.Result(tools.Query(request));
    [McpServerTool(Name="lab.compare",Destructive=false,Idempotent=true,UseStructuredContent=true,OutputSchemaType=typeof(LabResponse))]
    [Description("Compute frozen comparisons, bounded objective vectors, complete four-arm factorial effects/interactions, or normal-build promotion decisions.")]
    public Task<CallToolResult> Compare(CompareRequest request)=>McpResponse.Result(tools.Compare(request));
}

[McpServerToolType]
public sealed class McpPatchTools(LabTools tools)
{
    [McpServerTool(Name="lab.patch",Destructive=true,UseStructuredContent=true,OutputSchemaType=typeof(LabResponse))]
    [Description("Prepare original-body compilation controls; run granted controls, preflight, apply, restore, or explicitly recover a CIL transaction with native reconciliation and scoped scenario oracles.")]
    public Task<CallToolResult> Patch(PatchRequest request)=>McpResponse.Result(tools.Patch(request));
}

internal static class McpResponse
{
    public static async Task<CallToolResult> Result(Task<LabResponse> pending)
    {
        var response=await pending;
        return new CallToolResult { IsError=response.Status=="error",Content=[new TextContentBlock { Text=Wire.Serialize(response) }],
            StructuredContent=JsonSerializer.SerializeToElement(response,Wire.Json) };
    }
}
