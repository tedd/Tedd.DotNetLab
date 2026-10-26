using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Application;

public record NativeTrialBlock(int Ordinal,IReadOnlyList<string> Order);
public record NativeTrialPlan(string Id,TargetIdentity Target,FrozenRecordReference Original,FrozenRecordReference Candidate,
    CompilationControlBinding CompilationControl,string ScenarioId,string ScenarioVersion,string InputHash,string HarnessVariant,
    int OperationsPerRun,int WarmupBatches,int MaximumDurationSeconds,ulong Seed,IReadOnlyList<NativeTrialBlock> Blocks,DateTimeOffset FrozenAt);
public record NativeTrialRun(string Id,string PlanId,string PlanHash,int BlockOrdinal,int ArmOrdinal,string TransactionId,
    string CandidateId,TargetIdentity Target,RuntimeFootprint Runtime,Measurement Measurement,CompilationRegimeEvidence Compilation,
    string ArtifactId,string CodeBeforeArtifactId,string CodeAfterArtifactId,long RestoredEpoch);
public record NativeTrialResult(string Id,string PlanId,string PlanHash,string OperationId,IReadOnlyList<string> RunIds,
    long RestoredEpoch,double GeometricCandidateToOriginalRatio,string ArtifactId,int IndependentSessions=1,
    string Quality="descriptive_only",bool EquivalencePassed=false,bool PromotionEligible=false,
    string Meaning="Randomized matched body transitions in one process session; inner blocks do not establish independent sampling or reset tiering/PGO history.");

public sealed partial class LabService
{
    public async Task<NativeTrialPlan> CreateNativeTrialAsync(string candidateId,string compilationControlId,int blocks=10,ulong seed=42,int maximumDurationSeconds=120)
    {
        if(blocks is <1 or >15||maximumDurationSeconds is <1 or >600||maximumDurationSeconds>Limits.MaximumDurationSeconds)
            throw new LabException("resource_limit","Native trial exceeds its bounded session or duration budget.");
        var binding=ControlBinding(compilationControlId); var control=await VerifyCompilationControlAsync(binding,CancellationToken.None);
        var candidate=CilCandidate(candidateId); await CandidateBodyAsync(candidate);
        var original=control.Candidate;
        if(candidate.Probe is not null||candidate.BodyHash==original.OriginalIlHash||candidate.BaseIlHash!=original.OriginalIlHash||
            candidate.OriginalIlHash!=original.OriginalIlHash||candidate.Scope!=original.Scope||candidate.ModuleLoadIdentity!=original.ModuleLoadIdentity||
            candidate.SourceTarget.Id!=original.SourceTarget.Id||Wire.Hash(candidate.Inliners??[])!=Wire.Hash(original.Inliners??[]))
            throw new LabException("control_mismatch","Trial requires a changed compatible body for the control's exact original method, load, caller closure, and target session.");
        var handshake=await ScenariosAsync(original.SourceTarget.Id,CancellationToken.None);
        var state=await profiler.SendAsync(handshake.Target,new ProfilerRequest(Wire.Version,"mutation_status",handshake.Target.Id,
            handshake.Target.ProcessId,handshake.Target.StartTimeUtcTicks,handshake.Target.Epoch),CancellationToken.None);
        if(handshake.Tainted||handshake.Runtime is null||handshake.Target.Epoch!=control.Result.RestoredEpoch||
            state.GetProperty("mode").GetString()!="restored_original_version"||state.GetProperty("pending").GetBoolean()||state.GetProperty("tainted").GetBoolean())
            throw new LabException("invalid_plan","Freeze a trial in the healthy restored session immediately following its control.");
        if(state.GetProperty("mutation_count").GetInt32()+2*blocks>32)
            throw new LabException("resource_limit","Frozen trial would exceed the native mutation budget; use a new controlled session.");
        var record=Catalog.Get(candidate.Id); var random=new DeterministicRandom(seed);
        var plan=new NativeTrialPlan(Wire.Id("native-trial-plan"),handshake.Target,control.Plan.Candidate,new(record.Id,record.Revision,record.Hash),binding,
            control.Plan.ScenarioId,control.Plan.ScenarioVersion,control.Plan.InputHash,control.Plan.HarnessVariant,control.Plan.OperationsPerRun,
            control.Plan.WarmupBatches,maximumDurationSeconds,seed,Enumerable.Range(0,blocks).Select(i=>new NativeTrialBlock(i,
                random.Shuffle(new[]{"original_body_rejit","candidate_rejit"}))).ToArray(),DateTimeOffset.UtcNow);
        Catalog.Put(plan.Id,"native_trial_plan",plan); return plan;
    }
    private CilCandidateRecord TrialCandidate(FrozenRecordReference reference)
    {
        var record=Catalog.Get(reference.Id);
        if(record.Revision!=reference.Revision||record.Hash!=reference.Hash) throw new LabException("stale_candidate","Native trial candidate differs from its frozen reference.");
        return CilCandidate(reference.Id);
    }
    private static ExperimentPlan NativeCompilationPlan(NativeTrialPlan plan)=>new(plan.Id,plan.ScenarioId,plan.ScenarioVersion,plan.InputHash,
        [plan.HarnessVariant],plan.Blocks.Count,plan.OperationsPerRun,plan.WarmupBatches,plan.Seed,"quiet-validation",false,"One live native session.",false,
        plan.MaximumDurationSeconds,new(),CompilationControl:plan.CompilationControl);
    private static void ValidateTrialSnapshot(NativeSnapshotReply snapshot,CilCandidateRecord selected,TargetIdentity target,CompilationRegimeEvidence compilation)
    {
        if(snapshot.CodeRewrittenForInspection||snapshot.Perturbation.RuntimeSuspended||!snapshot.Perturbation.WorkerRegisteredWithRuntime||
            snapshot.OriginalBodyHash!=selected.OriginalIlHash||!snapshot.Snapshots.Any(s=>s.CurrentIlHash==selected.BodyHash&&s.RejitId==compilation.RejitId)||
            snapshot.Snapshots.Any(s=>s.TargetId!=target.Id||s.Epoch!=target.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)||
                s.RuntimeVersion!=target.RuntimeVersion||s.Signature!=selected.Method.SignatureHex||s.ModuleMvid!=selected.Method.ModuleMvid||
                s.ModuleHash!=selected.Method.ModuleHash||s.ModuleLoadIdentity!=selected.ModuleLoadIdentity||s.MethodToken!=selected.Method.Token))
            throw new LabException("invalid_snapshot","Trial snapshot lacks the selected active body/version or exact method identity.");
        foreach(var version in snapshot.Snapshots) NativeDecoder.Validate(version);
    }
    public async Task<NativeTrialResult> VerifyNativeTrialAsync(string resultId,CancellationToken cancellation=default)
    {
        var result=Record<NativeTrialResult>(resultId,"native_trial_result"); var plan=Record<NativeTrialPlan>(result.PlanId,"native_trial_plan");
        var operation=Catalog.GetOperation(result.OperationId); var artifact=Record<Artifact>(result.ArtifactId,"artifact");
        if(result.PlanHash!=Wire.Hash(plan)||operation.State!=OperationState.Completed||operation.ResultId!=result.Id||result.RunIds.Count!=2*plan.Blocks.Count||
            result.RunIds.Distinct().Count()!=result.RunIds.Count||result.IndependentSessions!=1||result.Quality!="descriptive_only"||result.EquivalencePassed||result.PromotionEligible)
            throw new LabException("trial_unverified","Native trial does not complete its frozen single-session budget.");
        if(artifact.Length>67108864) throw new LabException("resource_limit","Native trial manifest exceeds its read budget.");
        using var report=JsonDocument.Parse(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact,cancellation),cancellation));
        var rows=report.RootElement.GetProperty("runs").Deserialize<NativeTrialRun[]>(Wire.Json)!;
        if(Wire.Hash(report.RootElement.GetProperty("plan").Deserialize<NativeTrialPlan>(Wire.Json))!=Wire.Hash(plan)||
            !rows.Select(r=>r.Id).SequenceEqual(result.RunIds)||report.RootElement.GetProperty("restored_epoch").GetInt64FromString()!=result.RestoredEpoch)
            throw new LabException("artifact_corrupt","Native trial report differs from its frozen plan or result.");
        var control=await VerifyCompilationControlAsync(plan.CompilationControl,cancellation);
        var original=TrialCandidate(plan.Original); var candidate=TrialCandidate(plan.Candidate); long priorRestoredEpoch=plan.Target.Epoch;
        foreach(var row in rows)
        {
            if(Wire.Hash(Record<NativeTrialRun>(row.Id,"native_trial_run"))!=Wire.Hash(row)||row.PlanId!=plan.Id||row.PlanHash!=result.PlanHash||
                row.BlockOrdinal<0||row.BlockOrdinal>=plan.Blocks.Count||row.ArmOrdinal is <0 or >1||
                row.Compilation.Regime!=plan.Blocks[row.BlockOrdinal].Order[row.ArmOrdinal])
                throw new LabException("artifact_corrupt","Native trial run identity or frozen arm order differs.");
            var selected=row.Compilation.Regime=="original_body_rejit"?original:candidate; var root=Record<MutationRoot>(row.TransactionId,"mutation");
            var outcome=Record<MutationOutcome>(root.Id+"-outcome","mutation_outcome"); VerifyMutationJournal(root,selected);
            if(root.OperationId!=operation.Id||root.NativeTrialPlanId!=plan.Id||root.CandidateId!=selected.Id||root.Target!=plan.Target with { Epoch=priorRestoredEpoch }||
                outcome.Failure is not null||!outcome.Restored||outcome.Tainted||outcome.Epoch!=row.RestoredEpoch||
                row.CandidateId!=selected.Id||row.Target!=plan.Target with { Epoch=priorRestoredEpoch+1 }||row.RestoredEpoch!=row.Target.Epoch+1||
                row.Compilation.BodyHash!=selected.BodyHash||row.Compilation.Epoch!=row.Target.Epoch||row.Compilation.CompilationControlId!=plan.CompilationControl.ResultId||
                row.Compilation.MethodScope!=selected.Scope||row.Compilation.CallerBindingsHash!=Wire.Hash(selected.Inliners??[])||row.Compilation.RequestFlags!=3||
                row.Measurement.RunId!=row.Id||row.Measurement.ExecutionId!=operation.Id||row.Measurement.CompletedOperations!=plan.OperationsPerRun||
                row.Measurement.InputHash!=plan.InputHash||row.Measurement.ScenarioVersion!=plan.ScenarioVersion||row.Measurement.Variant!=plan.HarnessVariant||
                !row.Measurement.Correct||!row.Measurement.Quiescent)
                throw new LabException("trial_unverified","Native trial lacks matching body, completed work, or verified per-arm restoration.");
            var measurement=Record<Artifact>(row.ArtifactId,"artifact");
            if(measurement.Length>1048576||Wire.Hash(Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(measurement,cancellation),cancellation)))!=Wire.Hash(row.Measurement))
                throw new LabException("artifact_corrupt","Native trial measurement differs from sealed evidence.");
            foreach(string codeId in new[]{row.CodeBeforeArtifactId,row.CodeAfterArtifactId})
            {
                var code=Record<Artifact>(codeId,"artifact"); if(code.Length>4194304) throw new LabException("resource_limit","Trial code snapshot exceeds its read budget.");
                var snapshot=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(code,cancellation),cancellation));
                ValidateTrialSnapshot(snapshot,selected,row.Target,row.Compilation);
            }
            priorRestoredEpoch=row.RestoredEpoch;
        }
        if(rows.Any(r=>Wire.Hash(r.Runtime)!=control.RuntimeHash)||rows.Select(r=>(r.BlockOrdinal,r.ArmOrdinal)).Distinct().Count()!=rows.Length||priorRestoredEpoch!=result.RestoredEpoch)
            throw new LabException("trial_unverified","Native trial session/runtime or complete matched blocks differ.");
        double ratio=Math.Exp(StatisticalEngine.Mean(plan.Blocks.Select(block=>Math.Log(rows.Single(r=>r.BlockOrdinal==block.Ordinal&&r.Compilation.Regime=="candidate_rejit").Measurement.NanosecondsPerOperation)-
            Math.Log(rows.Single(r=>r.BlockOrdinal==block.Ordinal&&r.Compilation.Regime=="original_body_rejit").Measurement.NanosecondsPerOperation)).ToArray()));
        if(ratio!=result.GeometricCandidateToOriginalRatio||ratio!=report.RootElement.GetProperty("geometric_candidate_to_original_ratio").GetDouble())
            throw new LabException("artifact_corrupt","Native trial descriptive estimate differs from its complete retained inputs.");
        return result;
    }
    public async Task<Operation> StartNativeTrialAsync(string planId,string originalGrantId,string candidateGrantId,string executionGrantId,
        string inspectionGrantId,string key,CancellationToken cancellation=default)
    {
        var plan=Record<NativeTrialPlan>(planId,"native_trial_plan");
        if(string.IsNullOrWhiteSpace(key)||key.Length>128) throw new LabException("invalid_request","A bounded idempotency key is required.");
        string hash=Wire.Hash(new { planId,plan_hash=Wire.Hash(plan),originalGrantId,candidateGrantId,executionGrantId,inspectionGrantId,key });
        // One acceptance per frozen sampling budget; a different key cannot produce an extra selectable trial.
        string acceptance="native-trial-"+plan.Id;
        if(Catalog.FindAccepted("local",acceptance,hash) is { } replay) return replay;
        var control=await VerifyCompilationControlAsync(plan.CompilationControl,cancellation);
        var original=TrialCandidate(plan.Original); var candidate=TrialCandidate(plan.Candidate);
        var context=await MutationContextAsync(plan.Target.Id,plan.Target.Epoch,candidate,candidateGrantId,executionGrantId,plan.ScenarioId,cancellation);
        var originalGrant=MutationGrant(originalGrantId,context.Target,original); var inspect=Authorize(inspectionGrantId,context.Target,Permission.InspectLiveCode);
        var handshake=await harness.HandshakeAsync(context.Target,cancellation); var scenario=handshake.Scenarios.Single(s=>s.Id==plan.ScenarioId);
        if(context.Target!=plan.Target||handshake.Runtime is null||Wire.Hash(handshake.Runtime)!=control.RuntimeHash||
            scenario.Version!=plan.ScenarioVersion||scenario.InputHash!=plan.InputHash||!scenario.Variants.Contains(plan.HarnessVariant)||handshake.ProbeHelper?.Active==true)
            throw new LabException("control_mismatch","Frozen native trial session, workload, or observer changed.");
        var initialState=await profiler.SendAsync(context.Target,new ProfilerRequest(Wire.Version,"mutation_status",context.Target.Id,context.Target.ProcessId,
            context.Target.StartTimeUtcTicks,context.Target.Epoch),cancellation);
        if(initialState.GetProperty("mode").GetString()!="restored_original_version"||initialState.GetProperty("pending").GetBoolean()||initialState.GetProperty("tainted").GetBoolean())
            throw new LabException("mutation_unverified","Trial must start from the verified restored original body.");
        var originalBody=await CandidateBodyAsync(original); var candidateBody=await CandidateBodyAsync(candidate);
        return Start("mutation",context.Target,acceptance,hash,async(operation,token)=>
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
            var runs=new List<NativeTrialRun>(); long epoch=plan.Target.Epoch; bool quiescent=true;
            foreach(var block in plan.Blocks)
            for(int arm=0;arm<block.Order.Count;arm++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                string regime=block.Order[arm]; var selected=regime=="original_body_rejit"?original:candidate;
                var grant=regime=="original_body_rejit"?originalGrant:context.Grant; var body=regime=="original_body_rejit"?originalBody:candidateBody;
                var root=new MutationRoot(Wire.Id("mutation"),operation.Id,selected.Id,selected.ContentHash,plan.Target with { Epoch=epoch },plan.ScenarioId,
                    grant.Id,context.Execute.Id,Wire.Id("native-trial-tx"),plan.OperationsPerRun,plan.MaximumDurationSeconds,
                    ExpectedInvocationsPerOperation:context.ExpectedInvocations,NativeTrialPlanId:plan.Id);
                Catalog.Put(root.Id,"mutation",root); Measurement? measurement=null; CompilationRegimeEvidence? compilation=null;
                string? artifactId=null,codeBefore=null,codeAfter=null; RuntimeFootprint? runtime=null; long measuredEpoch=0;
                async Task<string> Code(long activeEpoch,CancellationToken ct)
                {
                    var target=plan.Target with { Epoch=activeEpoch };
                    var snapshot=await AcquireNativeSnapshotAsync(target,new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,
                        start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=activeEpoch,grant=inspect,
                        selector=new { module_mvid=selected.Method.ModuleMvid,method_token=selected.Method.Token,module_hash=selected.Method.ModuleHash,
                            signature=selected.Method.SignatureHex,module_load_id=selected.ModuleLoadIdentity } },inspect,root.Id,ct);
                    ValidateTrialSnapshot(snapshot,selected,target,compilation!);
                    var artifact=await Artifacts.WriteJsonAsync(operation.Id,snapshot,"native-trial-code/v1",4194304,ct); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"native_trial_code");
                    return artifact.Id;
                }
                async Task Measure(long activeEpoch,CancellationToken ct)
                {
                    measuredEpoch=activeEpoch; var target=plan.Target with { Epoch=activeEpoch }; var before=await harness.HandshakeAsync(target,ct);
                    runtime=before.Runtime;
                    compilation=await CompilationRegimeAsync(target,runtime,NativeCompilationPlan(plan),scenario,control,ct);
                    if(compilation?.Regime!=regime||compilation.BodyHash!=selected.BodyHash) throw new LabException("mutation_unverified","Trial arm is not the intended active body.");
                    codeBefore=await Code(activeEpoch,ct);
                    Policies.Authorize(context.Execute,Catalog.Root,target,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow); Catalog.UseGrant(context.Execute);
                    string runId=Wire.Id("native-trial-run"),blockId=plan.Id+"-block-"+block.Ordinal;
                    measurement=await ExecuteMeasuredAsync(new Tedd.DotNetLab.Contracts.RunRequest(operation.Id,runId,blockId,plan.ScenarioId,plan.HarnessVariant,
                        plan.OperationsPerRun,plan.WarmupBatches,true,false,activeEpoch,target,context.Execute,DateTimeOffset.UtcNow.AddSeconds(plan.MaximumDurationSeconds)),ct);
                    Policies.Validate(measurement);
                    if(measurement.ExecutionId!=operation.Id||measurement.RunId!=runId||measurement.BlockId!=blockId||measurement.Variant!=plan.HarnessVariant||
                        !measurement.Correct||!measurement.Quiescent||measurement.CompletedOperations!=plan.OperationsPerRun||
                        measurement.InputHash!=plan.InputHash||measurement.ScenarioVersion!=plan.ScenarioVersion)
                        throw new LabException("correctness_failed","Trial measurement violates its frozen work or correctness contract.");
                    var after=await harness.HandshakeAsync(target,ct);
                    if(after.Target!=target||after.Tainted||Wire.Hash(after.Runtime)!=Wire.Hash(runtime)||
                        Wire.Hash(await CompilationRegimeAsync(target,after.Runtime,NativeCompilationPlan(plan),scenario,control,ct))!=Wire.Hash(compilation))
                        throw new LabException("not_comparable","Trial body, runtime, observer, or quiescence changed during measurement.");
                    codeAfter=await Code(activeEpoch,ct);
                    var artifact=await Artifacts.WriteJsonAsync(operation.Id,measurement,"native-trial-measurement/v1",cancellation:ct); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"native_trial_measurement"); artifactId=artifact.Id;
                }
                Task<string> Retain(long restoredEpoch,CancellationToken ct)
                {
                    var run=new NativeTrialRun(measurement!.RunId,plan.Id,Wire.Hash(plan),block.Ordinal,arm,root.Id,selected.Id,plan.Target with { Epoch=measuredEpoch },
                        runtime!,measurement,compilation!,artifactId!,codeBefore!,codeAfter!,restoredEpoch);
                    Catalog.Put(run.Id,"native_trial_run",run); runs.Add(run); return Task.FromResult(run.Id);
                }
                quiescent=await ExecuteMutationAsync(root,selected,body,grant,context.Execute,NativeGrant(grant,selected,root.NativeTransactionId),operation,deadline.Token,false,
                    new((_,_)=>Task.CompletedTask,Measure,Retain,"native_trial"),completeOperation:false);
                var outcome=Record<MutationOutcome>(root.Id+"-outcome","mutation_outcome");
                if(outcome.Failure is not null||!outcome.Restored||outcome.Tainted)
                {
                    Catalog.Transition(operation.Id,outcome.Tainted?OperationState.Tainted:OperationState.Failed,outcome.Id,outcome.Failure??"restore_unverified"); return quiescent;
                }
                epoch=outcome.Epoch;
            }
            var ratios=plan.Blocks.Select(block=>Math.Log(runs.Single(r=>r.BlockOrdinal==block.Ordinal&&r.Compilation.Regime=="candidate_rejit").Measurement.NanosecondsPerOperation)-
                Math.Log(runs.Single(r=>r.BlockOrdinal==block.Ordinal&&r.Compilation.Regime=="original_body_rejit").Measurement.NanosecondsPerOperation)).ToArray();
            double ratio=Math.Exp(StatisticalEngine.Mean(ratios));
            if(!double.IsFinite(ratio)||ratio<=0) throw new LabException("invalid_measurement","Trial descriptive ratio exceeds the finite range.");
            string id=Wire.Id("native-trial-result"); var report=await Artifacts.WriteJsonAsync(operation.Id,new { plan,runs,geometric_candidate_to_original_ratio=ratio,
                restored_epoch=epoch,independent_sessions=1,quality="descriptive_only" },"native-trial/v1",67108864,deadline.Token);
            Upsert(report.Id,"artifact",report); Catalog.Pin(report.Id,"native_trial_result");
            var result=new NativeTrialResult(id,plan.Id,Wire.Hash(plan),operation.Id,runs.Select(r=>r.Id).ToArray(),epoch,ratio,report.Id);
            Catalog.Put(result.Id,"native_trial_result",result); Catalog.Transition(operation.Id,OperationState.Completed,result.Id); return quiescent;
        });
    }
}
