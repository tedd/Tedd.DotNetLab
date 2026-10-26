using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Storage;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class MutationRecoveryTests
{
    private static async Task<CilCandidateRecord> Candidate(LabService service,Process process,bool probe,bool trackExceptions=false,bool inliner=false)
    {
        var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
        await service.ProfilerAsync(target.Id,CancellationToken.None);
        var grant=service.IssueGrant(target.Id,Permission.InspectLiveCode,180,3);
        var image=await service.ImportAsync(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"),
            "application/vnd.microsoft.portable-executable");
        var method=Assert.Single((await service.MethodsAsync(image.Id,inliner?"NativeFixture.InlineLeaf":trackExceptions?"NativeFixture.Divide":"NativeFixture.Value",100,null)).Rows);
        var operation=await ServiceTests.Wait(service,(await service.InspectLiveAsync(target.Id,method.Id,0,grant.Id,null,CancellationToken.None)).Id);
        Assert.Equal(OperationState.Completed,operation.State);
        var inspection=service.Catalog.Get<JsonElement>(operation.ResultId!);
        var artifact=service.Catalog.Get<Artifact>(inspection.GetProperty("manifest_artifact_id").GetString()!);
        var snapshot=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(artifact)));
        if(probe) return await service.RegisterProbeAsync(operation.ResultId!,snapshot.OriginalBodyHash,true,trackExceptions:trackExceptions);
        string path=Path.Combine(service.Catalog.Root,"replacement.json");
        await File.WriteAllTextAsync(path,Wire.Serialize(new CilInput(Wire.Version,[0x16,0x1f,40,0x18,0x58,0x2a],"equivalent integer expression")));
        return await service.RegisterCilAsync((await service.ImportAsync(path,"application/json")).Id,operation.ResultId!,snapshot.OriginalBodyHash,[]);
    }
    private static async Task<string> Crash(string workspace,object request)
    {
        string path=Path.Combine(workspace,"crash-request.json"); await File.WriteAllTextAsync(path,Wire.Serialize(request));
        var start=new ProcessStartInfo("dotnet")
        { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.ControllerFault","Release","net10.0","Tedd.DotNetLab.ControllerFault.dll"));
        start.ArgumentList.Add(path);
        using var process=Process.Start(TestWorkspace.PinRuntime(start))!;
        try
        {
            var output=process.StandardOutput.ReadToEndAsync(); var error=process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Assert.True(process.ExitCode==71,$"Unexpected fixture exit {process.ExitCode}: {await output}\n{await error}");
            return Wire.Deserialize<JsonElement>(await output).GetProperty("transaction_id").GetString()!;
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Theory]
    [InlineData("prepared",false,false,false,false)]
    [InlineData("request_submitted",false,false,false,false)]
    [InlineData("compilation_driving",false,false,false,false)]
    [InlineData("measurable",false,false,false,false)]
    [InlineData("outcome_recorded",false,false,false,false)]
    [InlineData("probe_helper_prepared",true,false,false,false)]
    [InlineData("probe_setup_verified",true,false,false,false)]
    [InlineData("measurable",true,false,false,false)]
    [InlineData("measurable",true,false,true,false)]
    [InlineData("measurable",true,false,false,true)]
    [InlineData("restoring",false,true,false,false)]
    [InlineData("restore_request_completed",false,true,false,false)]
    [Case("IT-TXN-RECOVERY", "FR-052,FR-053,FR-062,FR-070,FR-072", "M6", "Integration")]
    public async Task ControllerProcessDeath_ReconcilesOriginalAndHealthBeforeReuse(string checkpoint,bool probe,bool restoring,bool trackExceptions,bool inliner)
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            CilCandidateRecord candidate; Grant mutation,execute; long epoch=0; string? originalTransaction=null;
            string scenario=inliner?"native-inliner":trackExceptions?"native-divide":"native-value";
            await using(var initial=new LabService(workspace.Root))
            {
                candidate=await Candidate(initial,process,probe,trackExceptions,inliner);
                mutation=initial.IssueMutationGrant(candidate.SourceTarget.Id,candidate.Id,180,10);
                execute=initial.IssueGrant(candidate.SourceTarget.Id,Permission.ExecuteRegisteredScenario,180,10);
                if(restoring)
                {
                    var apply=await ServiceTests.Wait(initial,(await initial.ApplyCilAsync(candidate.SourceTarget.Id,0,candidate.Id,
                        mutation.Id,execute.Id,scenario,"initial")).Id);
                    Assert.Equal(OperationState.Completed,apply.State);
                    epoch=initial.Catalog.Get<MutationOutcome>(apply.ResultId!).Epoch;
                    originalTransaction=initial.Catalog.List("mutation").Rows.Select(r=>Wire.Deserialize<MutationRoot>(r.Json)).Single().Id;
                }
            }
            string transaction=await Crash(workspace.Root,new { workspace=workspace.Root,target_id=candidate.SourceTarget.Id,epoch,
                candidate_id=candidate.Id,grant_id=mutation.Id,execution_grant_id=execute.Id,scenario_id=scenario,checkpoint,
                transaction_id=originalTransaction });
            await using var recovered=new LabService(workspace.Root);
            var source=recovered.Catalog.Get<MutationRoot>(transaction);
            Assert.Equal(source.OperationId,recovered.Catalog.RecoverableMutationOperation(candidate.SourceTarget.Id));
            Assert.Equal(OperationState.Interrupted,recovered.Status(source.OperationId).State);
            var denied=recovered.Catalog.Accept("test","blocked","hash","execution",candidate.SourceTarget.Id);
            Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>recovered.Catalog.AcquireLease(candidate.SourceTarget.Id,denied.Id)).Code);
            var native=await recovered.ProfilerAsync(candidate.SourceTarget.Id,CancellationToken.None); epoch=native.Epoch;
            var fresh=recovered.IssueMutationGrant(candidate.SourceTarget.Id,candidate.Id,180,10);
            var freshExecute=recovered.IssueGrant(candidate.SourceTarget.Id,Permission.ExecuteRegisteredScenario,180,10);
            var tools=new LabTools(recovered);
            var response=await tools.Patch(new RecoverMutation(transaction,epoch.ToString(),fresh.Id,freshExecute.Id,"recover"));
            Assert.True(response.Status=="ok",response.Code+": "+response.Message);
            var operation=await ServiceTests.Wait(recovered,response.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
            Assert.True(operation.State==OperationState.Completed,operation.ErrorCode);
            var result=recovered.Catalog.Get<MutationRecoveryResult>(operation.ResultId!);
            Assert.Equal(checkpoint is "prepared" or "request_submitted" or "probe_helper_prepared"?"not_applied":"restored",result.Phase);
            Assert.True(result.OriginalBodyVerified); Assert.True(result.HealthVerified);
            Assert.False(result.PristineRuntime); Assert.False(result.PromotionEligible);
            Assert.Equal(candidate.OriginalIlHash,result.OriginalBodyHash); Assert.Equal(candidate.ContentHash,result.CandidateHash);
            Assert.Null(recovered.Catalog.RecoverableMutationOperation(candidate.SourceTarget.Id));
            foreach(string artifactId in new[]{result.NativeManifestArtifactId,result.ValidationArtifactId})
            {
                Assert.True(recovered.Catalog.IsPinned(artifactId));
                var artifact=recovered.Catalog.Get<Artifact>(artifactId); await recovered.Artifacts.VerifyAsync(artifact.Hash,artifact.Length);
            }
            Assert.Equal(operation.Id,(await recovered.RecoverMutationAsync(transaction,epoch,fresh.Id,freshExecute.Id,"recover")).Id);
            Assert.False((await recovered.ScenariosAsync(candidate.SourceTarget.Id,CancellationToken.None)).ProbeHelper!.Active);
            var run=new Tedd.DotNetLab.Contracts.RunRequest("after-recovery","run","block",scenario,"baseline",3,0,true,false,
                result.Target.Epoch,result.Target,freshExecute,DateTimeOffset.UtcNow.AddSeconds(30));
            Assert.True((await new HarnessClient().ExecuteAsync(run,CancellationToken.None)).Correct);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Theory,InlineData("execution"),InlineData("individual_latencies"),Case("UT-SEC-RECOVERY", "FR-070,FR-072", "M6", "Unit")]
    public void RecoveryLease_CannotClearExecutionTaintOrUnverifiedMutation(string executionKind)
    {
        using var workspace=new TestWorkspace(); string sourceId;
        using(var before=new Catalog(workspace.Root))
        {
            var source=before.Accept("test","source","hash",executionKind,"target"); sourceId=source.Id;
            before.AcquireLease("target",source.Id); before.Transition(source.Id,OperationState.Running);
        }
        using var catalog=new Catalog(workspace.Root); catalog.Recover();
        Assert.Null(catalog.RecoverableMutationOperation("target"));
        var recovery=catalog.Accept("test","recovery","hash","mutation_recovery","target");
        Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>catalog.AcquireMutationRecoveryLease("target",recovery.Id,sourceId)).Code);
        var next=catalog.Accept("test","next","hash","execution","target");
        Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>catalog.AcquireLease("target",next.Id)).Code);
    }
    [Fact, Case("UT-TXN-RECOVERY-PROOF", "FR-053,FR-072", "M6", "Unit")]
    public void MutationRecovery_RequiresCompleteScopedArtifactEvidence()
    {
        using var workspace=new TestWorkspace(); string sourceId;
        using(var before=new Catalog(workspace.Root))
        {
            var source=before.Accept("test","source","hash","mutation","target"); sourceId=source.Id;
            before.AcquireLease("target",source.Id); before.Transition(source.Id,OperationState.Running);
        }
        using var catalog=new Catalog(workspace.Root); catalog.Recover();
        var recovery=catalog.Accept("test","recovery","hash","mutation_recovery","target");
        catalog.AcquireMutationRecoveryLease("target",recovery.Id,sourceId); catalog.Transition(recovery.Id,OperationState.Running);
        var result=new MutationRecoveryResult("proof",recovery.Id,sourceId,new("target",1,1,"runtime","10.0.12","x64"),
            "native-tx",new('a',64),new('b',64),"scope","module","restored",new('c',64),"missing-native","missing-health",true,false);
        catalog.Put(result.Id,"mutation_recovery_result",result);
        Assert.Equal("recovery_unverified",Assert.Throws<LabException>(()=>catalog.CompleteMutationRecovery("target",recovery.Id,sourceId,result.Id)).Code);
        var missing=result with { Id="missing-artifacts",HealthVerified=true }; catalog.Put(missing.Id,"mutation_recovery_result",missing);
        Assert.Equal("recovery_unverified",Assert.Throws<LabException>(()=>catalog.CompleteMutationRecovery("target",recovery.Id,sourceId,missing.Id)).Code);
        Assert.Equal(sourceId,catalog.RecoverableMutationOperation("target"));
        catalog.ReleaseLease("target",recovery.Id,true);
        var next=catalog.Accept("test","next","hash","execution","target");
        Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>catalog.AcquireLease("target",next.Id)).Code);
    }
    [Theory,InlineData("expired"),InlineData("foreign_transaction")]
    [Case("IT-TXN-RECOVERY-NEGATIVE", "FR-052,FR-053,FR-070,FR-072", "M6", "Integration")]
    public async Task InvalidRecovery_CannotBypassExpiryOrUnknownNativeIdentity(string failure)
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            CilCandidateRecord candidate; Grant mutation,execute;
            await using(var initial=new LabService(workspace.Root))
            {
                candidate=await Candidate(initial,process,false);
                mutation=initial.IssueMutationGrant(candidate.SourceTarget.Id,candidate.Id,180,10);
                execute=initial.IssueGrant(candidate.SourceTarget.Id,Permission.ExecuteRegisteredScenario,180,10);
            }
            string transaction=await Crash(workspace.Root,new { workspace=workspace.Root,target_id=candidate.SourceTarget.Id,epoch=0L,
                candidate_id=candidate.Id,grant_id=mutation.Id,execution_grant_id=execute.Id,scenario_id="native-value",checkpoint="prepared",transaction_id=(string?)null });
            await using var recovered=new LabService(workspace.Root);
            var source=recovered.Catalog.Get<MutationRoot>(transaction); long epoch=0;
            if(failure=="expired")
            {
                var expired=recovered.IssueMutationGrant(candidate.SourceTarget.Id,candidate.Id,1,10);
                var wait=expired.ExpiresAt-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(30); if(wait>TimeSpan.Zero) await Task.Delay(wait);
                Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>recovered.RecoverMutationAsync(transaction,0,expired.Id,execute.Id,"expired"))).Code);
                Assert.Equal(source.OperationId,recovered.Catalog.RecoverableMutationOperation(candidate.SourceTarget.Id));
                Assert.Equal(0,(await recovered.ProfilerAsync(candidate.SourceTarget.Id,CancellationToken.None)).Epoch);
            }
            else
            {
                var input=Wire.Deserialize<CilInput>(await File.ReadAllTextAsync(await recovered.Artifacts.VerifiedPathAsync(recovered.Catalog.Get<Artifact>(candidate.BodyArtifactId))));
                var nativeGrant=mutation with { Id="foreign-native",ContentHash=candidate.BodyHash };
                var receipt=await new ProfilerClient().SendAsync(candidate.SourceTarget,new { schema_version=Wire.Version,action="apply",
                    target_id=candidate.SourceTarget.Id,process_id=process.Id,start_time_utc_ticks=candidate.SourceTarget.StartTimeUtcTicks,
                    expected_target_epoch=0L,grant=nativeGrant,transaction_id="foreign-native-tx",body=input.Body,base_il_hash=candidate.BaseIlHash,
                    selector=new { module_mvid=candidate.Method.ModuleMvid,method_token=candidate.Method.Token,module_hash=candidate.Method.ModuleHash,
                        signature=candidate.Method.SignatureHex,module_load_id=candidate.ModuleLoadIdentity } },CancellationToken.None);
                epoch=long.Parse(receipt.GetProperty("epoch").GetString()!);
                var recovery=await ServiceTests.Wait(recovered,(await recovered.RecoverMutationAsync(transaction,epoch,mutation.Id,execute.Id,"unknown")).Id);
                Assert.Equal(OperationState.Tainted,recovery.State); Assert.Equal("recovery_unverified",recovery.ErrorCode);
                Assert.Null(recovered.Catalog.RecoverableMutationOperation(candidate.SourceTarget.Id));
                Assert.Equal(epoch,(await recovered.ProfilerAsync(candidate.SourceTarget.Id,CancellationToken.None)).Epoch);
            }
            var next=recovered.Catalog.Accept("test","blocked","hash","execution",candidate.SourceTarget.Id);
            Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>recovered.Catalog.AcquireLease(candidate.SourceTarget.Id,next.Id)).Code);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    [Theory,InlineData("recovery_restore_submitted"),InlineData("recovery_committed")]
    [Case("IT-TXN-RECOVERY-RESTART", "FR-052,FR-053,FR-072", "M6", "Integration")]
    public async Task ControllerDiesDuringRecovery_OriginalInterruptionRemainsTheRecoveryAuthority(string checkpoint)
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            CilCandidateRecord candidate; Grant mutation,execute;
            await using(var initial=new LabService(workspace.Root))
            {
                candidate=await Candidate(initial,process,false);
                mutation=initial.IssueMutationGrant(candidate.SourceTarget.Id,candidate.Id,180,10);
                execute=initial.IssueGrant(candidate.SourceTarget.Id,Permission.ExecuteRegisteredScenario,180,10);
            }
            string transaction=await Crash(workspace.Root,new { workspace=workspace.Root,target_id=candidate.SourceTarget.Id,epoch=0L,
                candidate_id=candidate.Id,grant_id=mutation.Id,execution_grant_id=execute.Id,scenario_id="native-value",checkpoint="compilation_driving" });
            long epoch;
            await using(var second=new LabService(workspace.Root))
            {
                epoch=(await second.ProfilerAsync(candidate.SourceTarget.Id,CancellationToken.None)).Epoch;
                mutation=second.IssueMutationGrant(candidate.SourceTarget.Id,candidate.Id,180,10);
                execute=second.IssueGrant(candidate.SourceTarget.Id,Permission.ExecuteRegisteredScenario,180,10);
            }
            string recoveryRoot=await Crash(workspace.Root,new { workspace=workspace.Root,target_id=candidate.SourceTarget.Id,epoch,
                candidate_id=candidate.Id,grant_id=mutation.Id,execution_grant_id=execute.Id,scenario_id="native-value",
                checkpoint,recover_transaction_id=transaction });
            await using var third=new LabService(workspace.Root);
            var original=third.Catalog.Get<MutationRoot>(transaction);
            var interruptedRecovery=third.Catalog.Get<MutationRecoveryRoot>(recoveryRoot);
            Assert.Equal(checkpoint=="recovery_committed"?OperationState.Completed:OperationState.Interrupted,third.Status(interruptedRecovery.OperationId).State);
            epoch=(await third.ProfilerAsync(candidate.SourceTarget.Id,CancellationToken.None)).Epoch;
            Operation complete;
            if(checkpoint=="recovery_committed") complete=third.Status(interruptedRecovery.OperationId);
            else
            {
                Assert.Equal(original.OperationId,third.Catalog.RecoverableMutationOperation(candidate.SourceTarget.Id));
                complete=await ServiceTests.Wait(third,(await third.RecoverMutationAsync(transaction,epoch,mutation.Id,execute.Id,"recover-again")).Id);
            }
            Assert.Equal(OperationState.Completed,complete.State);
            Assert.Equal("restored",third.Catalog.Get<MutationRecoveryResult>(complete.ResultId!).Phase);
            Assert.Null(third.Catalog.RecoverableMutationOperation(candidate.SourceTarget.Id));
            var next=third.Catalog.Accept("test","after-recovery","hash","execution",candidate.SourceTarget.Id);
            third.Catalog.AcquireLease(candidate.SourceTarget.Id,next.Id); third.Catalog.ReleaseLease(candidate.SourceTarget.Id,next.Id,true);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
    private sealed class NoLiveRegistry : Tedd.DotNetLab.Collector.EventPipe.ITargetRegistry
    {
        public int Calls;
        public IReadOnlyList<TargetInfo> Discover()=>[];
        public void Verify(TargetIdentity target,long epoch) { Calls++; throw new InvalidOperationException("Invalid journal reached the live boundary."); }
    }
    [Theory,InlineData("missing"),InlineData("bad_head"),InlineData("wrong_candidate")]
    [Case("UT-TXN-JOURNAL-RECOVERY", "FR-052,FR-070,FR-072", "M6", "Unit")]
    public async Task InvalidJournal_IsRejectedBeforeAnyLiveBoundary(string fault)
    {
        using var workspace=new TestWorkspace(); string transaction;
        using(var catalog=new Catalog(workspace.Root))
        {
            var target=new TargetIdentity("target",1,1,"runtime","10.0.12","x64");
            var method=new StoredMethod(new('a',64),"00000000-0000-0000-0000-000000000001","0x06000001","Fixture","Value","000008",false,false,true,true);
            var candidate=new CilCandidateRecord("candidate",Wire.Version,target,method,"module","inspection","manifest","body",
                new('b',64),new('c',64),new('c',64),"fixture",[]);
            catalog.Put(candidate.Id,"cil_candidate",candidate);
            var operation=catalog.Accept("test","interrupted","hash","mutation",target.Id);
            catalog.AcquireLease(target.Id,operation.Id); catalog.Transition(operation.Id,OperationState.Running);
            var root=new MutationRoot(Wire.Id("mutation"),operation.Id,candidate.Id,candidate.ContentHash,target,"fixture","grant","execute","native-tx",100,30);
            catalog.Put(root.Id,"mutation",root); transaction=root.Id;
            if(fault!="missing")
            {
                var evidence=JsonSerializer.SerializeToElement(new { content_hash=fault=="wrong_candidate"?"different":candidate.ContentHash,
                    candidate.BodyHash,candidate.OriginalIlHash,candidate.BaseIlHash,candidate.Scope },Wire.Json);
                var item=new MutationEvent(root.Id+"-event-0001",root.Id,1,"prepared",fault=="bad_head"?"invalid-head":null,0,evidence,null,DateTimeOffset.UtcNow);
                catalog.Put(item.Id,"mutation_event",item);
            }
        }
        var registry=new NoLiveRegistry(); await using var service=new LabService(workspace.Root,targetRegistry:registry);
        Assert.Equal("journal_invalid",(await Assert.ThrowsAsync<LabException>(()=>service.RecoverMutationAsync(transaction,0,"missing-grant","missing-execute","recover"))).Code);
        Assert.Equal(0,registry.Calls);
        Assert.NotNull(service.Catalog.RecoverableMutationOperation("target"));
    }
}
