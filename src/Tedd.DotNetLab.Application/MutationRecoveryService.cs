using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Application;

public record MutationRecoveryRoot(string Id,string OperationId,string SourceTransactionId,string SourceOperationId,
    string SourceJournalHeadHash,string CandidateId,string CandidateHash,TargetIdentity Target,string GrantId,string ExecutionGrantId);

public sealed partial class LabService
{
    private (string Head,bool Submitted) VerifyMutationJournal(MutationRoot root,CilCandidateRecord candidate)
    {
        var record=Catalog.Get(root.Id);
        if(record.Kind!="mutation"||record.Revision!=1||record.Hash!=Wire.Hash(root)||root.CandidateHash!=candidate.ContentHash)
            throw new LabException("journal_invalid","Mutation root or candidate identity differs from the committed manifest.");
        var records=Catalog.RecordsByPrefix("mutation_event",root.Id+"-event-");
        string? previous=null; bool submitted=false; int sequence=0;
        foreach(var row in records)
        {
            var item=Wire.Deserialize<MutationEvent>(row.Json);
            if(row.Revision!=1||row.Hash!=Wire.Hash(item)||item.Id!=row.Id||item.TransactionId!=root.Id||
                item.Sequence!=++sequence||item.PreviousHash!=previous||row.Id!=root.Id+"-event-"+sequence.ToString("d4"))
                throw new LabException("journal_invalid","Mutation event hash chain or sequence is invalid.");
            if(sequence==1)
            {
                if(item.Phase!="prepared"||item.Evidence is not {} evidence||
                    evidence.GetProperty("content_hash").GetString()!=candidate.ContentHash||
                    evidence.GetProperty("body_hash").GetString()!=candidate.BodyHash||
                    evidence.GetProperty("original_il_hash").GetString()!=candidate.OriginalIlHash||
                    evidence.GetProperty("base_il_hash").GetString()!=candidate.BaseIlHash||
                    evidence.GetProperty("scope").GetString()!=candidate.Scope)
                    throw new LabException("journal_invalid","Prepared evidence does not match the frozen candidate.");
            }
            submitted|=item.Phase=="request_submitted"; previous=row.Hash;
        }
        if(previous is null) throw new LabException("journal_invalid","Mutation preparation is missing.");
        if(root.RestoresTransactionId is not null)
        {
            var apply=Catalog.Get<MutationRoot>(root.RestoresTransactionId);
            if(apply.RestoresTransactionId is not null||apply.NativeTransactionId!=root.NativeTransactionId||apply.CandidateHash!=root.CandidateHash||
                apply.Target.Id!=root.Target.Id||!VerifyMutationJournal(apply,candidate).Submitted)
                throw new LabException("journal_invalid","Restore journal lacks its original submitted transaction.");
            submitted=true;
        }
        return(previous,submitted);
    }
    public async Task<Operation> RecoverMutationAsync(string transactionId,long epoch,string grantId,string executionGrantId,
        string key,CancellationToken cancellation=default)
    {
        if(Catalog.Get(transactionId).Kind!="mutation") throw new LabException("invalid_request","Recovery requires an interrupted mutation root.");
        var source=Catalog.Get<MutationRoot>(transactionId); var candidate=CilCandidate(source.CandidateId);
        string hash=Wire.Hash(new { transactionId,epoch,grantId,executionGrantId });
        if(Catalog.FindAccepted("local",key,hash) is {} accepted) return accepted;
        if(Catalog.RecoverableMutationOperation(source.Target.Id)!=source.OperationId||
            Catalog.GetOperation(source.OperationId).State is not (OperationState.Interrupted or OperationState.Completed or OperationState.Failed))
            throw new LabException("target_tainted","This target has no recoverable interruption for the selected mutation.","Restart the target.");
        var journal=VerifyMutationJournal(source,candidate);
        var context=await MutationContextAsync(source.Target.Id,epoch,candidate,grantId,executionGrantId,source.ScenarioId,cancellation);
        var body=await CandidateBodyAsync(candidate);
        return Start("mutation_recovery",context.Target,key,hash,async(operation,token)=>
        {
            var root=new MutationRecoveryRoot(Wire.Id("mutation-recovery"),operation.Id,source.Id,source.OperationId,journal.Head,
                candidate.Id,candidate.ContentHash,context.Target,grantId,executionGrantId);
            Catalog.Put(root.Id,"mutation_recovery",root);
            await ExecuteRecoveryAsync(root,source,candidate,body,context.Grant,context.Execute,journal.Submitted,operation,token);
            return true;
        },source.OperationId);
    }
    private async Task ExecuteRecoveryAsync(MutationRecoveryRoot root,MutationRoot source,CilCandidateRecord candidate,byte[] body,
        Grant parent,Grant execute,bool submitted,Operation operation,CancellationToken cancellation)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(60));
        long epoch=root.Target.Epoch; int sequence=0; string? head=null;
        void Journal(string phase,object? evidence=null)
        {
            string id=root.Id+"-event-"+(++sequence).ToString("d4");
            var item=new MutationEvent(id,root.Id,sequence,phase,head,epoch,
                evidence is null?null:JsonSerializer.SerializeToElement(evidence,Wire.Json),null,DateTimeOffset.UtcNow);
            head=Catalog.Put(id,"mutation_event",item).Hash; mutationJournalCommitted?.Invoke(item);
        }
        var nativeGrant=NativeGrant(parent,candidate,root.Id);
        async Task<JsonElement> Send(string action)=>await profiler.SendAsync(root.Target,
            MutationCommand(action,root.Target,epoch,candidate,body,nativeGrant,source.NativeTransactionId),deadline.Token);
        void Bind(JsonElement state)
        {
            VerifyCallerEvidence(state,candidate,"bound");
            if(state.GetProperty("epoch").GetInt64FromString()!=epoch||state.GetProperty("selected_method_scope").GetString()!=candidate.Scope||
                state.GetProperty("original_il_hash").GetString()!=candidate.OriginalIlHash||
                state.GetProperty("module_load_identity").GetString()!=candidate.ModuleLoadIdentity||state.GetProperty("tainted").GetBoolean())
                throw new LabException("recovery_unverified","Live original body, module load, scope, epoch, or native taint differs.");
        }
        Journal("recovery_prepared",new { root.SourceJournalHeadHash,candidate.ContentHash,candidate.OriginalIlHash });
        Policies.Authorize(parent,Catalog.Root,root.Target,candidate.Probe is null?Permission.ReplaceIl:Permission.InstallProbes,
            DateTimeOffset.UtcNow,candidate.ContentHash,candidate.Scope); Catalog.UseGrant(parent);
        var native=await Send("reconcile_mutation"); Bind(native); Journal("native_reconciled",native);
        string phase;
        if(native.GetProperty("transaction_id").GetString()==source.NativeTransactionId)
        {
            if(!submitted||native.GetProperty("candidate_hash").GetString()!=candidate.BodyHash||native.GetProperty("method_scope").GetString()!=candidate.Scope)
                throw new LabException("recovery_unverified","Native activation lacks matching durable submission evidence.");
            if(candidate.Probe is not null&&(await harness.HandshakeAsync(root.Target,deadline.Token)).ProbeHelper is {Active:true} helper)
            {
                if(helper.ActivePlanHash!=candidate.ContentHash||helper.MethodScope!=candidate.Scope)
                    throw new LabException("observer_mismatch","Active helper belongs to another plan.");
                var snapshot=await ProbeControlAsync(root.Target,epoch,candidate,parent,root.Id,"stop",deadline.Token);
                var stopped=await Artifacts.WriteJsonAsync(operation.Id,snapshot,"probe-helper/1"); Upsert(stopped.Id,"artifact",stopped); Catalog.Pin(stopped.Id,"recovery_probe_evidence");
                Journal("probe_session_stopped",new { artifact_id=stopped.Id });
            }
            Journal("recovery_restore_submitted");
            var receipt=await Send("restore"); epoch=receipt.GetProperty("epoch").GetInt64FromString();
            if(!receipt.GetProperty("restore_api_succeeded").GetBoolean()) throw new LabException("restore_failed","Per-method original restore failed.");
            VerifyCallerEvidence(receipt,candidate,"restored");
            Journal("restore_request_completed",receipt); phase="restored";
        }
        else
        {
            if(native.GetProperty("transaction_id").GetString()!=""||native.GetProperty("pending").GetBoolean()||
                epoch!=source.Target.Epoch||candidate.BaseIlHash!=candidate.OriginalIlHash||native.GetProperty("current_body_hash").GetString()!=candidate.OriginalIlHash)
                throw new LabException("recovery_unverified","Unknown activation cannot be declared unapplied or restored.");
            var helper=(await harness.HandshakeAsync(root.Target,deadline.Token)).ProbeHelper;
            if(helper is {Active:true})
            {
                if(candidate.Probe is null||helper.ActivePlanHash!=candidate.ContentHash||helper.MethodScope!=candidate.Scope)
                    throw new LabException("observer_mismatch","Unexpected active helper prevents recovery.");
                await ProbeControlAsync(root.Target,epoch,candidate,parent,root.Id,"stop",deadline.Token);
            }
            phase="not_applied"; Journal("submission_not_observed",native);
        }
        Policies.Authorize(execute,Catalog.Root,root.Target,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow); Catalog.UseGrant(execute);
        var run=new Tedd.DotNetLab.Contracts.RunRequest(root.Id,Wire.Id("recovery-setup"),"recovery-setup",source.ScenarioId,"candidate",
            source.SetupOperations,0,true,false,epoch,root.Target with { Epoch=epoch },execute,DateTimeOffset.UtcNow.AddSeconds(30),candidate.Scope,
            candidate.Inliners?.Select(c=>c.Scope).ToArray());
        var validation=await harness.SetupAsync(run,deadline.Token);
        if(!validation.Correct||!validation.Quiescent) throw new LabException("restore_failed","Original scenario health remains unverified.");
        activityScheduler.AcknowledgeQuiescence(run.Target);
        Journal("recovery_health_verified",validation);
        if(phase=="restored")
        {
            var verified=await Send("verify_mutation");
            if(verified.GetProperty("pending").GetBoolean()||verified.GetProperty("tainted").GetBoolean()||
                !verified.GetProperty("restore_api_succeeded").GetBoolean()||!verified.GetProperty("correctness_passed").GetBoolean())
                throw new LabException("restore_failed","Native restoration and application-health verification did not complete.");
        }
        native=await Send("reconcile_mutation"); Bind(native);
        if(native.GetProperty("current_body_hash").GetString()!=candidate.OriginalIlHash||native.GetProperty("pending").GetBoolean())
            throw new LabException("restore_failed","Original future-call body remains unverified.");
        var manifest=await Artifacts.WriteJsonAsync(operation.Id,new { target=root.Target with {Epoch=epoch},root.SourceTransactionId,
            root.SourceJournalHeadHash,candidate.ContentHash,candidate.OriginalIlHash,phase,native },"mutation-recovery/1");
        Upsert(manifest.Id,"artifact",manifest); Catalog.Pin(manifest.Id,"mutation_recovery_evidence");
        var health=await Artifacts.WriteJsonAsync(operation.Id,new { request=run,response=validation },"harness-health/1");
        Upsert(health.Id,"artifact",health); Catalog.Pin(health.Id,"mutation_recovery_evidence");
        await ProfilerAsync(root.Target.Id,deadline.Token);
        Journal("recovery_verified",new { phase,native_manifest_artifact_id=manifest.Id,validation_artifact_id=health.Id });
        var result=new MutationRecoveryResult(root.Id+"-result",operation.Id,source.OperationId,root.Target with {Epoch=epoch},
            source.NativeTransactionId,candidate.ContentHash,candidate.OriginalIlHash,candidate.Scope,candidate.ModuleLoadIdentity,phase,
            root.SourceJournalHeadHash,manifest.Id,health.Id,true,true);
        Catalog.Put(result.Id,"mutation_recovery_result",result);
        Catalog.CompleteMutationRecovery(root.Target.Id,operation.Id,source.OperationId,result.Id);
        Journal("recovery_committed",new { result_id=result.Id });
    }
}
