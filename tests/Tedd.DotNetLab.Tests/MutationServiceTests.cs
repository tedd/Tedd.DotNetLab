using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class MutationServiceTests
{
    [Fact, Case("UT-TXN-016", "FR-052,FR-072", "M6", "Integration")]
    public void InterruptedMutationLease_RequiresRecoveryBeforeFurtherWork()
    {
        using var workspace=new TestWorkspace(); string operationId;
        using(var catalog=new Tedd.DotNetLab.Storage.Catalog(workspace.Root))
        {
            var operation=catalog.Accept("caller","mutation","hash","mutation","target"); operationId=operation.Id;
            catalog.AcquireLease("target",operation.Id); catalog.Transition(operation.Id,OperationState.Running);
        }
        using var recovered=new Tedd.DotNetLab.Storage.Catalog(workspace.Root); recovered.Recover();
        Assert.Equal(OperationState.Interrupted,recovered.GetOperation(operationId).State);
        var next=recovered.Accept("caller","next","hash","mutation","target");
        Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>recovered.AcquireLease("target",next.Id)).Code);
    }
    private static async Task<Operation> Finish(LabService service,Operation operation)
    {
        var end=DateTimeOffset.UtcNow.AddSeconds(30);
        while(!Lifecycle.Terminal(operation.State)&&DateTimeOffset.UtcNow<end)
        { await Task.Delay(10); operation=service.Status(operation.Id); }
        Assert.True(Lifecycle.Terminal(operation.State)); return operation;
    }
    [Theory,InlineData(false),InlineData(true)]
    [Case("IT-TXN-DURABLE", "FR-052,FR-053,FR-062,FR-072", "M6", "Integration")]
    public async Task Journal_PrecedesSubmissionAndRecordsVerifiedRestore(bool incorrect)
    {
        using var workspace=new TestWorkspace();
        using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root);
            var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            await service.ProfilerAsync(target.Id,CancellationToken.None);
            var inspectGrant=service.IssueGrant(target.Id,Permission.InspectLiveCode,90,3);
            var build=await service.ImportAsync(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"),
                "application/vnd.microsoft.portable-executable");
            var method=Assert.Single((await service.MethodsAsync(build.Id,"NativeFixture.Value",100,null)).Rows);
            var inspection=await Finish(service,await service.InspectLiveAsync(target.Id,method.Id,0,inspectGrant.Id,null,CancellationToken.None));
            Assert.Equal(OperationState.Completed,inspection.State);
            var row=service.Catalog.Get<JsonElement>(inspection.ResultId!);
            var manifest=service.Catalog.Get<Artifact>(row.GetProperty("manifest_artifact_id").GetString()!);
            var snapshot=Wire.Deserialize<Tedd.DotNetLab.Analysis.Native.NativeSnapshotReply>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(manifest)));
            string inputPath=Path.Combine(workspace.Root,"candidate.json");
            byte[] body=incorrect?[0x0e,0x1f,43,0x2a]:[0x16,0x1f,40,0x18,0x58,0x2a];
            await File.WriteAllTextAsync(inputPath,Wire.Serialize(new CilInput(Wire.Version,body,"integer expression fixture")));
            var input=await service.ImportAsync(inputPath,"application/json");
            var candidate=await service.RegisterCilAsync(input.Id,inspection.ResultId!,snapshot.OriginalBodyHash,[]);
            var mutation=service.IssueMutationGrant(target.Id,candidate.Id,90,10);
            var execution=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,90,10);
            var tools=new Tedd.DotNetLab.Mcp.LabTools(service);
            var denied=await tools.Patch(new Tedd.DotNetLab.Mcp.ApplyCil(target.Id,"0",candidate.Id,inspectGrant.Id,execution.Id,"native-value","wrong-permission"));
            Assert.Equal("permission_denied",denied.Code);
            var preview=await tools.Patch(new Tedd.DotNetLab.Mcp.PreflightCil(target.Id,"0",candidate.Id,mutation.Id));
            Assert.Equal("ok",preview.Status);
            var operation=await Finish(service,await service.ApplyCilAsync(target.Id,0,candidate.Id,mutation.Id,execution.Id,"native-value","apply"));
            var outcome=service.Catalog.Get<MutationOutcome>(operation.ResultId!);
            Assert.False(outcome.PromotionEligible); Assert.False(outcome.Tainted);
            Assert.Equal(!incorrect,outcome.Measurable); Assert.Equal(incorrect,outcome.Restored);
            Assert.Equal(incorrect?OperationState.Failed:OperationState.Completed,operation.State);
            Assert.Equal(operation.Id,(await service.ApplyCilAsync(target.Id,0,candidate.Id,mutation.Id,execution.Id,"native-value","apply")).Id);
            Assert.Equal("idempotency_conflict",(await Assert.ThrowsAsync<LabException>(()=>service.ApplyCilAsync(target.Id,0,candidate.Id,
                mutation.Id,execution.Id,"native-value","apply",101))).Code);
            var root=service.Catalog.List("mutation").Rows.Select(r=>Wire.Deserialize<MutationRoot>(r.Json)).Single(r=>r.OperationId==operation.Id);
            var events=service.Catalog.List("mutation_event").Rows.Select(r=>Wire.Deserialize<MutationEvent>(r.Json))
                .Where(e=>e.TransactionId==root.Id).OrderBy(e=>e.Sequence).ToArray();
            Assert.Equal("prepared",events[0].Phase);
            Assert.True(Array.FindIndex(events,e=>e.Phase=="preflight_passed")<Array.FindIndex(events,e=>e.Phase=="request_submitted"));
            for(int i=1;i<events.Length;i++) Assert.Equal(Wire.Hash(events[i-1]),events[i].PreviousHash);
            if(!incorrect)
            {
                var restored=await Finish(service,await service.RestoreCilAsync(root.Id,outcome.Epoch,mutation.Id,execution.Id,"restore"));
                Assert.Equal(OperationState.Completed,restored.State);
                var restoredOutcome=service.Catalog.Get<MutationOutcome>(restored.ResultId!);
                Assert.True(restoredOutcome.Restored); Assert.False(restoredOutcome.Measurable); Assert.False(restoredOutcome.Tainted);
                Assert.True(restoredOutcome.Epoch>outcome.Epoch);
            }
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
