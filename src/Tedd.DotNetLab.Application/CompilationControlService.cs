using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Application;

public record CompilationControlPlan(string Id,FrozenRecordReference Candidate,TargetIdentity Target,string ScenarioId,string ScenarioVersion,string InputHash,
    string HarnessVariant,int Blocks,int OperationsPerRun,int WarmupBatches,int MaximumDurationSeconds,DateTimeOffset FrozenAt,int RejitRequestFlags=3,
    string CallerPolicy="frozen-startup-native-inliner-closure/v1",string Order="all_original_normal_then_original_body_rejit",
    string Meaning="One process session; fixed phase order does not establish independent blocks or reset tiering/PGO history.");
public record CompilationControlMeasurement(string Id,string PlanId,string PlanHash,string Regime,int BlockOrdinal,long Epoch,Measurement Measurement,
    RuntimeFootprint Runtime,string ArtifactId,string CodeBeforeArtifactId,string CodeAfterArtifactId,int RejitRequestFlags,string CallerPolicy);
public record CompilationControlResult(string Id,string PlanId,string PlanHash,string TransactionId,string CandidateHash,long RestoredEpoch,
    int MeasuredRuns,int IndependentSessions,double GeometricRejitToNormalRatio,string ArtifactId,bool ProtocolVerified,bool RestorationVerified,
    string Quality="descriptive_only",bool EquivalencePassed=false,bool PromotionEligible=false,
    string Meaning="A/A original-body control with distinct observed compilation regimes; no confidence interval, equivalence, PGO reset, or source-promotion claim.");

public sealed partial class LabService
{
    private sealed record CompilationControlHooks(Func<long,CancellationToken,Task> BeforeApply,Func<long,CancellationToken,Task> AfterApply,
        Func<long,CancellationToken,Task<string>> Completed,string Purpose="compilation_control");
    public async Task<CilCandidateRecord> PrepareOriginalBodyControlAsync(string inspectionId)
    {
        var inspection=Record<JsonElement>(inspectionId,"inspection");
        if(inspection.GetProperty("qualification").GetString()!="verified_live_acquisition") throw new LabException("invalid_request","A control requires a verified live original body.");
        var source=Record<Artifact>(inspection.GetProperty("manifest_artifact_id").GetString()!,"artifact");
        var snapshot=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(source)));
        if(snapshot.Snapshots.Count==0||snapshot.Snapshots.Any(s=>s.RejitId!="0"||s.CurrentIlHash!=snapshot.OriginalBodyHash)||snapshot.CodeRewrittenForInspection)
            throw new LabException("invalid_plan","Original-normal control must be selected before any ReJIT body exists.");
        var artifact=await Artifacts.WriteJsonAsync(Wire.Id("original-control"),new CilInput(Wire.Version,snapshot.OriginalBody,"Byte-identical original IL for compilation-regime A/A control."),"original-body-control/v1");
        Upsert(artifact.Id,"artifact",artifact);
        return await RegisterCilAsync(artifact.Id,inspectionId,snapshot.OriginalBodyHash,[]);
    }
    public async Task<CompilationControlPlan> CreateCompilationControlAsync(string candidateId,string scenarioId,string harnessVariant="baseline",int blocks=10,
        int operationsPerRun=1000,int warmupBatches=1,int maximumDurationSeconds=60)
    {
        var candidate=CilCandidate(candidateId); var body=await CandidateBodyAsync(candidate);
        if(candidate.Probe is not null||candidate.BodyHash!=candidate.OriginalIlHash||candidate.BaseIlHash!=candidate.OriginalIlHash||candidate.SourceTarget.Epoch!=0||
            Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body))!=candidate.OriginalIlHash)
            throw new LabException("invalid_plan","A compilation control requires the exact original IL and original-normal startup epoch.");
        if(blocks is <1 or >100||operationsPerRun is <1 or >1000000||warmupBatches is <1 or >1000||maximumDurationSeconds is <1 or >120||maximumDurationSeconds>Limits.MaximumDurationSeconds)
            throw new LabException("resource_limit","Compilation-control sampling or duration exceeds policy.");
        var handshake=await ScenariosAsync(candidate.SourceTarget.Id,CancellationToken.None);
        var scenario=handshake.Scenarios.SingleOrDefault(s=>s.Id==scenarioId);
        if(handshake.Target.Epoch!=0||handshake.Tainted||handshake.ProbeHelper?.Active==true||handshake.Runtime is null||scenario is null||scenario.IsAsync||
            scenario.Affinity!=ThreadAffinity.Any||!scenario.Variants.Contains(harnessVariant)||!(scenario.MutationScopes?.Contains(candidate.Scope)??false)||
            candidate.Inliners?.Any(c=>!(scenario.MutationScopes?.Contains(c.Scope)??false))==true)
            throw new LabException("scenario_scope_mismatch","Control requires a quiescent original-normal target and registered synchronous workload covering every method/caller.");
        var record=Catalog.Get(candidateId);
        var plan=new CompilationControlPlan(Wire.Id("compilation-control-plan"),new(candidateId,record.Revision,record.Hash),handshake.Target,scenarioId,
            scenario.Version,scenario.InputHash,harnessVariant,blocks,operationsPerRun,warmupBatches,maximumDurationSeconds,DateTimeOffset.UtcNow);
        Catalog.Put(plan.Id,"compilation_control_plan",plan); return plan;
    }
    public async Task<Operation> StartCompilationControlAsync(string planId,string mutationGrantId,string executionGrantId,string inspectionGrantId,string key,
        CancellationToken cancellation=default)
    {
        var plan=Record<CompilationControlPlan>(planId,"compilation_control_plan"); var candidateRecord=Catalog.Get(plan.Candidate.Id);
        if(candidateRecord.Revision!=plan.Candidate.Revision||candidateRecord.Hash!=plan.Candidate.Hash)
            throw new LabException("stale_candidate","Control candidate differs from its frozen reference.");
        var candidate=CilCandidate(plan.Candidate.Id); var body=await CandidateBodyAsync(candidate);
        string hash=Wire.Hash(new { planId,plan_hash=Wire.Hash(plan),mutationGrantId,executionGrantId,inspectionGrantId });
        if(Catalog.FindAccepted("local",key,hash) is { } replay) return replay;
        var context=await MutationContextAsync(plan.Target.Id,plan.Target.Epoch,candidate,mutationGrantId,executionGrantId,plan.ScenarioId,cancellation);
        var inspect=Authorize(inspectionGrantId,context.Target,Permission.InspectLiveCode);
        var handshake=await harness.HandshakeAsync(context.Target,cancellation);
        var scenario=handshake.Scenarios.Single(s=>s.Id==plan.ScenarioId);
        var status=await profiler.SendAsync(context.Target,new ProfilerRequest(Wire.Version,"mutation_status",context.Target.Id,context.Target.ProcessId,context.Target.StartTimeUtcTicks,0),cancellation);
        if(context.Target!=plan.Target||scenario.Version!=plan.ScenarioVersion||scenario.InputHash!=plan.InputHash||status.GetProperty("mutation_count").GetInt32()!=0||
            status.GetProperty("pending").GetBoolean()||status.GetProperty("tainted").GetBoolean()||handshake.Runtime is null||handshake.ProbeHelper?.Active==true)
            throw new LabException("invalid_plan","Control must start in its frozen original-normal session before any mutation.");
        return Start("mutation",context.Target,key,hash,async(operation,token)=>
        {
            var root=new MutationRoot(Wire.Id("mutation"),operation.Id,candidate.Id,candidate.ContentHash,context.Target,plan.ScenarioId,mutationGrantId,executionGrantId,
                Wire.Id("native-control"),plan.OperationsPerRun,plan.MaximumDurationSeconds,ExpectedInvocationsPerOperation:context.ExpectedInvocations,CompilationControlPlanId:plan.Id);
            Catalog.Put(root.Id,"mutation",root); var measurements=new List<CompilationControlMeasurement>();
            async Task<string> Code(long epoch,CancellationToken ct)
            {
                var target=plan.Target with { Epoch=epoch };
                var snapshot=await AcquireNativeSnapshotAsync(target,new { schema_version=Wire.Version,action="snapshot",target_id=target.Id,process_id=target.ProcessId,
                    start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=epoch,grant=inspect,
                    selector=new { module_mvid=candidate.Method.ModuleMvid,method_token=candidate.Method.Token,module_hash=candidate.Method.ModuleHash,
                        signature=candidate.Method.SignatureHex,module_load_id=candidate.ModuleLoadIdentity } },inspect,root.Id,ct);
                if(snapshot.Snapshots.Count==0||snapshot.CodeRewrittenForInspection||snapshot.Perturbation.RuntimeSuspended||!snapshot.Perturbation.WorkerRegisteredWithRuntime||
                    snapshot.OriginalBodyHash!=candidate.OriginalIlHash||snapshot.Snapshots.Any(s=>s.TargetId!=target.Id||s.RuntimeVersion!=target.RuntimeVersion||
                    s.Epoch!=epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)||s.CurrentIlHash!=candidate.OriginalIlHash||s.Signature!=candidate.Method.SignatureHex||
                    s.ModuleMvid!=candidate.Method.ModuleMvid||s.ModuleHash!=candidate.Method.ModuleHash||s.ModuleLoadIdentity!=candidate.ModuleLoadIdentity||s.MethodToken!=candidate.Method.Token))
                    throw new LabException("invalid_snapshot","Control code identity differs from the original method and load.");
                foreach(var version in snapshot.Snapshots) NativeDecoder.Validate(version);
                var artifact=await Artifacts.WriteJsonAsync(operation.Id,snapshot,"compilation-control-code/v1",4194304,ct); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"compilation_control_code");
                return artifact.Id;
            }
            async Task Phase(string regime,long epoch,CancellationToken ct)
            {
                var phaseState=await profiler.SendAsync(plan.Target,new ProfilerRequest(Wire.Version,"mutation_status",plan.Target.Id,plan.Target.ProcessId,plan.Target.StartTimeUtcTicks,epoch),ct);
                if(regime=="original_normal_compile"?(epoch!=0||phaseState.GetProperty("mutation_count").GetInt32()!=0):
                    phaseState.GetProperty("mode").GetString()!="original_body_rejit"||phaseState.GetProperty("request_flags").GetInt32()!=plan.RejitRequestFlags||
                    phaseState.GetProperty("candidate_hash").GetString()!=candidate.OriginalIlHash||!phaseState.GetProperty("compilation_observed").GetBoolean())
                    throw new LabException("mutation_unverified","Control compilation regime or ReJIT flags differ from the frozen protocol.");
                string before=await Code(epoch,ct); var collected=new List<Measurement>();
                for(int block=0;block<plan.Blocks;block++)
                {
                    ct.ThrowIfCancellationRequested(); var target=plan.Target with { Epoch=epoch };
                    Policies.Authorize(context.Execute,Catalog.Root,target,Permission.ExecuteRegisteredScenario,DateTimeOffset.UtcNow); Catalog.UseGrant(context.Execute);
                    string runId=Wire.Id("control-run"),blockId=plan.Id+"-block-"+block;
                    var measurement=await ExecuteMeasuredAsync(new Tedd.DotNetLab.Contracts.RunRequest(root.Id,runId,blockId,plan.ScenarioId,
                        plan.HarnessVariant,plan.OperationsPerRun,plan.WarmupBatches,true,false,epoch,target,context.Execute,
                        DateTimeOffset.UtcNow.AddSeconds(plan.MaximumDurationSeconds)),ct);
                    Policies.Validate(measurement);
                    if(measurement.ExecutionId!=root.Id||measurement.RunId!=runId||measurement.BlockId!=blockId||measurement.Variant!=plan.HarnessVariant||
                        !measurement.Correct||!measurement.Quiescent||measurement.CompletedOperations!=plan.OperationsPerRun||measurement.InputHash!=plan.InputHash||measurement.ScenarioVersion!=plan.ScenarioVersion)
                        throw new LabException("correctness_failed","Control measurement violates its frozen completed-work or scenario contract.");
                    collected.Add(measurement);
                }
                var after=await harness.HandshakeAsync(plan.Target,ct);
                if(after.Target.Epoch!=epoch||after.Tainted||Wire.Hash(after.Runtime)!=Wire.Hash(handshake.Runtime)||after.ProbeHelper?.Active==true)
                    throw new LabException("observer_mismatch","Control runtime, observer, code epoch, or quiescence changed.");
                string codeAfter=await Code(epoch,ct);
                for(int block=0;block<collected.Count;block++)
                {
                    var artifact=await Artifacts.WriteJsonAsync(operation.Id,collected[block],"compilation-control-measurement/v1",cancellation:ct); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"compilation_control_measurement");
                    var measured=new CompilationControlMeasurement(collected[block].RunId,plan.Id,Wire.Hash(plan),regime,block,epoch,collected[block],after.Runtime!,artifact.Id,before,codeAfter,
                        regime=="original_normal_compile"?0:plan.RejitRequestFlags,plan.CallerPolicy);
                    Catalog.Put(measured.Id,"compilation_control_measurement",measured); measurements.Add(measured);
                }
            }
            async Task<string> Complete(long epoch,CancellationToken ct)
            {
                if(measurements.Count!=2*plan.Blocks) throw new LabException("mutation_unverified","Control did not complete both frozen measurement phases.");
                var ratios=Enumerable.Range(0,plan.Blocks).Select(block=>Math.Log(measurements.Single(m=>m.BlockOrdinal==block&&m.Regime=="original_body_rejit").Measurement.NanosecondsPerOperation)-
                    Math.Log(measurements.Single(m=>m.BlockOrdinal==block&&m.Regime=="original_normal_compile").Measurement.NanosecondsPerOperation)).ToArray();
                double ratio=Math.Exp(StatisticalEngine.Mean(ratios)); if(!double.IsFinite(ratio)||ratio<=0) throw new LabException("invalid_measurement","Control descriptive ratio is outside its finite range.");
                string id=Wire.Id("compilation-control-result");
                var artifact=await Artifacts.WriteJsonAsync(operation.Id,new { plan,root,candidate,measurements,geometric_rejit_to_normal_ratio=ratio,independent_sessions=1,
                    quality="descriptive_only",restored_epoch=epoch,tier_pgo_history="not_reset_or_fully_observed" },"compilation-control/v1",67108864,ct);
                Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"compilation_control_result");
                var result=new CompilationControlResult(id,plan.Id,Wire.Hash(plan),root.Id,candidate.ContentHash,epoch,measurements.Count,1,ratio,artifact.Id,true,true);
                Catalog.Put(result.Id,"compilation_control_result",result); return result.Id;
            }
            return await ExecuteMutationAsync(root,candidate,body,context.Grant,context.Execute,NativeGrant(context.Grant,candidate,root.NativeTransactionId),operation,token,false,
                new((epoch,ct)=>Phase("original_normal_compile",epoch,ct),(epoch,ct)=>Phase("original_body_rejit",epoch,ct),Complete));
        });
    }
}
