using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class NativeTrialTests
{
    [Theory,InlineData("Value","native-value"),InlineData("InlineLeaf","native-inliner"),Case("IT-REJIT-003,IT-E2E-002,UT-TXN-023","FR-031,FR-032,FR-052,FR-053,FR-055,FR-062","M6","Integration")]
    public async Task RandomizedBodyTrial_UsesDistinctVersionsRestoresEachArmAndSurvivesRestart(string method,string scenario)
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root); string resultId; LabService? active=null;
        try
        {
            await using(var service=new LabService(workspace.Root,mutationJournalCommitted:item=>
            {
                if(item.Phase!="native_trial_completed") return;
                var root=active!.Catalog.Get<MutationRoot>(item.TransactionId);
                Assert.Equal(OperationState.Running,active.Status(root.OperationId).State);
                Assert.Equal("lease_conflict",Assert.Throws<LabException>(()=>active.Catalog.AcquireLease(root.Target.Id,root.OperationId)).Code);
            }))
            {
                active=service;
                var prepared=await CompilationControlTests.Prepare(service,process.Id,method);
                var control=await CompilationAdmissionTests.Control(service,prepared.Target,prepared.Candidate,scenario);
                var candidate=await CompilationAdmissionTests.Candidate(service,prepared.InspectionId,prepared.Candidate.OriginalIlHash);
                var tools=new LabTools(service); var frozen=await tools.Experiment(new CreateNativeTrial(candidate.Id,control.Id,Blocks:10,Seed:"8181"));
                Assert.True(frozen.Status=="ok",Wire.Serialize(frozen)); var plan=frozen.Data!.Value.Deserialize<NativeTrialPlan>(Wire.Json)!;
                Assert.Equal(10,plan.Blocks.Count); Assert.Equal(2,plan.Blocks.Select(b=>b.Order[0]).Distinct().Count());
                var originalGrant=service.IssueMutationGrant(prepared.Target.Id,prepared.Candidate.Id,300,10);
                var candidateGrant=service.IssueMutationGrant(prepared.Target.Id,candidate.Id,300,10);
                var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,300,60);
                var inspect=service.IssueGrant(prepared.Target.Id,Permission.InspectLiveCode,300,120);
                var reply=await tools.Patch(new RunNativeTrial(plan.Id,originalGrant.Id,candidateGrant.Id,execute.Id,inspect.Id,"frozen-trial"));
                Assert.True(reply.Status=="ok",Wire.Serialize(reply)); var operation=reply.Data!.Value.Deserialize<Operation>(Wire.Json)!;
                Assert.Equal(operation.Id,(await service.StartNativeTrialAsync(plan.Id,originalGrant.Id,candidateGrant.Id,execute.Id,inspect.Id,"frozen-trial")).Id);
                var complete=await ServiceTests.Wait(service,operation.Id); Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); resultId=complete.ResultId!;
                var result=service.Catalog.Get<NativeTrialResult>(resultId); Assert.Equal(20,result.RunIds.Count); Assert.Equal(1,result.IndependentSessions);
                Assert.Equal("descriptive_only",result.Quality); Assert.False(result.EquivalencePassed||result.PromotionEligible); Assert.True(result.GeometricCandidateToOriginalRatio>0);
                var rows=result.RunIds.Select(service.Catalog.Get<NativeTrialRun>).ToArray();
                foreach(var block in plan.Blocks)
                {
                    var arms=rows.Where(r=>r.BlockOrdinal==block.Ordinal).OrderBy(r=>r.ArmOrdinal).ToArray();
                    Assert.Equal(block.Order,arms.Select(r=>r.Compilation.Regime)); Assert.True(arms[0].RestoredEpoch<arms[1].Target.Epoch);
                    Assert.NotEqual(arms[0].Compilation.RejitId,arms[1].Compilation.RejitId);
                }
                Assert.All(rows,run=>
                {
                    Assert.Equal(control.Id,run.Compilation.CompilationControlId); Assert.Equal(3,run.Compilation.RequestFlags);
                    Assert.Equal(run.Compilation.Regime=="original_body_rejit"?prepared.Candidate.BodyHash:candidate.BodyHash,run.Compilation.BodyHash);
                    Assert.Equal(run.Target.Epoch+1,run.RestoredEpoch); Assert.Equal(plan.HarnessVariant,run.Measurement.Variant);
                    var root=service.Catalog.Get<MutationRoot>(run.TransactionId); Assert.Equal(plan.Id,root.NativeTrialPlanId);
                    var outcome=service.Catalog.Get<MutationOutcome>(root.Id+"-outcome"); Assert.True(outcome.Restored); Assert.False(outcome.Tainted);
                });
                Assert.Equal(control.RestoredEpoch+40,result.RestoredEpoch); Assert.Equal(result.RestoredEpoch,(await service.ScenariosAsync(prepared.Target.Id,CancellationToken.None)).Target.Epoch);
                Assert.Equal(operation.Id,(await service.StartNativeTrialAsync(plan.Id,originalGrant.Id,candidateGrant.Id,execute.Id,inspect.Id,"frozen-trial")).Id);
                Assert.Equal("idempotency_conflict",(await Assert.ThrowsAsync<LabException>(()=>service.StartNativeTrialAsync(plan.Id,originalGrant.Id,candidateGrant.Id,execute.Id,inspect.Id,"extra-trial"))).Code);
                Assert.True(service.Catalog.IsPinned(result.ArtifactId));
                Assert.Equal(Wire.Hash(result),Wire.Hash(await service.VerifyNativeTrialAsync(result.Id)));
            }
            await using var reopened=new LabService(workspace.Root);
            var retained=reopened.Catalog.Get<NativeTrialResult>(resultId); Assert.Equal(20,retained.RunIds.Count);
            foreach(string id in retained.RunIds) Assert.NotNull(reopened.Catalog.Get<NativeTrialRun>(id).Compilation);
            var verified=await new LabTools(reopened).Query(new QueryNativeTrial(resultId)); Assert.True(verified.Status=="ok",Wire.Serialize(verified));
            reopened.Artifacts.Delete(reopened.Catalog.Get<Artifact>(reopened.Catalog.Get<NativeTrialRun>(retained.RunIds[0]).CodeBeforeArtifactId));
            Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>reopened.VerifyNativeTrialAsync(resultId))).Code);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Theory,InlineData(false),InlineData(true),Case("UT-TXN-015,UT-SEC-004","FR-052,FR-053,FR-070","M6","Integration")]
    public async Task MeasurementCancellationOrTargetDeath_CannotPublishCompletedTrial(bool kill)
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root); LabService? active=null;
        try
        {
            await using var service=new LabService(workspace.Root,mutationJournalCommitted:item=>
            {
                if(item.Phase!="native_trial_measurement_completed") return;
                if(kill) { if(!process.HasExited) process.Kill(true); }
                else active!.Cancel(active.Catalog.Get<MutationRoot>(item.TransactionId).OperationId);
            }); active=service;
            var prepared=await CompilationControlTests.Prepare(service,process.Id); var control=await CompilationAdmissionTests.Control(service,prepared.Target,prepared.Candidate);
            var candidate=await CompilationAdmissionTests.Candidate(service,prepared.InspectionId,prepared.Candidate.OriginalIlHash);
            var plan=await service.CreateNativeTrialAsync(candidate.Id,control.Id,2);
            var originalGrant=service.IssueMutationGrant(prepared.Target.Id,prepared.Candidate.Id,120,2); var changedGrant=service.IssueMutationGrant(prepared.Target.Id,candidate.Id,120,2);
            var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,120,12); var inspect=service.IssueGrant(prepared.Target.Id,Permission.InspectLiveCode,120,24);
            Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>service.StartNativeTrialAsync(plan.Id,execute.Id,changedGrant.Id,execute.Id,inspect.Id,"wrong-original-permission"))).Code);
            var complete=await ServiceTests.Wait(service,(await service.StartNativeTrialAsync(plan.Id,originalGrant.Id,changedGrant.Id,execute.Id,inspect.Id,"interrupted-trial")).Id);
            Assert.Equal(kill?OperationState.Tainted:OperationState.Failed,complete.State);
            Assert.Empty(service.Catalog.List("native_trial_result").Rows); Assert.Empty(service.Catalog.List("native_trial_run").Rows);
            var outcome=service.Catalog.Get<MutationOutcome>(complete.ResultId!);
            Assert.Equal(kill,outcome.Tainted); Assert.Equal(!kill,outcome.Restored);
            if(!kill) Assert.False((await service.ScenariosAsync(prepared.Target.Id,CancellationToken.None)).Tainted);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
