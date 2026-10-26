using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;
using System.Text.Json.Serialization;

namespace Tedd.DotNetLab.Application;

public record IndividualLatencyPlan(string Id,string ScenarioId,string ScenarioVersion,string InputHash,string Variant,int Operations,
    int WarmupBatches,int MaximumDurationSeconds,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CompilationControlBinding? CompilationControl=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? TargetRuntimeVersion=null);
public record IndividualLatencyRun(string Id,string PlanId,string PlanHash,TargetIdentity Target,RuntimeFootprint Runtime,
    Measurement Measurement,string ArtifactId,string ObserverIdentity,string CompilationRegime="registered_harness_individual_timing",
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CompilationRegimeEvidence? Compilation=null);

public sealed partial class LabService
{
    public IndividualLatencyPlan CreateIndividualLatencyPlan(string scenarioId,string scenarioVersion,string inputHash,string variant,int operations=100,
        int warmupBatches=1,int maximumDurationSeconds=60,string? compilationControlId=null,string? targetRuntimeVersion=null)
    {
        if(new[]{scenarioId,scenarioVersion,inputHash,variant}.Any(s=>string.IsNullOrWhiteSpace(s)||s.Length>512)||operations is <1 or >10000||
            warmupBatches is <0 or >1000||maximumDurationSeconds is <1 or >600||maximumDurationSeconds>Limits.MaximumDurationSeconds)
            throw new LabException("invalid_plan","Invalid bounded individual-operation timing plan.");
        if(targetRuntimeVersion is { } version&&!RuntimeSupport.IsSelectableManagedVersion(version))
            throw new LabException("invalid_plan","Target runtime requires an exact .NET version of 10.0.0 or later, including any preview suffix.");
        var plan=new IndividualLatencyPlan(Wire.Id("individual-plan"),scenarioId,scenarioVersion,inputHash,variant,operations,warmupBatches,maximumDurationSeconds,
            TargetRuntimeVersion:targetRuntimeVersion);
        plan=plan with { CompilationControl=FreezeCompilationControl(IndividualCompilationPlan(plan),compilationControlId).CompilationControl };
        Catalog.Put(plan.Id,"individual_latency_plan",plan); return plan;
    }
    private static ExperimentPlan IndividualCompilationPlan(IndividualLatencyPlan plan)=>new(plan.Id,plan.ScenarioId,plan.ScenarioVersion,plan.InputHash,
        [plan.Variant],1,plan.Operations,plan.WarmupBatches,0,"quiet-validation",false,"Individual-operation collection.",false,plan.MaximumDurationSeconds,new(),
        CompilationControl:plan.CompilationControl,TargetRuntimeVersion:plan.TargetRuntimeVersion);
    public async Task<Operation> StartIndividualLatenciesAsync(string targetId,long epoch,string planId,string grantId,string key,CancellationToken cancellation)
    {
        var plan=Record<IndividualLatencyPlan>(planId,"individual_latency_plan"); var handshake=await ScenariosAsync(targetId,cancellation);
        var target=Target(targetId).Identity; if(target.Epoch!=epoch||handshake.Tainted) throw new LabException("stale_epoch","Target epoch or health differs.");
        if(handshake.Runtime is null) throw new LabException("not_collected","Individual timing requires a retained runtime/observer footprint.");
        Tedd.DotNetLab.Transport.Local.ProfilerHandshake? qualifiedNative=null;
        if(OperatingSystem.IsLinux()&&handshake.Runtime?.NativeProfilerPresent==true)
        {
            var native=await ProfilerAsync(targetId,cancellation);
            if(Target(targetId).Capabilities["rejit"].State==CapabilityState.Supported) qualifiedNative=native;
        }
        ExecutionQualification.Validate(handshake,"quiet-validation",plan.CompilationControl is not null,qualifiedNative,plan.TargetRuntimeVersion);
        var scenario=handshake.Scenarios.SingleOrDefault(s=>s.Id==plan.ScenarioId);
        if(scenario is null||scenario.Version!=plan.ScenarioVersion||scenario.InputHash!=plan.InputHash||!scenario.Variants.Contains(plan.Variant))
            throw new LabException("not_comparable","Scenario input, version, or variant differs from the frozen latency plan.");
        var compilationPlan=IndividualCompilationPlan(plan);
        var control=plan.CompilationControl is null?null:await VerifyCompilationControlAsync(plan.CompilationControl,cancellation);
        var compilation=await CompilationRegimeAsync(target,handshake.Runtime,compilationPlan,scenario,control,cancellation);
        var grant=await AuthorizeTargetOrScratchAsync(grantId,target,Permission.ExecuteRegisteredScenario,handshake,cancellation);
        return Start("individual_latencies",target,key,Wire.Hash(new { planId,plan_hash=Wire.Hash(plan),target,grantId }),async(operation,token)=>
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
            if(Wire.Hash(await CompilationRegimeAsync(target,handshake.Runtime,compilationPlan,scenario,control,deadline.Token))!=Wire.Hash(compilation))
                throw new LabException("not_comparable","Compilation state changed before individual timing.");
            await ReauthorizeTargetOrScratchAsync(grant,target,Permission.ExecuteRegisteredScenario,handshake,deadline.Token); Catalog.UseGrant(grant);
            string runId=Wire.Id("individual-run");
            var targetGrant=BindTargetGrant(grant,target);
            var request=new RunRequest(operation.Id,runId,runId,plan.ScenarioId,plan.Variant,plan.Operations,plan.WarmupBatches,true,false,epoch,target,targetGrant,
                DateTimeOffset.UtcNow.AddSeconds(plan.MaximumDurationSeconds),RetainIndividualLatencies:true);
            var measured=await ExecuteMeasuredAsync(request,deadline.Token);
            string observer=scenario.IsAsync?"harness_async_individual_stopwatch/v1":"harness_sync_individual_stopwatch/v1";
            var vector=measured.IndividualLatencies;
            if(vector is null||vector.ObserverIdentity!=observer||vector.ObservationType!="individual_operation_latency"||vector.Frequency!=measured.Frequency||
                vector.Frequency<=0||vector.ElapsedTicks.Count!=plan.Operations||vector.ElapsedTicks.Any(t=>t<0)||!measured.Correct||!measured.Quiescent||
                measured.CompletedOperations!=plan.Operations||measured.InputHash!=plan.InputHash||measured.ScenarioVersion!=plan.ScenarioVersion)
                throw new LabException("scenario_failed","Individual timing vector or completed-work contract is invalid.");
            var after=await harness.HandshakeAsync(target,deadline.Token);
            if(after.Target!=handshake.Target||Wire.Hash(after.Runtime)!=Wire.Hash(handshake.Runtime)||after.Tainted)
                throw new LabException("not_comparable","Runtime/observer identity changed during individual timing.");
            if(Wire.Hash(await CompilationRegimeAsync(target,after.Runtime,compilationPlan,scenario,control,deadline.Token))!=Wire.Hash(compilation))
                throw new LabException("not_comparable","Compilation state changed during individual timing.");
            var artifact=await Artifacts.WriteJsonAsync(operation.Id,measured,observer,cancellation:deadline.Token); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"individual_latency_measurement");
            var run=new IndividualLatencyRun(runId,plan.Id,Wire.Hash(plan),target,after.Runtime!,measured with { IndividualLatencies=null },artifact.Id,observer,
                CompilationRegime:compilation?.Regime??"registered_harness_individual_timing",Compilation:compilation);
            Catalog.Put(run.Id,"individual_latency_run",run); Catalog.Transition(operation.Id,OperationState.Completed,run.Id); return true;
        });
    }
    public async Task<object> IndividualLatencySliceAsync(string runId,int offset=0,int count=100)
    {
        if(offset<0||count is <1 or >1000) throw new LabException("resource_limit","Invalid individual-latency slice bounds.");
        var run=Record<IndividualLatencyRun>(runId,"individual_latency_run"); var artifact=Record<Artifact>(run.ArtifactId,"artifact");
        if(artifact.Length>16777216) throw new LabException("resource_limit","Individual vector exceeds its read budget.");
        var measured=Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact)));
        var vector=measured.IndividualLatencies;
        if(vector is null||vector.ObserverIdentity!=run.ObserverIdentity||Wire.Hash(measured with { IndividualLatencies=null })!=Wire.Hash(run.Measurement)||
            vector.Frequency!=measured.Frequency||vector.ElapsedTicks.Count!=measured.CompletedOperations)
            throw new LabException("artifact_corrupt","Individual timing vector differs from its retained index.");
        if(offset>vector.ElapsedTicks.Count) throw new LabException("invalid_cursor","Slice starts beyond the individual observation vector.");
        var ticks=vector.ElapsedTicks.Skip(offset).Take(count).ToArray();
        return new { run_id=runId,vector.ObserverIdentity,vector.ObservationType,vector.Frequency,total=vector.ElapsedTicks.Count,offset,
            elapsed_ticks=ticks,next_offset=offset+ticks.Length<vector.ElapsedTicks.Count?(int?)(offset+ticks.Length):null,vector.Meaning };
    }
}
