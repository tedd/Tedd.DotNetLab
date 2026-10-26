using System.Security.Cryptography;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public async Task<CilCandidateRecord> RegisterCilAsync(string bodyArtifactId,string inspectionId,string baseIlHash,
        IReadOnlyList<string> components)
    {
        if(Catalog.Get(inspectionId).Kind!="inspection") throw new LabException("invalid_request","Candidate requires a live acquisition record.");
        var inspection=Catalog.Get<JsonElement>(inspectionId);
        if(inspection.GetProperty("qualification").GetString()!="verified_live_acquisition")
            throw new LabException("invalid_request","Imported evidence cannot establish live candidate identity.");
        var target=inspection.GetProperty("target").Deserialize<TargetIdentity>(Wire.Json)!;
        var method=Catalog.Get<StoredMethod>(inspection.GetProperty("method_id").GetString()!);
        string manifestId=inspection.GetProperty("manifest_artifact_id").GetString()!;
        var manifest=Catalog.Get<Artifact>(manifestId);
        var source=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(manifest)));
        var identities=source.Snapshots.Where(s=>s.CurrentIlHash==baseIlHash).Select(s=>s.ModuleLoadIdentity).Distinct().ToArray();
        if(identities.Length!=1) throw new LabException("stale_candidate","Base body is absent or ambiguous in retained live evidence.");
        var artifact=Catalog.Get<Artifact>(bodyArtifactId);
        if(artifact.MediaType!="application/json"||artifact.Length>8192||!artifact.Complete)
            throw new LabException("invalid_request","Replacement input must be a sealed JSON artifact of at most 8 KiB.");
        var input=Wire.Deserialize<CilInput>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact)));
        if(input.SchemaVersion!=Wire.Version||string.IsNullOrWhiteSpace(input.Transformation)||input.Transformation.Length>4096||components.Count>32)
            throw new LabException("invalid_request","Invalid candidate schema, transformation, or component budget.");
        foreach(string component in components) if(Catalog.Get(component).Kind is not ("candidate" or "cil_candidate"))
            throw new LabException("invalid_request","Candidate component is not registered.");
        var image=await CodeArtifactAsync("art-"+method.ModuleHash,"application/vnd.microsoft.portable-executable");
        var policy=NativeBodyPolicy.Resolve(input.Body,method,image.Path);
        var verification=NativeBodyPolicy.Verify(input.Body,policy);
        var candidate=new CilCandidateRecord(Wire.Id("cil"),Wire.Version,target,method,identities[0],inspectionId,
            manifestId,artifact.Id,verification.Hash,baseIlHash,source.OriginalBodyHash,input.Transformation,components,Policy:policy,Inliners:source.Inliners??[]);
        Catalog.Put(candidate.Id,"cil_candidate",candidate);
        Catalog.Pin(artifact.Id,"candidate_body"); Catalog.Pin(manifestId,"candidate_original");
        return candidate;
    }
    // Human CLI only. The parent grant binds the complete candidate manifest, not merely its opcode bytes.
    public Grant IssueMutationGrant(string targetId,string candidateId,int seconds,int maximumOperations)
    {
        var candidate=CilCandidate(candidateId); var target=Target(targetId).Identity;
        if(target.Id!=candidate.SourceTarget.Id||target.StartTimeUtcTicks!=candidate.SourceTarget.StartTimeUtcTicks)
            throw new LabException("target_identity_changed","Candidate belongs to another target session.");
        if(seconds is <1 or >3600||maximumOperations is <1 or >100000) throw new LabException("invalid_request","Invalid mutation grant budget.");
        targets.Verify(target,target.Epoch);
        var grant=new Grant(Wire.Id("grant"),Catalog.Root,targetId,target.StartTimeUtcTicks,candidate.Probe is null?Permission.ReplaceIl:Permission.InstallProbes,
            DateTimeOffset.UtcNow.AddSeconds(seconds),maximumOperations,candidate.ContentHash,candidate.Scope);
        Catalog.Put(grant.Id,"grant",grant); return grant;
    }
    private CilCandidateRecord CilCandidate(string id)
    {
        if(Catalog.Get(id).Kind!="cil_candidate") throw new LabException("invalid_request","Expected a registered CIL candidate.");
        return Catalog.Get<CilCandidateRecord>(id);
    }
    private async Task<byte[]> CandidateBodyAsync(CilCandidateRecord candidate)
    {
        var artifact=Catalog.Get<Artifact>(candidate.BodyArtifactId);
        var input=Wire.Deserialize<CilInput>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact)));
        string hash=candidate.Probe is null?(candidate.Policy is null?NativeLeafPolicy.Verify(input.Body,candidate.Method):NativeBodyPolicy.Verify(input.Body,candidate.Policy)).Hash:
            Convert.ToHexStringLower(SHA256.HashData(input.Body));
        if(candidate.Probe is not null&&!input.Body.SequenceEqual(RewriteProbe(candidate.Method,candidate.Probe).Body))
            throw new LabException("artifact_corrupt","Probe body differs from its certified rewrite.");
        if(hash!=candidate.BodyHash)
            throw new LabException("artifact_corrupt","Candidate body differs from its immutable manifest.");
        return input.Body;
    }
    private Grant MutationGrant(string grantId,TargetIdentity target,CilCandidateRecord candidate)
    {
        if(Catalog.Get(grantId).Kind!="grant") throw new LabException("permission_denied","Expected a human-issued mutation grant.");
        var grant=Catalog.Get<Grant>(grantId);
        Policies.Authorize(grant,Catalog.Root,target,candidate.Probe is null?Permission.ReplaceIl:Permission.InstallProbes,DateTimeOffset.UtcNow,candidate.ContentHash,candidate.Scope);
        targets.Verify(target,target.Epoch); return grant;
    }
    private static Grant NativeGrant(Grant parent,CilCandidateRecord candidate,string transaction)=>parent with {
        Id="native-"+Wire.Hash(new { parent.Id,transaction,candidate.ContentHash })[..32],
        ContentHash=candidate.Inliners is {Count:>0}?Wire.Hash(new { body_hash=candidate.BodyHash,inliners=candidate.Inliners }):candidate.BodyHash,MaximumOperations=16 };
    private static void VerifyCallerEvidence(JsonElement state,CilCandidateRecord candidate,string phase)
    {
        var expected=candidate.Inliners??[];
        if(!state.TryGetProperty("affected_callers",out var callers)||callers.ValueKind!=JsonValueKind.Array||callers.GetArrayLength()!=expected.Count)
            throw new LabException("inliner_coverage_changed","Affected caller evidence differs from the frozen manifest.");
        int position=0;
        foreach(var caller in callers.EnumerateArray())
        {
            var binding=caller.GetProperty("binding").Deserialize<NativeInlinerBinding>(Wire.Json);
            if(binding!=expected[position++]||phase=="compiled"&&!caller.GetProperty("compilation_observed").GetBoolean()||
                phase=="restored"&&!caller.GetProperty("restore_api_succeeded").GetBoolean())
                throw new LabException("mutation_unverified","Every approved caller requires matching identity and per-method phase evidence.");
        }
    }
    private object MutationCommand(string action,TargetIdentity target,long epoch,CilCandidateRecord candidate,byte[] body,
        Grant nativeGrant,string transaction)=>new { schema_version=Wire.Version,action,target_id=target.Id,
            process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=epoch,
            grant=nativeGrant,transaction_id=transaction,body,base_il_hash=candidate.BaseIlHash,
            probe=candidate.Probe is not null,probe_id=0,helper_hash=candidate.Probe?.HelperHash??"",
            enter_token=candidate.Probe?.EnterToken??0,exit_token=candidate.Probe?.ExitToken??0,
            exceptional_exit_token=candidate.Probe?.Fault?.ExceptionalExitToken??0,
            probe_local_signature=candidate.Probe?.Fault?.LocalSignatureToken??0,probe_local_count=candidate.Probe?.Fault?.LocalCount??0,
            return_local=candidate.Probe?.Fault?.ReturnLocal??0,
            inliners=candidate.Inliners??[],
            selector=new { module_mvid=candidate.Method.ModuleMvid,method_token=candidate.Method.Token,
                module_hash=candidate.Method.ModuleHash,signature=candidate.Method.SignatureHex,module_load_id=candidate.ModuleLoadIdentity } };
    private async Task VerifyMutationAdapterAsync(string targetId,CilCandidateRecord candidate,ProfilerHandshake native,CancellationToken cancellation)
    {
        if(Target(targetId).Capabilities["rejit"].State!=CapabilityState.Supported||native.Rejit!="experimental"||(!native.Startup&&!VerifiedWindowsAttach(native)))
            throw new LabException("capability_unsupported","Mutation requires a qualified disposable profiler adapter.");
        if(!native.Startup)
        {
            if(candidate.Probe is not null) throw new LabException("capability_unsupported","Attach selected probes require separate qualification.");
            var manifest=Record<Artifact>(candidate.SourceManifestArtifactId,"artifact");
            var source=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(manifest,cancellation),cancellation));
            if(source.InlinerCoverage!="excluded_by_verified_noinline_method"||candidate.Inliners?.Count>0)
                throw new LabException("inliner_coverage_unknown","Attach mutation requires retained verified no-inline coverage.");
        }
    }
    private async Task<(TargetIdentity Target,Grant Grant,Grant Execute,int? ExpectedInvocations)> MutationContextAsync(string targetId,long epoch,
        CilCandidateRecord candidate,string grantId,string executionGrantId,string scenarioId,CancellationToken cancellation)
    {
        var native=await ProfilerAsync(targetId,cancellation); await VerifyMutationAdapterAsync(targetId,candidate,native,cancellation);
        var handshake=await ScenariosAsync(targetId,cancellation);
        if(handshake.Target.Epoch!=epoch) throw new LabException("stale_epoch","Mutation epoch differs from the target.");
        if(handshake.Tainted) throw new LabException("target_tainted","Harness quiescence is unknown.");
        if(candidate.SourceTarget.Id!=targetId||candidate.SourceTarget.StartTimeUtcTicks!=handshake.Target.StartTimeUtcTicks)
            throw new LabException("target_identity_changed","Candidate belongs to another target session.");
        var scenario=handshake.Scenarios.SingleOrDefault(s=>s.Id==scenarioId);
        if(scenario is null||scenario.IsAsync||scenario.Affinity!=ThreadAffinity.Any||!(scenario.MutationScopes?.Contains(candidate.Scope)??false))
            throw new LabException("scenario_scope_mismatch","Registered synchronous setup oracle does not cover the selected method.");
        if(candidate.Inliners?.Any(c=>!(scenario.MutationScopes?.Contains(c.Scope)??false))==true)
            throw new LabException("scenario_scope_mismatch","Setup quiescence and correctness must declare every frozen caller scope.");
        int? expected=scenario.MutationInvocationsPerOperation?.TryGetValue(candidate.Scope,out int count)==true?count:null;
        if(expected is <=0 or >1000000) throw new LabException("invalid_plan","Declared invocation oracle exceeds policy.");
        return(handshake.Target,MutationGrant(grantId,handshake.Target,candidate),
            Authorize(executionGrantId,handshake.Target,Permission.ExecuteRegisteredScenario),expected);
    }
    public async Task<object> PreflightCilAsync(string targetId,long epoch,string candidateId,string grantId,CancellationToken cancellation=default)
    {
        var candidate=CilCandidate(candidateId);
        var handshake=await ProfilerAsync(targetId,cancellation);
        var target=Target(targetId).Identity;
        await VerifyMutationAdapterAsync(targetId,candidate,handshake,cancellation);
        if(target.Epoch!=epoch) throw new LabException("stale_epoch","Preflight epoch differs.");
        if(target.Id!=candidate.SourceTarget.Id||target.StartTimeUtcTicks!=candidate.SourceTarget.StartTimeUtcTicks)
            throw new LabException("target_identity_changed","Candidate belongs to another target session.");
        var grant=MutationGrant(grantId,target,candidate);
        var body=await CandidateBodyAsync(candidate);
        string transaction=Wire.Id("preflight");
        var nativeGrant=NativeGrant(grant,candidate,transaction);
        var operation=Catalog.Accept("local",transaction,Wire.Hash(new {candidateId,epoch,grantId}),"mutation_preflight",targetId,Workspace.Policy.Id);
        try { Catalog.AcquireLease(targetId,operation.Id); }
        catch(LabException error) { Catalog.Transition(operation.Id,OperationState.Failed,error:error.Code); throw; }
        Catalog.Transition(operation.Id,OperationState.Running);
        try
        {
            Catalog.UseGrant(grant);
            var result=await profiler.SendAsync(target,MutationCommand("preflight",target,epoch,candidate,body,nativeGrant,transaction),cancellation);
            string id=Wire.Id("preflight-result"); Catalog.Put(id,"mutation_preflight",new { id,candidate_id=candidateId,candidate.ContentHash,target,result });
            Catalog.Transition(operation.Id,OperationState.Completed,id);
            return new { operation_id=operation.Id,result_id=id,candidate.ContentHash,native=result };
        }
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            Catalog.Transition(operation.Id,OperationState.Failed,error:error is LabException failure?failure.Code:"preflight_failed"); throw;
        }
        finally { Catalog.ReleaseLease(targetId,operation.Id,true); }
    }
    public async Task<Operation> ApplyCilAsync(string targetId,long epoch,string candidateId,string grantId,string executionGrantId,
        string scenarioId,string key,int setupOperations=100,int maximumDurationSeconds=30,CancellationToken cancellation=default)
    {
        if(setupOperations is <1 or >1000000||maximumDurationSeconds is <1 or >120)
            throw new LabException("resource_limit","Mutation setup budget exceeds policy.");
        var candidate=CilCandidate(candidateId);
        string hash=Wire.Hash(new { targetId,epoch,candidateId,candidate.ContentHash,grantId,executionGrantId,scenarioId,setupOperations,maximumDurationSeconds });
        if(Catalog.FindAccepted("local",key,hash) is {} previous) return previous;
        var context=await MutationContextAsync(targetId,epoch,candidate,grantId,executionGrantId,scenarioId,cancellation);
        var body=await CandidateBodyAsync(candidate);
        return Start("mutation",context.Target,key,hash,async(operation,token)=>
        {
            var root=new MutationRoot(Wire.Id("mutation"),operation.Id,candidateId,candidate.ContentHash,context.Target,scenarioId,
                grantId,executionGrantId,Wire.Id("native-tx"),setupOperations,maximumDurationSeconds,ExpectedInvocationsPerOperation:context.ExpectedInvocations);
            Catalog.Put(root.Id,"mutation",root);
            var nativeGrant=NativeGrant(context.Grant,candidate,root.NativeTransactionId);
            return await ExecuteMutationAsync(root,candidate,body,context.Grant,context.Execute,nativeGrant,operation,token,false);
        });
    }
    public async Task<Operation> RestoreCilAsync(string transactionId,long epoch,string grantId,string executionGrantId,
        string key,CancellationToken cancellation=default)
    {
        if(Catalog.Get(transactionId).Kind!="mutation") throw new LabException("invalid_request","Expected a mutation journal root.");
        var previous=Catalog.Get<MutationRoot>(transactionId);
        string hash=Wire.Hash(new { transactionId,epoch,grantId,executionGrantId });
        if(Catalog.FindAccepted("local",key,hash) is {} accepted) return accepted;
        var candidate=CilCandidate(previous.CandidateId);
        var context=await MutationContextAsync(previous.Target.Id,epoch,candidate,grantId,executionGrantId,previous.ScenarioId,cancellation);
        var body=await CandidateBodyAsync(candidate);
        return Start("mutation",context.Target,key,hash,async(operation,token)=>
        {
            var root=previous with { Id=Wire.Id("mutation"),OperationId=operation.Id,Target=context.Target,
                GrantId=grantId,ExecutionGrantId=executionGrantId,RestoresTransactionId=transactionId };
            Catalog.Put(root.Id,"mutation",root);
            var nativeGrant=NativeGrant(context.Grant,candidate,root.Id);
            return await ExecuteMutationAsync(root,candidate,body,context.Grant,context.Execute,nativeGrant,operation,token,true);
        });
    }
    private async Task<bool> ExecuteMutationAsync(MutationRoot root,CilCandidateRecord candidate,byte[] body,Grant parent,
        Grant execute,Grant nativeGrant,Operation operation,CancellationToken cancellation,bool restoreOnly,CompilationControlHooks? control=null,bool completeOperation=true)
    {
        int sequence=0; string? previousHash=null; long epoch=root.Target.Epoch; bool submitted=false,helperStarted=false;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(root.MaximumDurationSeconds));
        void Journal(string phase,object? evidence=null,string? error=null)
        {
            string id=root.Id+"-event-"+(++sequence).ToString("d4");
            var item=new MutationEvent(id,root.Id,sequence,phase,previousHash,epoch,
                evidence is null?null:JsonSerializer.SerializeToElement(evidence,Wire.Json),error,DateTimeOffset.UtcNow);
            previousHash=Catalog.Put(id,"mutation_event",item).Hash;
            mutationJournalCommitted?.Invoke(item);
        }
        async Task<JsonElement> Send(string action,CancellationToken token)=>await profiler.SendAsync(root.Target,
            MutationCommand(action,root.Target,epoch,candidate,body,nativeGrant,root.NativeTransactionId),token);
        async Task Setup(CancellationToken token)
        {
            Policies.Authorize(execute,Catalog.Root,root.Target,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow);
            Catalog.UseGrant(execute);
            var run=new Tedd.DotNetLab.Contracts.RunRequest(root.Id,Wire.Id("setup"),"mutation-setup",root.ScenarioId,"candidate",
                root.SetupOperations,0,true,false,epoch,root.Target with { Epoch=epoch },execute,
                DateTimeOffset.UtcNow.AddSeconds(root.MaximumDurationSeconds),candidate.Scope,candidate.Inliners?.Select(c=>c.Scope).ToArray());
            var result=await harness.SetupAsync(run,token);
            Journal("setup_oracle",result);
            if(!result.Correct||!result.Quiescent) throw new LabException("correctness_failed","Application-owned mutation setup oracle failed.");
            activityScheduler.AcknowledgeQuiescence(run.Target);
        }
        void Outcome(string phase,bool measurable,bool restored,bool tainted,string? failure,string? controlResultId=null)
        {
            var result=new MutationOutcome(root.Id+"-outcome",candidate.Id,phase,epoch,measurable,restored,tainted,failure,CompilationControlResultId:controlResultId);
            Catalog.Put(result.Id,"mutation_outcome",result);
            Journal("outcome_recorded",result);
            if(completeOperation) Catalog.Transition(operation.Id,tainted?OperationState.Tainted:failure is null?OperationState.Completed:OperationState.Failed,result.Id,failure);
        }
        async Task Restore(CancellationToken token)
        {
            Journal("restoring");
            if(candidate.Probe is not null&&(await harness.HandshakeAsync(root.Target,token)).ProbeHelper?.Active==true)
            {
                var stopped=await ProbeControlAsync(root.Target,epoch,candidate,parent,root.Id,"stop",token);
                helperStarted=false;
                var artifact=await Artifacts.WriteJsonAsync(operation.Id,stopped,"probe-helper/1"); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"probe_restore_evidence");
                Journal("probe_session_stopped",new {artifact_id=artifact.Id});
            }
            var response=await Send("restore",token); epoch=response.GetProperty("epoch").GetInt64FromString();
            if(!response.GetProperty("restore_api_succeeded").GetBoolean()) throw new LabException("restore_failed","Native per-method restore status was not successful.");
            VerifyCallerEvidence(response,candidate,"restored");
            Journal("restore_request_completed",response);
            await Setup(token);
            var verified=await Send("verify_mutation",token);
            VerifyCallerEvidence(verified,candidate,"restored");
            if(verified.GetProperty("pending").GetBoolean()||verified.GetProperty("tainted").GetBoolean()||
                verified.GetProperty("current_body_hash").GetString()!=candidate.OriginalIlHash)
                throw new LabException("restore_failed","Original future-call body or application health remains unverified.");
            Journal("restored",verified);
            await ProfilerAsync(root.Target.Id,token);
        }
        Journal("prepared",new { candidate.ContentHash,candidate.BodyHash,candidate.OriginalIlHash,candidate.BaseIlHash,candidate.Scope });
        try
        {
            Policies.Authorize(parent,Catalog.Root,root.Target,candidate.Probe is null?Permission.ReplaceIl:Permission.InstallProbes,DateTimeOffset.UtcNow,candidate.ContentHash,candidate.Scope);
            Catalog.UseGrant(parent); Journal("permission_checked");
            if(restoreOnly)
            {
                submitted=true;
                await Restore(deadline.Token); Outcome("restored",false,true,false,null); return true;
            }
            var preflight=await Send("preflight",deadline.Token);
            if(preflight.GetProperty("body_hash").GetString()!=candidate.BodyHash||preflight.GetProperty("method_scope").GetString()!=candidate.Scope||
                preflight.GetProperty("original_il_hash").GetString()!=candidate.OriginalIlHash||preflight.GetProperty("module_load_identity").GetString()!=candidate.ModuleLoadIdentity)
                throw new LabException("stale_candidate","Native preflight identity differs from the frozen candidate.");
            Journal("preflight_passed",preflight); Journal("quiescing");
            if(control is not null)
            {
                Journal(control.Purpose=="compilation_control"?"control_original_normal_started":control.Purpose+"_before_apply_started"); await control.BeforeApply(epoch,deadline.Token);
                Journal(control.Purpose=="compilation_control"?"control_original_normal_completed":control.Purpose+"_before_apply_completed");
            }
            if(candidate.Probe is not null)
            {
                var helper=await ProbeControlAsync(root.Target,epoch,candidate,parent,root.Id,"begin",deadline.Token);
                helperStarted=true; Journal("probe_helper_prepared",helper);
            }
            deadline.Token.ThrowIfCancellationRequested();
            // Commit submission intent before any byte can reach the native endpoint.
            Journal("request_submitted"); submitted=true;
            var receipt=await Send("apply",deadline.Token); epoch=receipt.GetProperty("epoch").GetInt64FromString();
            Journal("compilation_driving",receipt); await Setup(deadline.Token);
            if(candidate.Probe is not null)
            {
                var snapshot=await ProbeControlAsync(root.Target,epoch,candidate,parent,root.Id,"stop",deadline.Token);
                helperStarted=false;
                long entered=snapshot.GetProperty("rows").EnumerateArray().Sum(r=>r.GetProperty("entered").GetInt64FromString());
                long? expected=root.ExpectedInvocationsPerOperation is int multiplier?(long)root.SetupOperations*multiplier:null;
                if(!snapshot.GetProperty("complete").GetBoolean()||entered==0||expected is long expectedCount&&entered!=expectedCount)
                    throw new LabException("probe_incomplete","Setup probe count/coverage differs from the registered invocation fixture.");
                var artifact=await Artifacts.WriteJsonAsync(operation.Id,snapshot,"probe-helper/1"); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"probe_setup_evidence");
                Journal("probe_setup_verified",new { artifact_id=artifact.Id,expected_invocations=expected,observed_invocations=entered });
                await ProbeControlAsync(root.Target,epoch,candidate,parent,root.Id,"begin",deadline.Token); helperStarted=true;
            }
            var compiled=await profiler.SendAsync(root.Target,new ProfilerRequest(Wire.Version,"mutation_status",root.Target.Id,
                root.Target.ProcessId,root.Target.StartTimeUtcTicks,epoch),deadline.Token);
            if(compiled.GetProperty("transaction_id").GetString()!=root.NativeTransactionId||
                compiled.GetProperty("epoch").GetInt64FromString()!=epoch||
                !compiled.GetProperty("compilation_observed").GetBoolean()||compiled.GetProperty("candidate_hash").GetString()!=candidate.BodyHash||
                compiled.GetProperty("method_scope").GetString()!=candidate.Scope||compiled.GetProperty("tainted").GetBoolean())
                throw new LabException("mutation_unverified","Matching method, body, epoch, and compilation evidence are required.");
            Journal("compilation_observed",compiled);
            VerifyCallerEvidence(compiled,candidate,"compiled");
            var verified=await Send("verify_mutation",deadline.Token);
            if(verified.GetProperty("pending").GetBoolean()||!verified.GetProperty("correctness_passed").GetBoolean())
                throw new LabException("mutation_unverified","Candidate is not measurable.");
            Journal("verified",verified); Journal("correctness_passed"); Journal("measurable");
            await ProfilerAsync(root.Target.Id,deadline.Token);
            if(control is not null)
            {
                Journal(control.Purpose=="compilation_control"?"control_original_body_rejit_started":control.Purpose+"_measurement_started"); await control.AfterApply(epoch,deadline.Token);
                Journal(control.Purpose=="compilation_control"?"control_original_body_rejit_completed":control.Purpose+"_measurement_completed"); await Restore(deadline.Token);
                string resultId=await control.Completed(epoch,deadline.Token); Journal(control.Purpose+"_completed",new { result_id=resultId });
                Outcome(control.Purpose=="compilation_control"?"control_restored":"trial_arm_restored",false,true,false,null,
                    control.Purpose=="compilation_control"?resultId:null); return true;
            }
            Outcome("measurable",true,false,false,null); return true;
        }
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            string code=error is LabException failure?failure.Code:error is OperationCanceledException?"cancelled":"mutation_failed";
            if(!submitted)
            {
                if(code=="resource_limit")
                {
                    try
                    {
                        using var evidenceDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        var state=await profiler.SendAsync(root.Target,new ProfilerRequest(Wire.Version,"mutation_status",root.Target.Id,
                            root.Target.ProcessId,root.Target.StartTimeUtcTicks,epoch),evidenceDeadline.Token);
                        if(state.TryGetProperty("resource_budget",out var budget)&&budget.GetProperty("restart_required").GetBoolean())
                            Journal("resource_budget_rejected",new { resource_budget=budget,observed_at=DateTimeOffset.UtcNow },code);
                    }
                    catch(Exception evidenceError) when(evidenceError is not OutOfMemoryException)
                    { Journal("resource_budget_evidence_unavailable",error:evidenceError is LabException evidenceFailure?evidenceFailure.Code:"budget_evidence_failed"); }
                }
                if(control is not null)
                {
                    try
                    {
                        using var healthDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        var health=await harness.HandshakeAsync(root.Target,healthDeadline.Token);
                        var nativeHealth=await profiler.HandshakeAsync(root.Target,healthDeadline.Token);
                        var healthState=await profiler.SendAsync(root.Target,new ProfilerRequest(Wire.Version,"mutation_status",root.Target.Id,
                            root.Target.ProcessId,root.Target.StartTimeUtcTicks,epoch),healthDeadline.Token);
                        if(health.Tainted||nativeHealth.ExecutionActive||healthState.GetProperty("pending").GetBoolean()||healthState.GetProperty("tainted").GetBoolean())
                            throw new LabException("target_tainted","Control measurement did not establish target quiescence.");
                    }
                    catch(Exception healthError) when(healthError is not OutOfMemoryException)
                    { Journal("tainted",error:"control_quiescence_unknown"); Outcome("tainted",false,false,true,code); return false; }
                }
                if(helperStarted)
                {
                    try { using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(10)); await ProbeControlAsync(root.Target,epoch,candidate,parent,root.Id,"stop",cleanup.Token); }
                    catch(Exception cleanupError) when(cleanupError is not OutOfMemoryException) { Journal("tainted",error:"probe_cleanup_unknown"); Outcome("tainted",false,false,true,code); return false; }
                }
                Journal("rejected",error:code); Outcome("rejected",false,false,false,code); return true;
            }
            try
            {
                using var recovery=new CancellationTokenSource(TimeSpan.FromSeconds(30));
                // A lost reply must be reconciled before using an epoch or attempting restoration.
                var status=await profiler.SendAsync(root.Target,new ProfilerRequest(Wire.Version,"mutation_status",root.Target.Id,
                    root.Target.ProcessId,root.Target.StartTimeUtcTicks,epoch),recovery.Token);
                if(status.GetProperty("transaction_id").GetString()!=root.NativeTransactionId||status.GetProperty("candidate_hash").GetString()!=candidate.BodyHash||
                    status.GetProperty("method_scope").GetString()!=candidate.Scope) throw new LabException("mutation_unverified","Native transaction identity is unknown.");
                epoch=status.GetProperty("epoch").GetInt64FromString();
                await Restore(recovery.Token); Outcome("restored",false,true,false,code); return true;
            }
            catch(Exception recoveryError) when(recoveryError is not OutOfMemoryException)
            {
                Journal("tainted",error:recoveryError is LabException failed?failed.Code:"restore_unknown");
                Outcome("tainted",false,false,true,code); return false;
            }
        }
    }
}

internal static class ExactJsonInteger
{
    public static long GetInt64FromString(this JsonElement value)=>long.Parse(value.GetString()!,System.Globalization.CultureInfo.InvariantCulture);
}
