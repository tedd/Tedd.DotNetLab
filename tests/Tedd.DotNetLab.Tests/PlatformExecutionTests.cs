using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class PlatformExecutionTests
{
    [Theory,InlineData("cpu-sum",false),InlineData("async-yield",true),
        Case("IT-PLAT-QUIET-EXECUTION","FR-002,FR-020,FR-024,FR-031,FR-037,FR-062,FR-072","M7","Integration")]
    public async Task PublicQuietExecutionPreservesMeasurementArtifactsAndRestartIdentity(string scenarioId,bool asynchronous)
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        string executionId; var hashes=new Dictionary<string,string>();
        try
        {
            await using(var service=new LabService(workspace.Root))
            {
                var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                var handshake=await service.ScenariosAsync(identity.Id,CancellationToken.None);
                var scenario=handshake.Scenarios.Single(s=>s.Id==scenarioId);
                Assert.Equal(OperatingSystem.IsWindows()?"windows":"linux",handshake.Runtime!.OperatingSystemFamily);
                var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,2,100,1,
                    73,"quiet-validation",false,"Single-process qualification; no independent-population claim.",false,30,new()));
                var grant=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,120,4); var tools=new LabTools(service);
                var request=new StartExecution(identity.Id,"0",plan.Id,grant.Id,"platform-execution");
                var accepted=await tools.Run(request); Assert.Equal("ok",accepted.Status);
                var operation=accepted.Data!.Value.Deserialize<Operation>(Wire.Json)!;
                var complete=await ServiceTests.Wait(service,operation.Id); Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete));
                executionId=complete.ResultId!; var execution=service.Catalog.Get<ExecutionRecord>(executionId);
                Assert.Equal(4,execution.RunIds.Count); Assert.Null(execution.CaptureId); Assert.Null(execution.AnalysisId);
                var replay=await tools.Run(request); Assert.Equal("ok",replay.Status);
                Assert.Equal(operation.Id,replay.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
                Assert.Equal(4,service.Catalog.List("run").Rows.Count); Assert.Empty(service.Catalog.List("capture").Rows);
                foreach(string id in execution.RunIds)
                {
                    var record=service.Catalog.Get(id); hashes[id]=record.Hash;
                    var run=Wire.Deserialize<RunRecord>(record.Json); var measured=run.Measurement;
                    Assert.Equal(handshake.Target,run.Target); Assert.Equal(plan.Id,run.PlanId); Assert.Equal(Wire.Hash(plan),run.PlanHash);
                    Assert.Equal(handshake.Runtime.OperatingSystemFamily,run.Runtime!.OperatingSystemFamily);
                    Assert.Equal(100,measured.CompletedOperations); Assert.True(measured.Correct&&measured.Quiescent);
                    Assert.True(measured.EndTicks>measured.StartTicks); Assert.True(measured.Frequency>0);
                    if(asynchronous) Assert.Null(measured.ThreadAllocationBytes); else Assert.NotNull(measured.ThreadAllocationBytes);
                    using var binding=JsonDocument.Parse(service.Catalog.Get(id+"-measurement").Json);
                    var artifact=service.Catalog.Get<Artifact>(binding.RootElement.GetProperty("artifact_id").GetString()!);
                    var retained=Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(artifact)));
                    Assert.Equal(Wire.Hash(measured),Wire.Hash(retained));
                }
                Assert.All(service.Compare(executionId),comparison=>Assert.False(comparison.PromotionEligible));
                Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>service.IssueGrant(identity.Id,Permission.InspectLiveCode,60,1)).Code);
            }
            target.Kill(true); await target.WaitForExitAsync();
            await using var reopened=new LabService(workspace.Root);
            Assert.Equal("completed",reopened.Catalog.Get<ExecutionRecord>(executionId).State);
            foreach(var pair in hashes) Assert.Equal(pair.Value,reopened.Catalog.Get(pair.Key).Hash);
            Assert.All(reopened.Compare(executionId),comparison=>Assert.False(comparison.PromotionEligible));
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
    [Fact,Case("UT-SEC-004","FR-037,FR-062,FR-070","M7","Integration")]
    public async Task PublicExecutionCannotUseObservationAuthorityOrConsumeAnOperation()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var observe=service.IssueGrant(identity.Id,Permission.Observe,60,1);
            var reply=await new LabTools(service).Run(new StartExecution(identity.Id,"0","unused-plan",observe.Id,"denied-platform-execution"));
            Assert.Equal("error",reply.Status); Assert.Equal("permission_denied",reply.Code);
            Assert.Empty(service.Catalog.List("operation").Rows); Assert.Empty(service.Catalog.List("run").Rows);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
}
