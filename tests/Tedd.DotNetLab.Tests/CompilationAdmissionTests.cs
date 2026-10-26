using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using System.Text.Json;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CompilationAdmissionTests
{
    internal static async Task<CilCandidateRecord> Candidate(LabService service,string inspectionId,string original)
    {
        var artifact=await service.Artifacts.WriteJsonAsync("equivalent-candidate",new CilInput(Wire.Version,[0x16,0x1f,40,0x18,0x58,0x2a],"Equivalent integer expression."),"fixture");
        try { Assert.Equal(Wire.Hash(artifact),service.Catalog.Get(artifact.Id).Hash); }
        catch(LabException error) when(error.Code=="record_not_found") { service.Catalog.Put(artifact.Id,"artifact",artifact); }
        return await service.RegisterCilAsync(artifact.Id,inspectionId,original,[]);
    }
    private static async Task<ExperimentPlan> Plan(LabService service,string targetId,string? controlId=null,int warmup=1)
    {
        var scenario=(await service.ScenariosAsync(targetId,CancellationToken.None)).Scenarios.Single(s=>s.Id=="native-value");
        var response=await new LabTools(service).Experiment(new CreatePlan(scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,
            OperationsPerRun:1000,WarmupBatches:warmup,IndependentBlocks:true,IndependenceJustification:"Declared controlled fixture blocks.",CompilationControlId:controlId));
        Assert.True(response.Status=="ok",Wire.Serialize(response)); return response.Data!.Value.Deserialize<ExperimentPlan>(Wire.Json)!;
    }
    internal static async Task<CompilationControlResult> Control(LabService service,TargetIdentity target,CilCandidateRecord original,string scenario="native-value")
    {
        var plan=await service.CreateCompilationControlAsync(original.Id,scenario);
        var mutate=service.IssueMutationGrant(target.Id,original.Id,120,8); var execute=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,120,32);
        var inspect=service.IssueGrant(target.Id,Permission.InspectLiveCode,120,12);
        var complete=await ServiceTests.Wait(service,(await service.StartCompilationControlAsync(plan.Id,mutate.Id,execute.Id,inspect.Id,Wire.Id("aa"))).Id);
        Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete));
        return service.Catalog.Get<CompilationControlResult>(service.Catalog.Get<MutationOutcome>(complete.ResultId!).CompilationControlResultId!);
    }
    [Fact,Case("UT-TXN-023,UT-EXP-006","FR-031,FR-032,FR-052","M6","Integration")]
    public async Task CandidatePerformanceTrialRequiresAFrozenCompletedControlBeforeAnyWork()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var prepared=await CompilationControlTests.Prepare(service,process.Id);
            var candidate=await Candidate(service,prepared.InspectionId,prepared.Candidate.OriginalIlHash);
            var mutate=service.IssueMutationGrant(prepared.Target.Id,candidate.Id,120,8); var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,120,32);
            var apply=await ServiceTests.Wait(service,(await service.ApplyCilAsync(prepared.Target.Id,0,candidate.Id,mutate.Id,execute.Id,"native-value","apply")).Id);
            Assert.Equal(OperationState.Completed,apply.State); var outcome=service.Catalog.Get<MutationOutcome>(apply.ResultId!); var plan=await Plan(service,prepared.Target.Id);
            Assert.Equal("compilation_control_required",(await Assert.ThrowsAsync<LabException>(()=>service.StartExecutionAsync(prepared.Target.Id,outcome.Epoch,plan.Id,execute.Id,"unqualified",CancellationToken.None))).Code);
            Assert.Empty(service.Catalog.List("execution").Rows);
            var individual=service.CreateIndividualLatencyPlan(plan.ScenarioId,plan.ScenarioVersion,plan.InputHash,"baseline",1000);
            Assert.Equal("compilation_control_required",(await Assert.ThrowsAsync<LabException>(()=>service.StartIndividualLatenciesAsync(prepared.Target.Id,outcome.Epoch,individual.Id,execute.Id,"unqualified-individual",CancellationToken.None))).Code);
            Assert.Empty(service.Catalog.List("individual_latency_run").Rows);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("IT-REJIT-006,IT-E2E-002","FR-032,FR-052,FR-055","M6","Integration")]
    public async Task QualifiedCandidateTrial_RetainsActiveCodeRegimeAndControlBindingAcrossRestart()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root); string executionId;
        try
        {
            await using(var service=new LabService(workspace.Root))
            {
                var prepared=await CompilationControlTests.Prepare(service,process.Id); var control=await Control(service,prepared.Target,prepared.Candidate);
                var candidate=await Candidate(service,prepared.InspectionId,prepared.Candidate.OriginalIlHash);
                var plan=await Plan(service,prepared.Target.Id,control.Id); Assert.Equal(control.Id,plan.CompilationControl!.ResultId);
                Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.Plan(plan with { WarmupBatches=2 })).Code);
                Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.Plan(plan with { FreshNormalBuild=true })).Code);
                var mutate=service.IssueMutationGrant(prepared.Target.Id,candidate.Id,120,8); var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,120,32);
                var apply=await ServiceTests.Wait(service,(await service.ApplyCilAsync(prepared.Target.Id,control.RestoredEpoch,candidate.Id,mutate.Id,execute.Id,"native-value","candidate")).Id);
                Assert.Equal(OperationState.Completed,apply.State); var outcome=service.Catalog.Get<MutationOutcome>(apply.ResultId!);
                var complete=await ServiceTests.Wait(service,(await service.StartExecutionAsync(prepared.Target.Id,outcome.Epoch,plan.Id,execute.Id,"qualified-trial",CancellationToken.None)).Id);
                Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); executionId=complete.ResultId!;
                var rows=service.Catalog.Get<ExecutionRecord>(executionId).RunIds.Select(service.Catalog.Get<RunRecord>).ToArray(); Assert.Equal(20,rows.Length);
                Assert.All(rows,r=>{ Assert.Equal("candidate_rejit",r.Compilation!.Regime); Assert.Equal(candidate.BodyHash,r.Compilation.BodyHash);
                    Assert.Equal(control.Id,r.Compilation.CompilationControlId); Assert.Equal(outcome.Epoch,r.Compilation.Epoch); Assert.Equal(3,r.Compilation.RequestFlags); Assert.NotEqual("0",r.Compilation.RejitId); });
                var comparison=Assert.Single(service.Compare(executionId)); Assert.False(comparison.PromotionEligible);
                var individual=service.CreateIndividualLatencyPlan(plan.ScenarioId,plan.ScenarioVersion,plan.InputHash,"baseline",1000,compilationControlId:control.Id);
                var individualOperation=await ServiceTests.Wait(service,(await service.StartIndividualLatenciesAsync(prepared.Target.Id,outcome.Epoch,individual.Id,execute.Id,"qualified-individual",CancellationToken.None)).Id);
                Assert.True(individualOperation.State==OperationState.Completed,Wire.Serialize(individualOperation));
                var individualRun=service.Catalog.Get<IndividualLatencyRun>(individualOperation.ResultId!);
                Assert.Equal("candidate_rejit",individualRun.CompilationRegime); Assert.Equal(control.Id,individualRun.Compilation!.CompilationControlId);
                Assert.Equal(candidate.BodyHash,individualRun.Compilation.BodyHash); Assert.Equal(outcome.Epoch,individualRun.Compilation.Epoch);
                using var slice=JsonDocument.Parse(Wire.Serialize(await service.IndividualLatencySliceAsync(individualRun.Id)));
                Assert.Equal(1000,slice.RootElement.GetProperty("total").GetInt32());
                var artifact=service.Catalog.Get<Artifact>(control.ArtifactId); service.Artifacts.Delete(artifact);
                Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>service.StartExecutionAsync(prepared.Target.Id,outcome.Epoch,plan.Id,execute.Id,"expired-control",CancellationToken.None))).Code);
            }
            await using var reopened=new LabService(workspace.Root);
            Assert.All(reopened.Catalog.Get<ExecutionRecord>(executionId).RunIds.Select(reopened.Catalog.Get<RunRecord>),r=>Assert.Equal("candidate_rejit",r.Compilation!.Regime));
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("UT-TXN-023,UT-SEC-002","FR-032,FR-052,FR-070","M6","Integration")]
    public async Task CompilationControlCannotAuthorizeAnotherNativeTargetSession()
    {
        using var workspace=new TestWorkspace(); using var first=await ProfilerTests.StartNativeTarget(workspace.Root);
        using var second=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root);
            var source=await CompilationControlTests.Prepare(service,first.Id); var control=await Control(service,source.Target,source.Candidate);
            var target=await CompilationControlTests.Prepare(service,second.Id);
            var candidate=await Candidate(service,target.InspectionId,target.Candidate.OriginalIlHash);
            var mutate=service.IssueMutationGrant(target.Target.Id,candidate.Id,120,8); var execute=service.IssueGrant(target.Target.Id,Permission.ExecuteRegisteredScenario,120,32);
            var apply=await ServiceTests.Wait(service,(await service.ApplyCilAsync(target.Target.Id,0,candidate.Id,mutate.Id,execute.Id,"native-value","other-target")).Id);
            Assert.Equal(OperationState.Completed,apply.State); var outcome=service.Catalog.Get<MutationOutcome>(apply.ResultId!);
            var plan=await Plan(service,target.Target.Id,control.Id);
            Assert.Equal("control_mismatch",(await Assert.ThrowsAsync<LabException>(()=>service.StartExecutionAsync(target.Target.Id,outcome.Epoch,plan.Id,execute.Id,"wrong-session",CancellationToken.None))).Code);
            Assert.Empty(service.Catalog.List("execution").Rows);
        }
        finally
        {
            foreach(var process in new[]{first,second}) { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
        }
    }
    [Fact,Case("UT-TXN-023","FR-032,FR-052,FR-053","M6","Integration")]
    public async Task OriginalAndRevertedVersionRemainDistinctObservedRegimes()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var prepared=await CompilationControlTests.Prepare(service,process.Id); var plan=await Plan(service,prepared.Target.Id);
            var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,120,64);
            var before=await ServiceTests.Wait(service,(await service.StartExecutionAsync(prepared.Target.Id,0,plan.Id,execute.Id,"normal",CancellationToken.None)).Id);
            Assert.Equal(OperationState.Completed,before.State);
            Assert.All(service.Catalog.Get<ExecutionRecord>(before.ResultId!).RunIds.Select(service.Catalog.Get<RunRecord>),r=>Assert.Equal("original_normal_compile",r.Compilation!.Regime));
            var control=await Control(service,prepared.Target,prepared.Candidate);
            var after=await ServiceTests.Wait(service,(await service.StartExecutionAsync(prepared.Target.Id,control.RestoredEpoch,plan.Id,execute.Id,"reverted",CancellationToken.None)).Id);
            Assert.Equal(OperationState.Completed,after.State);
            Assert.All(service.Catalog.Get<ExecutionRecord>(after.ResultId!).RunIds.Select(service.Catalog.Get<RunRecord>),r=>Assert.Equal("restored_original_version",r.Compilation!.Regime));
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
