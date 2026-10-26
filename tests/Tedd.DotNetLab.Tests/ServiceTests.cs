using Tedd.DotNetLab.Application;
using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ServiceTests
{
    public static async Task<Operation> Wait(LabService service,string id)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        while(true)
        {
            var operation=service.Status(id);
            if(Tedd.DotNetLab.Domain.Lifecycle.Terminal(operation.State)) return operation;
            await Task.Delay(20,timeout.Token);
        }
    }
    [Fact, Case("IT-E2E-001", "FR-030,FR-038,FR-062", "M3", "Integration")]
    public async Task Execution_NotebookAndComparison_SurviveControllerRestart()
    {
        using var workspace=new TestWorkspace();
        using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            string comparisonId,executionId,hypothesisId;
            await using(var service=new LabService(workspace.Root))
            {
                var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                var handshake=await service.ScenariosAsync(identity.Id,CancellationToken.None);
                var scenario=handshake.Scenarios.Single(s=>s.Id=="cpu-sum");
                hypothesisId=service.Hypothesis("Remove enumerator allocation.","Allocation and elapsed cost decrease.",[]).Id;
                var allocation=ObjectiveTests.Allocation(true);
                var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,10,500,1,
                    42,"quiet-validation",true,"Declared independent synthetic laboratory blocks; no population generalization.",false,30,new(),
                    GuardrailMetrics:[allocation.Metric],GuardrailPolicies:[allocation]));
                var grant=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,60,20);
                var operation=await service.StartExecutionAsync(identity.Id,0,plan.Id,grant.Id,"execution-one",CancellationToken.None);
                var finished=await Wait(service,operation.Id);
                Assert.True(finished.State==OperationState.Completed,finished.ErrorCode);
                executionId=finished.ResultId!;
                Assert.Equal(20,service.Catalog.Get<ExecutionRecord>(executionId).RunIds.Count);
                var replay=await service.StartExecutionAsync(identity.Id,0,plan.Id,grant.Id,"execution-one",CancellationToken.None);
                Assert.Equal(operation.Id,replay.Id);
                var comparison=Assert.Single(service.Compare(executionId));
                comparisonId=comparison.Id;
                Assert.False(comparison.PromotionEligible);
                Assert.Equal(comparison.Id,Assert.Single(service.Compare(executionId)).Id);
                var vectorReply=await new Tedd.DotNetLab.Mcp.LabTools(service).Compare(new(executionId,IncludeObjectives:true));
                Assert.True(vectorReply.Status=="ok",Wire.Serialize(vectorReply));
                var index=Assert.Single(vectorReply.Data!.Value.Deserialize<ObjectiveVectorIndex[]>(Wire.Json)!);
                var vector=await service.ReadObjectivesAsync(index.Id);
                Assert.Equal(2,vector.Effects.Count); Assert.False(vector.PromotionEligible);
                Assert.Equal(comparison.Ratio,vector.Effects[0].Effect);
                Assert.Equal("process",vector.Effects[1].Scope);
                Assert.True(service.Catalog.IsPinned(index.DetailArtifactId));
                service.WorkItem("Confirm in fresh normal builds.",1,[],[comparisonId]);
                service.Decision(comparisonId,"retain_for_confirmation","Exploratory evidence only.");
            }
            await using var reopened=new LabService(workspace.Root);
            Assert.Equal(comparisonId,Assert.Single(reopened.Compare(executionId)).Id);
            Assert.Equal(hypothesisId,Assert.Single(reopened.Catalog.List("hypothesis").Rows).Id);
            Assert.Single(reopened.Catalog.List("work_item").Rows);
            Assert.Single(reopened.Catalog.List("decision").Rows);
            Assert.Single(await reopened.CompareObjectivesAsync(executionId));
        }
        finally { if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); } }
    }
    [Fact, Case("UT-SEC-004", "FR-070", "M3", "Integration")]
    public async Task Execution_ObservationGrant_IsDenied()
    {
        using var workspace=new TestWorkspace();
        using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root);
            var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var observe=service.IssueGrant(identity.Id,Permission.Observe,60,1);
            Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>
                service.StartExecutionAsync(identity.Id,0,"unused",observe.Id,"denied",CancellationToken.None))).Code);
        }
        finally { if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); } }
    }
    [Fact, Case("UT-ART-009", "FR-021,FR-026", "M3", "Integration")]
    public async Task Capture_StoredAnalysisAndMissingEvidence_AreExplicit()
    {
        using var workspace=new TestWorkspace();
        using var target=await CaptureTests.StartTarget();
        try
        {
            await using var service=new LabService(workspace.Root);
            var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var grant=service.IssueGrant(identity.Id,Permission.Observe,60,1);
            var operation=service.StartCapture(identity.Id,0,"baseline-context",1,grant.Id,"capture-once");
            var done=await Wait(service,operation.Id);
            Assert.True(done.State==OperationState.Completed,done.ErrorCode);
            var analysis=Wire.Deserialize<AnalysisIndex>(Assert.Single(service.Catalog.List("analysis").Rows).Json);
            var missing=await service.MetricAsync(analysis.Id,"method.average_ns");
            Assert.Equal(Availability.NotCollected,missing.Status);
            Assert.Null(missing.Value);
            var capture=service.Catalog.Get<Capture>(done.ResultId!);
            var raw=service.Catalog.Get<Artifact>(capture.ArtifactId!);
            service.Artifacts.Delete(raw);
            Assert.Equal(Availability.ArtifactExpired,(await service.MetricAsync(analysis.Id,"gc.cycle_count")).Status);
            Assert.NotEmpty((await service.AnalysisAsync(analysis.Id)).GcCycles);
        }
        finally { if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); } }
    }
    [Fact, Case("UT-EXP-012", "FR-030", "M3", "Unit")]
    public async Task WorkItems_CyclicRevision_IsRejected()
    {
        using var workspace=new TestWorkspace();
        await using var service=new LabService(workspace.Root);
        var a=service.WorkItem("A",1,[],[]);
        var b=service.WorkItem("B",2,[a.Id],[]);
        Assert.Equal("dependency_cycle",Assert.Throws<LabException>(()=>service.WorkItem("A revised",1,[b.Id],[],a.Id,1)).Code);
        Assert.Equal(1,service.Catalog.Get(a.Id).Revision);
    }
}
