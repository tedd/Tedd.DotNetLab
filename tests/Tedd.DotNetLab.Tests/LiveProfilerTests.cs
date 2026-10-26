using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class LiveProfilerTests
{
    [Fact, Case("IT-NAT-MCP", "FR-040,FR-043,FR-070", "M4", "Integration")]
    public async Task LiveInspection_McpPersistsBoundedVersionedEvidence()
    {
        using var workspace=new TestWorkspace();
        using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new Tedd.DotNetLab.Application.LabService(workspace.Root);
            var published=Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient.GetPublishedProcesses().ToArray();
            var registry=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover();
            var discovered=service.Discover();
            Assert.True(discovered.Any(t=>t.Identity.ProcessId==process.Id),
                $"Target {process.Id}, exited={process.HasExited}; raw={published.Length}/{published.Contains(process.Id)}, registry={registry.Count}/{registry.Any(t=>t.Identity.ProcessId==process.Id)}, service={discovered.Count}.");
            var target=discovered.Single(t=>t.Identity.ProcessId==process.Id).Identity;
            await service.ProfilerAsync(target.Id,CancellationToken.None);
            var grant=service.IssueGrant(target.Id,Permission.InspectLiveCode,60,3);
            var build=await service.ImportAsync(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"),
                "application/vnd.microsoft.portable-executable");
            var method=Assert.Single((await service.MethodsAsync(build.Id,"NativeFixture.Value",100,null)).Rows);
            var response=await new Tedd.DotNetLab.Mcp.LabTools(service).Inspect(new Tedd.DotNetLab.Mcp.InspectNative(target.Id,method.Id,"0",grant.Id));
            Assert.Equal("ok",response.Status);
            var operation=response.Data!.Value.Deserialize<Operation>(Wire.Json)!;
            var timeout=DateTimeOffset.UtcNow.AddSeconds(15);
            while(!Tedd.DotNetLab.Domain.Lifecycle.Terminal(operation.State)&&DateTimeOffset.UtcNow<timeout)
            { await Task.Delay(10); operation=service.Status(operation.Id); }
            Assert.Equal(OperationState.Completed,operation.State);
            var inspection=service.Catalog.Get<JsonElement>(operation.ResultId!);
            Assert.Equal("verified_live_acquisition",inspection.GetProperty("qualification").GetString());
            Assert.False(inspection.GetProperty("proves_earlier_run_code").GetBoolean());
            foreach(var id in inspection.GetProperty("artifact_ids").EnumerateArray())
            {
                var artifact=service.Catalog.Get<Artifact>(id.GetString()!);
                await service.Artifacts.VerifyAsync(artifact.Hash,artifact.Length);
                Assert.True(service.Catalog.IsPinned(artifact.Id));
                Assert.NotNull(await service.DecodeAsync(artifact.Id));
            }
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Fact, Case("IT-NAT-QUIET", "FR-043,FR-074", "M4", "Integration")]
    public async Task LiveSnapshot_CannotOverlapRegisteredExecution()
    {
        using var workspace=new TestWorkspace();
        using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var harness=new Tedd.DotNetLab.Harness.HarnessClient();
            var handshake=await harness.HandshakeAsync(target,CancellationToken.None);
            var scenario=handshake.Scenarios.First(s=>!s.IsAsync);
            var run=new Tedd.DotNetLab.Contracts.RunRequest("exec","run","block",scenario.Id,"baseline",1000000,0,true,false,0,handshake.Target,
                new("execute",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow.AddMinutes(1),1),
                DateTimeOffset.UtcNow.AddSeconds(30));
            var running=harness.ExecuteAsync(run,CancellationToken.None);
            var client=new ProfilerClient();
            var deadline=DateTimeOffset.UtcNow.AddSeconds(10);
            while(!(await client.HandshakeAsync(target,CancellationToken.None)).ExecutionActive&&DateTimeOffset.UtcNow<deadline) await Task.Delay(10);
            Assert.False(running.IsCompleted);
            var denied=await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,new {
                schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,
                start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L },CancellationToken.None));
            Assert.Equal("lease_conflict",denied.Code);
            Assert.True((await running).Correct);
            Assert.False((await client.HandshakeAsync(target,CancellationToken.None)).ExecutionActive);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
