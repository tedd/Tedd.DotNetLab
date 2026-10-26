using System.Text.Json;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Application;

public record NativeSeriesPlan(string Id,IReadOnlyList<FrozenRecordReference> Trials,AnalysisPolicy Analysis,bool IndependentSessions,
    string IndependenceJustification,string CarryoverJustification,string ComparabilityHash,string RuntimeHash,DateTimeOffset FrozenAt,
    int MaximumDurationSeconds,string Estimand="geometric_mean_of_session_mean_matched_log_batch_cost_ratios",
    string Aggregation="equal_weight_sessions;equal_weight_matched_blocks_within_session/v1");
public record NativeSeriesSession(FrozenRecordReference TrialResult,string TrialPlanId,TargetIdentity Target,double OriginalGeometricCost,
    double CandidateGeometricCost,IReadOnlyList<FrozenRecordReference> Runs,string TrialArtifactId,string TrialArtifactHash);
public record NativeSeriesDataset(string Id,string PlanId,string PlanHash,IReadOnlyList<FrozenRecordReference> TrialResults,string ArtifactId,string ArtifactHash,
    int ObservedSessions,int InputRunCount,string Provenance="verified_native_body_trial_sessions/v1");
public record NativeSeriesComparison(string Id,string PlanId,string PlanHash,string DatasetId,string DatasetHash,string ArtifactId,
    Comparison Comparison,int ObservedSessions,int InputRunCount,string Estimand,string Aggregation,IReadOnlyList<string> Assumptions,
    string Meaning="Live original-body/candidate ReJIT contrast across declared process sessions; normal-build confirmation remains required for promotion.");

public sealed partial class LabService
{
    private FrozenRecordReference NativeReference(string id,string kind)
    {
        var record=Catalog.Get(id); if(record.Kind!=kind) throw new LabException("invalid_request","Native series source kind differs.");
        return new(record.Id,record.Revision,record.Hash);
    }
    private T NativeFrozen<T>(FrozenRecordReference reference,string kind)
    {
        var record=Catalog.Get(reference.Id);
        if(record.Kind!=kind||record.Revision!=reference.Revision||record.Hash!=reference.Hash)
            throw new LabException("not_comparable","Native series source revision/hash differs.");
        return Wire.Deserialize<T>(record.Json);
    }
    private string NativeSeriesFingerprint(NativeTrialPlan plan)
    {
        var original=TrialCandidate(plan.Original); var candidate=TrialCandidate(plan.Candidate);
        return Wire.Hash(new { plan.ScenarioId,plan.ScenarioVersion,plan.InputHash,plan.HarnessVariant,plan.OperationsPerRun,plan.WarmupBatches,
            blocks=plan.Blocks.Count,plan.Target.RuntimeVersion,plan.Target.Architecture,original.Scope,original.Method.SignatureHex,
            original_body=original.BodyHash,candidate_body=candidate.BodyHash,candidate.Policy,
            callers=(original.Inliners??[]).Select(c=>new { c.ModuleMvid,c.ModuleHash,c.MethodToken,c.Signature,c.OriginalIlHash }),
            flags=3,regimes=new[]{"original_body_rejit","candidate_rejit"},profile="quiet-validation" });
    }
    public async Task<NativeSeriesPlan> CreateNativeSeriesAsync(IReadOnlyList<string> trialPlanIds,AnalysisPolicy? analysis=null,bool independentSessions=false,
        string independenceJustification="",string carryoverJustification="",int maximumDurationSeconds=300,CancellationToken cancellation=default)
    {
        analysis??=new(); Policies.Validate(analysis);
        if(trialPlanIds.Count is <1 or >100||trialPlanIds.Distinct().Count()!=trialPlanIds.Count||trialPlanIds.Any(string.IsNullOrWhiteSpace)||
            independenceJustification.Length>8192||carryoverJustification.Length>8192||maximumDurationSeconds is <1 or >600||maximumDurationSeconds>Limits.MaximumDurationSeconds||
            independentSessions&&(string.IsNullOrWhiteSpace(independenceJustification)||string.IsNullOrWhiteSpace(carryoverJustification)))
            throw new LabException("invalid_plan","Native series requires distinct sessions, a fixed bounded budget, and explicit independence/carryover declarations for inference.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(maximumDurationSeconds));
        var references=trialPlanIds.Select(id=>NativeReference(id,"native_trial_plan")).ToArray();
        var trials=references.Select(r=>NativeFrozen<NativeTrialPlan>(r,"native_trial_plan")).ToArray();
        if(trials.Select(t=>(t.Target.ProcessId,t.Target.StartTimeUtcTicks)).Distinct().Count()!=trials.Length||trials.Select(t=>t.Target.Id).Distinct().Count()!=trials.Length)
            throw new LabException("invalid_plan","Each native series slot requires a distinct process/start session.");
        var fingerprints=trials.Select(NativeSeriesFingerprint).Distinct().ToArray();
        if(fingerprints.Length!=1) throw new LabException("not_comparable","Native series workloads, methods, bodies, callers, or sampling policies differ.");
        var runtimeHashes=new List<string>();
        foreach(var trial in trials) runtimeHashes.Add((await VerifyCompilationControlAsync(trial.CompilationControl,deadline.Token)).RuntimeHash);
        if(runtimeHashes.Distinct().Count()!=1) throw new LabException("not_comparable","Native series runtime, build, configuration, or observer footprints differ.");
        var plan=new NativeSeriesPlan(Wire.Id("native-series-plan"),references,analysis,independentSessions,independenceJustification,carryoverJustification,
            fingerprints[0],runtimeHashes[0],DateTimeOffset.UtcNow,maximumDurationSeconds);
        Catalog.FreezeNativeSeries(plan.Id,plan,references.Select(r=>new Tedd.DotNetLab.Storage.CatalogSourceReference(r.Id,r.Revision,r.Hash)).ToArray()); return plan;
    }
    private async Task<NativeSeriesSession[]> CollectNativeSeriesAsync(NativeSeriesPlan plan,IReadOnlyList<FrozenRecordReference> results,CancellationToken cancellation)
    {
        if(results.Count!=plan.Trials.Count||results.Select(r=>r.Id).Distinct().Count()!=results.Count)
            throw new LabException("not_comparable","Every frozen native session requires one distinct completed result; posthoc exclusions are forbidden.");
        var sessions=new List<NativeSeriesSession>();
        for(int slot=0;slot<plan.Trials.Count;slot++)
        {
            cancellation.ThrowIfCancellationRequested(); var trial=NativeFrozen<NativeTrialPlan>(plan.Trials[slot],"native_trial_plan");
            var membership=Record<JsonElement>("native-series-member-"+trial.Id,"native_series_member");
            if(membership.GetProperty("series_id").GetString()!=plan.Id||membership.GetProperty("series_hash").GetString()!=Wire.Hash(plan)||
                membership.GetProperty("trial").Deserialize<FrozenRecordReference>(Wire.Json)!=plan.Trials[slot]||NativeSeriesFingerprint(trial)!=plan.ComparabilityHash)
                throw new LabException("not_comparable","Trial lacks its exact premeasurement series membership or workload fingerprint.");
            var result=NativeFrozen<NativeTrialResult>(results[slot],"native_trial_result");
            if(result.PlanId!=trial.Id||result.PlanHash!=plan.Trials[slot].Hash||Catalog.GetOperation(result.OperationId).CreatedAt<plan.FrozenAt)
                throw new LabException("not_comparable","Native result does not belong to the selected slot or was accepted before series freeze.");
            await VerifyNativeTrialAsync(result.Id,cancellation);
            var runs=result.RunIds.Select(id=>Record<NativeTrialRun>(id,"native_trial_run")).ToArray();
            if(runs.Any(r=>Wire.Hash(r.Runtime)!=plan.RuntimeHash)) throw new LabException("not_comparable","Native session runtime/observer differs from the frozen series.");
            var aggregate=NativeSessionEngine.Aggregate(trial.Target.Id,trial.Blocks.Select(block=>new CostPair(block.Ordinal.ToString(System.Globalization.CultureInfo.InvariantCulture),trial.Target.Id,
                runs.Single(r=>r.BlockOrdinal==block.Ordinal&&r.Compilation.Regime=="original_body_rejit").Measurement.NanosecondsPerOperation,
                runs.Single(r=>r.BlockOrdinal==block.Ordinal&&r.Compilation.Regime=="candidate_rejit").Measurement.NanosecondsPerOperation)).ToArray());
            var artifact=Record<Artifact>(result.ArtifactId,"artifact");
            sessions.Add(new(results[slot],trial.Id,trial.Target,aggregate.Baseline,aggregate.Candidate,runs.Select(r=>NativeReference(r.Id,"native_trial_run")).ToArray(),artifact.Id,artifact.Hash));
        }
        return sessions.ToArray();
    }
    public async Task<NativeSeriesDataset> RegisterNativeSeriesResultsAsync(string planId,IReadOnlyList<string> resultIds,CancellationToken cancellation=default)
    {
        var plan=Record<NativeSeriesPlan>(planId,"native_series_plan");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
        if(resultIds.Count!=plan.Trials.Count||resultIds.Select(id=>id).Distinct().Count()!=resultIds.Count||
            resultIds.Any(id=>string.IsNullOrWhiteSpace(id)||id.Length>512))
            throw new LabException("not_comparable","Native result selection must contain the exact distinct frozen session budget.");
        var references=resultIds.ToArray().Select(id=>NativeReference(id,"native_trial_result")).ToArray();
        var sessions=await CollectNativeSeriesAsync(plan,references,deadline.Token);
        var artifact=await Artifacts.WriteJsonAsync(plan.Id,new { plan_id=plan.Id,plan_hash=Wire.Hash(plan),sessions,provenance="verified_native_body_trial_sessions/v1" },
            "native-series-sessions/v1",16777216,deadline.Token); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"native_series_dataset");
        var dataset=new NativeSeriesDataset("native-series-data-"+plan.Id,plan.Id,Wire.Hash(plan),references,artifact.Id,artifact.Hash,sessions.Length,sessions.Sum(s=>s.Runs.Count));
        Upsert(dataset.Id,"native_series_dataset",dataset); return dataset;
    }
    public Operation StartNativeSeriesRegistration(string planId,IReadOnlyList<string> resultIds,string key)
    {
        var plan=Record<NativeSeriesPlan>(planId,"native_series_plan");
        if(resultIds.Count!=plan.Trials.Count||resultIds.Distinct().Count()!=resultIds.Count||resultIds.Any(id=>string.IsNullOrWhiteSpace(id)||id.Length>512))
            throw new LabException("not_comparable","Native registration must cover the exact frozen session budget.");
        var selected=resultIds.ToArray(); var references=selected.Select(id=>NativeReference(id,"native_trial_result")).ToArray();
        var target=new TargetIdentity(plan.Id,0,0,plan.Id,"retained-evidence","analysis");
        return Start("native_series_registration",target,key,Wire.Hash(new { planId,plan_hash=Wire.Hash(plan),results=references }),async(operation,cancellation)=>
        {
            var result=await RegisterNativeSeriesResultsAsync(planId,selected,cancellation);
            Catalog.Transition(operation.Id,OperationState.Completed,result.Id); return true;
        });
    }
    public async Task<NativeSeriesComparison> CompareNativeSeriesAsync(string datasetId,CancellationToken cancellation=default)
    {
        var dataset=Record<NativeSeriesDataset>(datasetId,"native_series_dataset"); var plan=Record<NativeSeriesPlan>(dataset.PlanId,"native_series_plan");
        if(dataset.PlanHash!=Wire.Hash(plan)) throw new LabException("not_comparable","Native series dataset plan hash differs.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
        var sessions=await CollectNativeSeriesAsync(plan,dataset.TrialResults,deadline.Token);
        var source=Record<Artifact>(dataset.ArtifactId,"artifact");
        if(source.Hash!=dataset.ArtifactHash||source.Length>16777216) throw new LabException("artifact_corrupt","Native series source manifest hash or size differs.");
        using var manifest=JsonDocument.Parse(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(source,deadline.Token),deadline.Token));
        if(manifest.RootElement.GetProperty("plan_id").GetString()!=plan.Id||manifest.RootElement.GetProperty("plan_hash").GetString()!=dataset.PlanHash||
            Wire.Hash(manifest.RootElement.GetProperty("sessions").Deserialize<NativeSeriesSession[]>(Wire.Json))!=Wire.Hash(sessions)||
            dataset.ObservedSessions!=sessions.Length||dataset.InputRunCount!=sessions.Sum(s=>s.Runs.Count))
            throw new LabException("artifact_corrupt","Native series source manifest differs from verified complete session inputs.");
        var pairs=sessions.Select(s=>new CostPair(s.TrialPlanId,s.Target.Id,s.OriginalGeometricCost,s.CandidateGeometricCost)).ToArray();
        string hash=Wire.Hash(new { plan_hash=Wire.Hash(plan),dataset_hash=Wire.Hash(dataset),engine=StatisticalEngine.Version,aggregation_engine=NativeSessionEngine.Version,plan.Aggregation });
        string id="native-series-comparison-"+hash;
        var comparison=StatisticalEngine.Paired(pairs,plan.Analysis,plan.IndependentSessions) with { Id=id+"-effect",
            Estimator=NativeSessionEngine.Version,RunIds=[],InputRunCount=dataset.InputRunCount,PromotionEligible=false };
        string[] assumptions=[plan.IndependentSessions?"independent_process_sessions_declared":"independence_not_established","fixed_premeasurement_session_and_block_budget",
            "no_posthoc_exclusions","equal_weight_session_log_costs","t_model_for_session_log_ratios","randomized_matched_body_order",
            string.IsNullOrWhiteSpace(plan.CarryoverJustification)?"carryover_adequacy_not_established":"carryover_adequacy_declared_by_plan",
            "tiering_pgo_history_not_reset","fresh_normal_build_confirmation_required"];
        var report=await Artifacts.WriteJsonAsync(id,new { plan,dataset,sessions,comparison,assumptions,expected_treatment_differences=new[]{"IL_body","ReJIT_identity","session_scoped_epochs"} },
            "native-series-comparison/v1",16777216,deadline.Token); Upsert(report.Id,"artifact",report); Catalog.Pin(report.Id,"native_series_comparison");
        var result=new NativeSeriesComparison(id,plan.Id,Wire.Hash(plan),dataset.Id,Wire.Hash(dataset),report.Id,comparison,sessions.Length,dataset.InputRunCount,
            plan.Estimand,plan.Aggregation,assumptions); Upsert(result.Id,"native_series_comparison",result);
        Upsert(comparison.Id,"comparison",comparison); return result;
    }
    public Operation StartNativeSeriesComparison(string datasetId,string key)
    {
        var dataset=Record<NativeSeriesDataset>(datasetId,"native_series_dataset"); var plan=Record<NativeSeriesPlan>(dataset.PlanId,"native_series_plan");
        var target=new TargetIdentity(plan.Id,0,0,plan.Id,"retained-evidence","analysis");
        return Start("native_series_analysis",target,key,Wire.Hash(new { datasetId,dataset_hash=Wire.Hash(dataset),plan_hash=Wire.Hash(plan),
            engine=StatisticalEngine.Version,aggregation=NativeSessionEngine.Version }),async(operation,cancellation)=>
        {
            var result=await CompareNativeSeriesAsync(datasetId,cancellation);
            Catalog.Transition(operation.Id,OperationState.Completed,result.Id); return true;
        });
    }
    public async Task<NativeSeriesComparison> ReadNativeSeriesComparisonAsync(string resultId,CancellationToken cancellation=default)
    {
        var result=Record<NativeSeriesComparison>(resultId,"native_series_comparison"); var artifact=Record<Artifact>(result.ArtifactId,"artifact");
        if(artifact.Length>16777216) throw new LabException("resource_limit","Native comparison report exceeds its read budget.");
        using var report=JsonDocument.Parse(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact,cancellation),cancellation));
        if(Wire.Hash(report.RootElement.GetProperty("comparison").Deserialize<Comparison>(Wire.Json))!=Wire.Hash(result.Comparison)||
            Wire.Hash(report.RootElement.GetProperty("plan").Deserialize<NativeSeriesPlan>(Wire.Json))!=result.PlanHash||
            Wire.Hash(report.RootElement.GetProperty("dataset").Deserialize<NativeSeriesDataset>(Wire.Json))!=result.DatasetHash||
            Wire.Hash(report.RootElement.GetProperty("assumptions").Deserialize<string[]>(Wire.Json))!=Wire.Hash(result.Assumptions))
            throw new LabException("artifact_corrupt","Native comparison report differs from its retained immutable index.");
        return result;
    }
}
