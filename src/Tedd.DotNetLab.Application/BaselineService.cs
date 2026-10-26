using System.Text.Json;
using System.Text.RegularExpressions;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    private static readonly string[] BaselineUnknowns=["cpu_model","affinity","power_policy","runtime_tier","pgo_history","background_load"];
    private static BaselineFingerprint Fingerprint(ExperimentPlan plan,ScenarioDescriptor scenario,TargetIdentity target,
        RuntimeFootprint? runtime,string variant,string profileHash,string observer,string compilationRegime)
    {
        if(runtime is null||runtime.EntryAssemblyHash.Length!=64||runtime.AssemblyHashes.Count==0||runtime.RuntimeConfigurationHash is null)
            throw new LabException("baseline_identity_unavailable","A baseline requires retained build and runtime-configuration identity.");
        return new(scenario.Id,scenario.Version,scenario.InputHash,variant,scenario.ResetLevel,plan.OperationsPerRun,plan.WarmupBatches,
            profileHash,observer,target.RuntimeVersion,target.Architecture,
            Wire.Hash(new { runtime.EntryAssemblyHash,runtime.AssemblyHashes }),Wire.Hash(new { runtime.OperatingSystem,runtime.Architecture,
                runtime.StopwatchFrequency,runtime.DebuggerAttached,runtime.NativeProfilerPresent,runtime.DiagnosticsEnabled,runtime.ProfilingEnabled,
                runtime.StartupHooks,runtime.AdditionalDependencies,runtime.RuntimeConfigurationHash }),target.Epoch,compilationRegime,
            runtime.NativeProfilerPresent?target.Id:null);
    }
    private async Task<(BaselineSource Source,BaselineFingerprint Fingerprint)> BaselineSourceAsync(string id,CancellationToken cancellation)
    {
        var record=Catalog.Get(id); Measurement measurement; BaselineFingerprint fingerprint; string artifactId;
        if(record.Kind=="run")
        {
            var run=Catalog.Get<RunRecord>(id); var execution=Record<ExecutionRecord>(run.ExecutionId,"execution");
            var plan=Record<ExperimentPlan>(run.PlanId,"plan");
            if(execution.State!="completed"||!execution.RunIds.Contains(id)||run.PlanHash!=Wire.Hash(plan)||execution.PlanHash!=run.PlanHash)
                throw new LabException("invalid_baseline","Only verified completed execution runs can be selected.");
            measurement=run.Measurement;
            var reference=Record<JsonElement>(id+"-measurement","measurement_artifact");
            artifactId=reference.GetProperty("artifact_id").GetString()!;
            fingerprint=Fingerprint(plan,run.Scenario,run.Target,run.Runtime,measurement.Variant,run.ProfileHash,run.ObserverState,
                run.Compilation?.Regime??(run.Runtime?.NativeProfilerPresent==true?"epoch_bound_profiled_process":"normal_compile/cooperative_process"));
            string path=await Artifacts.VerifiedPathAsync(Record<Artifact>(artifactId,"artifact"),cancellation);
            if(Wire.Hash(Wire.Deserialize<Measurement>(await File.ReadAllTextAsync(path,cancellation)))!=Wire.Hash(measurement))
                throw new LabException("artifact_corrupt","Baseline measurement differs from its sealed evidence.");
        }
        else if(record.Kind=="normal_build_session")
        {
            var session=Catalog.Get<NormalBuildSession>(id); var execution=Record<ConfirmationExecution>(session.ExecutionId,"confirmation_execution");
            var plan=Record<ConfirmationPlan>(session.PlanId,"confirmation_plan");
            if(execution.State!="completed"||!execution.SessionIds.Contains(id)||execution.PlanHash!=plan.ContentHash||session.ExitCode!=0)
                throw new LabException("invalid_baseline","Normal-build selection requires a completed joined confirmation execution.");
            measurement=session.Measurement; artifactId=session.MeasurementArtifactId;
            var descriptor=new ScenarioDescriptor(plan.Experiment.ScenarioId,plan.Experiment.ScenarioVersion,plan.Experiment.InputHash,
                ResetLevel.FreshProcess,ThreadAffinity.Any,false,"Registered completed work.",plan.Experiment.Variants);
            fingerprint=Fingerprint(plan.Experiment,descriptor,session.Target,session.Runtime,session.Variant,Profiles.Resolve(plan.Experiment.Profile).Hash,
                "diagnostics_disabled/owned_process",session.CompilationRegime);
            string path=await Artifacts.VerifiedPathAsync(Record<Artifact>(artifactId,"artifact"),cancellation);
            using var evidence=JsonDocument.Parse(await File.ReadAllTextAsync(path,cancellation));
            if(evidence.RootElement.GetProperty("plan_hash").GetString()!=plan.ContentHash||
                Wire.Hash(evidence.RootElement.GetProperty("measurement").Deserialize<Measurement>(Wire.Json))!=Wire.Hash(measurement))
                throw new LabException("artifact_corrupt","Baseline session differs from its sealed evidence.");
        }
        else throw new LabException("invalid_baseline","Baseline sources must be immutable run or normal-build session records.");
        Policies.Validate(measurement);
        if(!measurement.Correct||!measurement.Quiescent||measurement.RunId!=id||measurement.InputHash!=fingerprint.InputHash||
            measurement.ScenarioVersion!=fingerprint.ScenarioVersion||measurement.CompletedOperations!=fingerprint.OperationsPerRun)
            throw new LabException("invalid_baseline","Baseline source failed identity, correctness, work, or quiescence validation.");
        return(new(new(id,record.Revision,record.Hash),artifactId,Wire.Hash(fingerprint)),fingerprint);
    }
    public async Task<Baseline> CreateBaselineAsync(IReadOnlyList<string> runIds,string role,CancellationToken cancellation=default)
    {
        if(runIds.Count is <1 or >10000||runIds.Distinct().Count()!=runIds.Count||string.IsNullOrWhiteSpace(role)||role.Length>128)
            throw new LabException("invalid_baseline","Baseline selection requires distinct bounded runs and a declared role.");
        var sources=new List<BaselineSource>(); BaselineFingerprint? fingerprint=null;
        foreach(string id in runIds.Order(StringComparer.Ordinal))
        {
            var source=await BaselineSourceAsync(id,cancellation); fingerprint??=source.Fingerprint;
            if(Wire.Hash(fingerprint)!=source.Source.FingerprintHash)
                throw new LabException("baseline_mismatch","Baseline runs differ in scenario, variant, build, runtime, settings, or observer identity.");
            sources.Add(source.Source);
        }
        string identity=Wire.Id("baseline");
        var manifest=new BaselineManifest(identity,role,fingerprint!,sources,BaselineUnknowns);
        var artifact=await Artifacts.WriteJsonAsync(identity,manifest,"baseline-selection/v1");
        var selection=new Baseline(identity,sources.Select(source=>source.Run.Id).ToArray(),role,artifact.Id,fingerprint!,BaselineUnknowns);
        lock(gate)
        {
            Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"baseline_manifest");
            foreach(var source in sources) Catalog.Pin(source.MeasurementArtifactId,"baseline_input");
            Catalog.Put(selection.Id,"baseline",selection);
        }
        return selection;
    }
    public NotebookRecord SetBaselineAlias(string name,string baselineId,long? expectedRevision=null)
    {
        if(!Regex.IsMatch(name,@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,63}\z")) throw new LabException("invalid_request","Invalid baseline alias name.");
        name=name.ToLowerInvariant(); var baseline=Catalog.Get(baselineId);
        if(baseline.Kind!="baseline") throw new LabException("invalid_baseline","Alias must identify an immutable baseline selection.");
        string id="baseline-alias-"+Wire.Hash(new { name })[..32];
        return Catalog.Put(id,"baseline_alias",new BaselineAlias(id,name,baseline.Id,baseline.Revision,baseline.Hash),expectedRevision);
    }
    private ExperimentPlan FreezeBaseline(ExperimentPlan plan,string? baselineId,string? aliasId)
    {
        if(baselineId is not null&&aliasId is not null||plan.Baseline is not null&&(baselineId is not null||aliasId is not null))
            throw new LabException("invalid_plan","Choose one baseline selection or alias.");
        if(aliasId is not null)
        {
            var row=Catalog.Get(aliasId); if(row.Kind!="baseline_alias") throw new LabException("invalid_baseline","Expected a baseline alias.");
            var alias=Wire.Deserialize<BaselineAlias>(row.Json);
            plan=plan with { Baseline=new(alias.BaselineId,alias.BaselineRevision,alias.BaselineHash,row.Id,row.Revision,row.Hash) };
        }
        else if(baselineId is not null)
        {
            var row=Catalog.Get(baselineId); if(row.Kind!="baseline") throw new LabException("invalid_baseline","Expected a baseline selection.");
            plan=plan with { Baseline=new(row.Id,row.Revision,row.Hash) };
        }
        if(plan.Baseline is { } frozen)
        {
            var baseline=FrozenSelection(frozen);
            if(baseline.Fingerprint.ScenarioId!=plan.ScenarioId||baseline.Fingerprint.ScenarioVersion!=plan.ScenarioVersion||
                baseline.Fingerprint.InputHash!=plan.InputHash||baseline.Fingerprint.Variant!=plan.Variants[0]||
                baseline.Fingerprint.OperationsPerRun!=plan.OperationsPerRun||baseline.Fingerprint.WarmupBatches!=plan.WarmupBatches||
                baseline.Fingerprint.ProfileHash!=Profiles.Resolve(plan.Profile).Hash)
                throw new LabException("baseline_mismatch","Plan differs from the frozen baseline scenario, control variant, or measurement policy.");
        }
        return plan;
    }
    private Baseline FrozenSelection(FrozenBaseline frozen)
    {
        if(frozen.Revision<1||!Regex.IsMatch(frozen.Hash,@"\A[0-9a-f]{64}\z")||
            (frozen.AliasId is null)!=(frozen.AliasRevision is null)||(frozen.AliasId is null)!=(frozen.AliasHash is null))
            throw new LabException("invalid_baseline","Frozen selection and alias require complete exact revision/hash bindings.");
        var row=Catalog.Get(frozen.Id,frozen.Revision);
        if(row.Kind!="baseline"||row.Hash!=frozen.Hash||frozen.SamplingPolicy!="fresh_matched_control_runs")
            throw new LabException("invalid_baseline","Frozen baseline identity differs from its retained revision.");
        if(frozen.AliasId is not null)
        {
            var aliasRow=Catalog.Get(frozen.AliasId,frozen.AliasRevision);
            var alias=Wire.Deserialize<BaselineAlias>(aliasRow.Json);
            if(aliasRow.Kind!="baseline_alias"||aliasRow.Hash!=frozen.AliasHash||alias.BaselineId!=frozen.Id||
                alias.BaselineRevision!=frozen.Revision||alias.BaselineHash!=frozen.Hash)
                throw new LabException("invalid_baseline","Frozen alias revision differs from its selected baseline.");
        }
        return Wire.Deserialize<Baseline>(row.Json);
    }
    private async Task VerifyBaselineAsync(FrozenBaseline frozen,CancellationToken cancellation)
    {
        var baseline=FrozenSelection(frozen);
        string path=await Artifacts.VerifiedPathAsync(Record<Artifact>(baseline.ManifestArtifactId,"artifact"),cancellation);
        var manifest=Wire.Deserialize<BaselineManifest>(await File.ReadAllTextAsync(path,cancellation));
        if(manifest.Id!=baseline.Id||manifest.Role!=baseline.Role||Wire.Hash(manifest.Fingerprint)!=Wire.Hash(baseline.Fingerprint)||
            !manifest.Sources.Select(source=>source.Run.Id).SequenceEqual(baseline.RunIds))
            throw new LabException("artifact_corrupt","Baseline manifest differs from its immutable selection.");
        foreach(var source in manifest.Sources)
        {
            if(Catalog.Get(source.Run.Id,source.Run.Revision).Hash!=source.Run.Hash||source.FingerprintHash!=Wire.Hash(baseline.Fingerprint))
                throw new LabException("artifact_corrupt","Baseline source revision or fingerprint differs.");
            await Artifacts.VerifiedPathAsync(Record<Artifact>(source.MeasurementArtifactId,"artifact"),cancellation);
        }
    }
    private void VerifyBaselineTarget(ExperimentPlan plan,HarnessHandshake handshake,ScenarioDescriptor scenario,string observer,CompilationRegimeEvidence? compilation=null)
    {
        if(plan.Baseline is null) return;
        var baseline=FrozenSelection(plan.Baseline);
        var current=Fingerprint(plan,scenario,handshake.Target,handshake.Runtime,plan.Variants[0],Profiles.Resolve(plan.Profile).Hash,observer,
            compilation?.Regime??(handshake.Runtime?.NativeProfilerPresent==true?"epoch_bound_profiled_process":"normal_compile/cooperative_process"));
        if(Wire.Hash(current)!=Wire.Hash(baseline.Fingerprint))
            throw new LabException("baseline_mismatch","Live build, runtime, settings, code epoch, or observer differs from the frozen baseline.");
    }
}
