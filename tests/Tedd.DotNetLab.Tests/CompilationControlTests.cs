using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Transport.Local;
using System.Text.Json;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CompilationControlTests
{
    internal static async Task<(TargetIdentity Target,string InspectionId,CilCandidateRecord Candidate)> Prepare(LabService service,int processId,string methodName="Value")
    {
        var target=service.Discover().Single(t=>t.Identity.ProcessId==processId).Identity; await service.ProfilerAsync(target.Id,CancellationToken.None);
        target=service.Target(target.Id).Identity;
        var inspect=service.IssueGrant(target.Id,Permission.InspectLiveCode,120,3);
        var image=await service.ImportAsync(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"),"application/vnd.microsoft.portable-executable");
        var method=Assert.Single((await service.MethodsAsync(image.Id,"NativeFixture."+methodName,100,null)).Rows);
        var acquired=await ServiceTests.Wait(service,(await service.InspectLiveAsync(target.Id,method.Id,0,inspect.Id,null,CancellationToken.None)).Id);
        Assert.True(acquired.State==OperationState.Completed,Wire.Serialize(acquired));
        var response=await new LabTools(service).Patch(new PrepareOriginalControl(acquired.ResultId!)); Assert.True(response.Status=="ok",Wire.Serialize(response));
        var candidate=response.Data!.Value.Deserialize<CilCandidateRecord>(Wire.Json)!; Assert.Equal(candidate.OriginalIlHash,candidate.BodyHash);
        return(target,acquired.ResultId!,candidate);
    }
    [Theory,InlineData("Value","native-value"),InlineData("InlineLeaf","native-inliner"),Case("UT-TXN-023,IT-REJIT-006","FR-032,FR-052,FR-053,FR-055","M6","Integration")]
    public async Task ByteIdenticalOriginalControl_MeasuresDistinctRegimesAndRestoresBeforeTerminal(string method,string scenario)
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root); string resultId;
        try
        {
            await using(var service=new LabService(workspace.Root))
            {
                var prepared=await Prepare(service,process.Id,method); var tools=new LabTools(service);
                var frozen=await tools.Experiment(new CreateCompilationControl(prepared.Candidate.Id,scenario,OperationsPerRun:1000,WarmupBatches:2));
                Assert.True(frozen.Status=="ok",Wire.Serialize(frozen)); var plan=frozen.Data!.Value.Deserialize<CompilationControlPlan>(Wire.Json)!;
                var mutation=service.IssueMutationGrant(prepared.Target.Id,prepared.Candidate.Id,120,8);
                var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,120,32);
                var inspect=service.IssueGrant(prepared.Target.Id,Permission.InspectLiveCode,120,12);
                var reply=await tools.Patch(new RunCompilationControl(plan.Id,mutation.Id,execute.Id,inspect.Id,"original-control"));
                Assert.True(reply.Status=="ok",Wire.Serialize(reply)); var operation=reply.Data!.Value.Deserialize<Operation>(Wire.Json)!;
                var complete=await ServiceTests.Wait(service,operation.Id); Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete));
                var outcome=service.Catalog.Get<MutationOutcome>(complete.ResultId!); Assert.True(outcome.Restored); Assert.False(outcome.Measurable||outcome.Tainted);
                resultId=outcome.CompilationControlResultId!; var result=service.Catalog.Get<CompilationControlResult>(resultId);
                Assert.True(result.ProtocolVerified&&result.RestorationVerified); Assert.Equal(20,result.MeasuredRuns); Assert.Equal(1,result.IndependentSessions);
                Assert.False(result.EquivalencePassed||result.PromotionEligible); Assert.Equal("descriptive_only",result.Quality); Assert.True(result.GeometricRejitToNormalRatio>0);
                var retained=new List<CompilationControlMeasurement>(); string? cursor=null;
                do
                {
                    var page=service.Catalog.List("compilation_control_measurement",100,cursor);
                    retained.AddRange(page.Rows.Select(r=>Wire.Deserialize<CompilationControlMeasurement>(r.Json))); cursor=page.NextCursor;
                } while(cursor is not null);
                var rows=retained.ToArray();
                Assert.Equal(10,rows.Count(r=>r.Regime=="original_normal_compile")); Assert.Equal(10,rows.Count(r=>r.Regime=="original_body_rejit"));
                Assert.All(rows,r=>{ Assert.True(r.Measurement.Correct&&r.Measurement.Quiescent); Assert.Equal(1000,r.Measurement.CompletedOperations); });
                Assert.All(rows.Where(r=>r.Regime=="original_normal_compile"),r=>{ Assert.Equal(0,r.Epoch); Assert.Equal(0,r.RejitRequestFlags); });
                Assert.All(rows.Where(r=>r.Regime=="original_body_rejit"),r=>{ Assert.Equal(1,r.Epoch); Assert.Equal(3,r.RejitRequestFlags); });
                var code=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(rows.First(r=>r.Regime=="original_body_rejit").CodeAfterArtifactId))));
                Assert.Contains(code.Snapshots,s=>s.RejitId!="0"&&s.CurrentIlHash==prepared.Candidate.OriginalIlHash);
                var status=await new ProfilerClient().SendAsync(prepared.Target,new ProfilerRequest(Wire.Version,"mutation_status",prepared.Target.Id,prepared.Target.ProcessId,prepared.Target.StartTimeUtcTicks,result.RestoredEpoch),CancellationToken.None);
                Assert.Equal("restored_original_version",status.GetProperty("mode").GetString()); Assert.True(status.GetProperty("restore_api_succeeded").GetBoolean()); Assert.False(status.GetProperty("pending").GetBoolean());
                Assert.Equal(operation.Id,(await service.StartCompilationControlAsync(plan.Id,mutation.Id,execute.Id,inspect.Id,"original-control")).Id);
                Assert.Equal("stale_epoch",(await Assert.ThrowsAsync<LabException>(()=>service.StartCompilationControlAsync(plan.Id,mutation.Id,execute.Id,inspect.Id,"extra-control"))).Code);
                Assert.True(service.Catalog.IsPinned(result.ArtifactId));
                var events=service.Catalog.RecordsByPrefix("mutation_event",result.TransactionId+"-event-").Select(r=>Wire.Deserialize<MutationEvent>(r.Json)).OrderBy(r=>r.Sequence).ToArray();
                Assert.True(Array.FindIndex(events,e=>e.Phase=="control_original_normal_completed")<Array.FindIndex(events,e=>e.Phase=="request_submitted"));
                Assert.True(Array.FindIndex(events,e=>e.Phase=="restored")<Array.FindIndex(events,e=>e.Phase=="compilation_control_completed"));
                var fresh=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,30,1);
                var target=service.Target(prepared.Target.Id).Identity;
                var measured=await new HarnessClient().ExecuteAsync(new Tedd.DotNetLab.Contracts.RunRequest("health","health","health",scenario,"baseline",10,1,true,false,target.Epoch,target,fresh,DateTimeOffset.UtcNow.AddSeconds(20)),CancellationToken.None);
                Assert.True(measured.Correct);
            }
            await using var reopened=new LabService(workspace.Root); Assert.True(reopened.Catalog.Get<CompilationControlResult>(resultId).RestorationVerified);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("UT-SEC-004,UT-TXN-023","FR-032,FR-052,FR-070","M6","Integration")]
    public async Task WrongPermissionOrChangedBodyCannotBePresentedAsAnOriginalControl()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var prepared=await Prepare(service,process.Id);
            var plan=await service.CreateCompilationControlAsync(prepared.Candidate.Id,"native-value");
            var observe=service.IssueGrant(prepared.Target.Id,Permission.InspectLiveCode,90,4); var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,90,32);
            Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>service.StartCompilationControlAsync(plan.Id,observe.Id,execute.Id,observe.Id,"denied"))).Code);
            var altered=await service.Artifacts.WriteJsonAsync("changed-control",new CilInput(Wire.Version,[0x0e,0x1f,43,0x2a],"Different constant"),"fixture"); service.Catalog.Put(altered.Id,"artifact",altered);
            var candidate=await service.RegisterCilAsync(altered.Id,prepared.InspectionId,prepared.Candidate.OriginalIlHash,[]);
            Assert.Equal("invalid_plan",(await Assert.ThrowsAsync<LabException>(()=>service.CreateCompilationControlAsync(candidate.Id,"native-value"))).Code);
            Assert.Empty(service.Catalog.List("compilation_control_result").Rows); Assert.Empty(service.Catalog.List("mutation").Rows);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("UT-TXN-016,IT-REJIT-004","FR-037,FR-052,FR-053","M6","Integration")]
    public async Task CancellationBeforeSubmission_ProducesNoControlCertificationAndPreservesOriginalHealth()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root); LabService? active=null;
        try
        {
            await using var service=new LabService(workspace.Root,mutationJournalCommitted:e=>{ if(e.Phase=="control_original_normal_completed") active!.Cancel(active.Catalog.Get<MutationRoot>(e.TransactionId).OperationId); }); active=service;
            var prepared=await Prepare(service,process.Id); var plan=await service.CreateCompilationControlAsync(prepared.Candidate.Id,"native-value",blocks:2);
            var mutation=service.IssueMutationGrant(prepared.Target.Id,prepared.Candidate.Id,90,8); var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,90,8);
            var inspect=service.IssueGrant(prepared.Target.Id,Permission.InspectLiveCode,90,12);
            var complete=await ServiceTests.Wait(service,(await service.StartCompilationControlAsync(plan.Id,mutation.Id,execute.Id,inspect.Id,"cancel-before-apply")).Id);
            Assert.Equal(OperationState.Failed,complete.State); var outcome=service.Catalog.Get<MutationOutcome>(complete.ResultId!);
            Assert.Equal("cancelled",outcome.Failure); Assert.False(outcome.Tainted||outcome.Measurable); Assert.Null(outcome.CompilationControlResultId);
            Assert.Empty(service.Catalog.List("compilation_control_result").Rows);
            var status=await new ProfilerClient().SendAsync(prepared.Target,new ProfilerRequest(Wire.Version,"mutation_status",prepared.Target.Id,prepared.Target.ProcessId,prepared.Target.StartTimeUtcTicks,0),CancellationToken.None);
            Assert.Equal(0,status.GetProperty("mutation_count").GetInt32()); Assert.False(status.GetProperty("tainted").GetBoolean());
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("UT-TXN-019,IT-REJIT-004","FR-052,FR-053,FR-072","M6","Integration")]
    public async Task TargetLossBeforeRestore_CannotPublishSuccessfulControlOrReleaseTaint()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root,mutationJournalCommitted:e=>{ if(e.Phase=="control_original_body_rejit_completed"&&!process.HasExited) process.Kill(true); });
            var prepared=await Prepare(service,process.Id); var plan=await service.CreateCompilationControlAsync(prepared.Candidate.Id,"native-value",blocks:2);
            var mutation=service.IssueMutationGrant(prepared.Target.Id,prepared.Candidate.Id,120,8); var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,120,8);
            var inspect=service.IssueGrant(prepared.Target.Id,Permission.InspectLiveCode,120,12);
            var complete=await ServiceTests.Wait(service,(await service.StartCompilationControlAsync(plan.Id,mutation.Id,execute.Id,inspect.Id,"lost-before-restore")).Id);
            Assert.Equal(OperationState.Tainted,complete.State); var outcome=service.Catalog.Get<MutationOutcome>(complete.ResultId!);
            Assert.True(outcome.Tainted); Assert.False(outcome.Restored); Assert.Null(outcome.CompilationControlResultId); Assert.Empty(service.Catalog.List("compilation_control_result").Rows);
            var denied=service.Catalog.Accept("fixture","after-loss","hash","execution",prepared.Target.Id);
            Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>service.Catalog.AcquireLease(prepared.Target.Id,denied.Id)).Code);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
