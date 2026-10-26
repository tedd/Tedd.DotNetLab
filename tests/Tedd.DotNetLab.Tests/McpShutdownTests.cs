using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class McpShutdownTests
{
    private static Process Start(string root,bool nativeExtension=false)
    {
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,
            RedirectStandardError=true,CreateNoWindow=true };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll"));
        start.ArgumentList.Add("serve"); start.ArgumentList.Add(root);
        if(nativeExtension) start.ArgumentList.Add("--enable-native-extension");
        return Process.Start(TestWorkspace.PinRuntime(start))!;
    }
    private static async Task<JsonElement> Request(Process process,int id,string method,object parameters)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc="2.0",id,method,@params=parameters }));
        await process.StandardInput.FlushAsync();
        while(true)
        {
            string? line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(line); Assert.True(line.Length<=1048576);
            using var message=JsonDocument.Parse(line);
            if(message.RootElement.TryGetProperty("id",out var found)&&found.ValueKind==JsonValueKind.Number&&found.GetInt32()==id)
            {
                Assert.False(message.RootElement.TryGetProperty("error",out _),line);
                return message.RootElement.GetProperty("result").Clone();
            }
        }
    }
    private static async Task Initialize(Process process)
    {
        await Request(process,1,"initialize",new { protocolVersion="2025-06-18",capabilities=new { },clientInfo=new { name="shutdown-contract",version="1" } });
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}"); await process.StandardInput.FlushAsync();
    }
    private static async Task<JsonElement> Tool(Process process,int id,string name,object request)
    {
        var result=await Request(process,id,"tools/call",new { name,arguments=new { request } });
        string text=result.GetProperty("content").EnumerateArray().Single(c=>c.GetProperty("type").GetString()=="text").GetProperty("text").GetString()!;
        var response=Wire.Deserialize<Tedd.DotNetLab.Mcp.LabResponse>(text); Assert.Equal("ok",response.Status); return response.Data!.Value;
    }
    [Theory,InlineData(false,false),InlineData(true,false),InlineData(true,true)]
    [Case("CT-MCP-SHUTDOWN-001","FR-038,FR-060,FR-072","M3","Contract")]
    public async Task IdleAndInitializedEofExitWithoutForcedTermination(bool initialized,bool nativeExtension)
    {
        using var workspace=new TestWorkspace(); using var process=Start(workspace.Root,nativeExtension); var errors=process.StandardError.ReadToEndAsync();
        try
        {
            if(initialized)
            {
                await Initialize(process); var tools=await Request(process,2,"tools/list",new { }); Assert.Equal(nativeExtension?7:6,tools.GetProperty("tools").GetArrayLength());
            }
            process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(process.ExitCode==0,await errors);
            await using var reopened=new LabService(workspace.Root); Assert.NotNull(reopened.Workspace.Workspace.Id);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Fact,Case("UT-OP-012,CT-MCP-SHUTDOWN-002","FR-011,FR-013,FR-038,FR-072","M3","Integration")]
    public async Task EofDuringCaptureDrainsOwnedSessionAndRetainsTruthfulTerminalEvidence()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(); string targetId,grantId;
        try
        {
            await using(var setup=new LabService(workspace.Root))
            {
                var identity=setup.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity; targetId=identity.Id;
                grantId=setup.IssueGrant(targetId,Permission.Observe,60,1).Id;
            }
            using var server=Start(workspace.Root); var errors=server.StandardError.ReadToEndAsync();
            try
            {
                await Initialize(server);
                var accepted=await Tool(server,2,"lab.run",new { action="start_capture",target_id=targetId,expected_target_epoch="0",
                    profile="baseline-context",duration_seconds=30,grant_id=grantId,idempotency_key="eof-capture" });
                string operationId=accepted.GetProperty("id").GetString()!;
                using(var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    while(true)
                    {
                        var history=await Tool(server,3,"lab.query",new { view="history",kind="capture" });
                        if(history.GetProperty("rows").EnumerateArray().Any(row=>Wire.Deserialize<Capture>(row.GetProperty("json").GetString()!).State==CaptureState.Collecting)) break;
                        await Task.Delay(20,timeout.Token);
                    }
                }
                server.StandardInput.Close(); await server.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.True(server.ExitCode==0,await errors);
                await using var reopened=new LabService(workspace.Root); var operation=reopened.Status(operationId);
                Assert.True(Tedd.DotNetLab.Domain.Lifecycle.Terminal(operation.State)); Assert.NotEqual(OperationState.Interrupted,operation.State);
                var capture=reopened.Catalog.List("capture").Rows.Select(row=>Wire.Deserialize<Capture>(row.Json)).Single();
                Assert.Contains(capture.State,new[] { CaptureState.Incomplete,CaptureState.Failed }); Assert.NotNull(capture.Quality);
                Assert.NotEqual("complete",capture.Quality.Coverage);
                if(capture.ArtifactId is not null) await reopened.Artifacts.VerifyAsync(reopened.Catalog.Get<Artifact>(capture.ArtifactId).Hash,reopened.Catalog.Get<Artifact>(capture.ArtifactId).Length);
                Assert.False(target.HasExited);
            }
            finally { if(!server.HasExited) { server.Kill(true); await server.WaitForExitAsync(); } }
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
}
