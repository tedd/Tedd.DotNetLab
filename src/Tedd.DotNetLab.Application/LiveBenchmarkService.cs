using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

namespace Tedd.DotNetLab.Application;

public record LiveModification(string Id,string Variant,string Regime,string? CandidateId,string? CandidateHash,
    string? MutationId,string BodyHash,string MethodScope);
public record LiveBenchmarkPlan(string Id,TargetIdentity Target,ScenarioDescriptor Scenario,string Variant,
    RuntimeFootprint Runtime,LiveModification Modification,CompilationRegimeEvidence? Compilation,
    CompilationControlBinding? CompilationControl,LiveBenchmarkPolicy Policy,int MaximumDurationSeconds,
    string? CalibrationResultId,DateTimeOffset FrozenAt);
public record LiveBenchmarkResult(string Id,string PlanId,string PlanHash,string OperationId,TargetIdentity Target,
    LiveModification Modification,string State,string? ArtifactId,LiveBenchmarkSummary? Summary,string? ComparabilityHash,
    int OperationsPerIteration,string? Failure,bool PromotionEligible=false);
public record LiveBenchmarkObservation(string ResultId,string OperationId,TargetIdentity Target,string? MutationId);
public record LiveBenchmarkGroup(string ModificationId,string ComparabilityHash,LiveModification Modification,
    IReadOnlyList<string> ResultIds,IReadOnlyList<LiveBenchmarkObservation> Observations,int ProcessSessions,int Runs,int Samples,double MeanNanoseconds,
    double MinimumRunMeanNanoseconds,double MaximumRunMeanNanoseconds,double? RunMeanStandardDeviationNanoseconds,
    double? RunMeanCoefficientOfVariation,double? ThreadAllocatedBytesPerOperation,
    double ProcessAllocatedBytesPerOperation,IReadOnlyDictionary<string,double> TelemetryPerOperation,
    string Quality,IReadOnlyList<string> Warnings,
    string Weighting="Equal weight per complete run; iterations within a process are not independent sessions.");
public record LiveBenchmarkContrast(LiveBenchmarkGroup Baseline,LiveBenchmarkGroup Candidate,double Ratio,
    double ChangePercent,double? ThreadAllocationDelta,string Quality,IReadOnlyList<string> Warnings,
    bool PromotionEligible=false,string Meaning="Descriptive live-process contrast; time order, workload interference, and tiering/PGO carryover can confound the effect.");

public sealed partial class LabService
{
    public async Task<LiveBenchmarkPlan> CreateLiveBenchmarkPlanAsync(string targetId,long epoch,string scenarioId,string variant="baseline",
        LiveBenchmarkPolicy? policy=null,string? calibrationResultId=null,string? mutationId=null,string? compilationControlId=null,
        int maximumDurationSeconds=120,CancellationToken cancellation=default)
    {
        policy??=new(); policy.Validate();
        if(maximumDurationSeconds<1||maximumDurationSeconds>Math.Min(600,Limits.MaximumDurationSeconds))
            throw new LabException("invalid_plan","Live benchmark deadline exceeds workspace policy.");
        var handshake=await ScenariosAsync(targetId,cancellation);
        RequireLiveHarness(handshake,epoch);
        var scenario=handshake.Scenarios.SingleOrDefault(s=>s.Id==scenarioId)
            ??throw new LabException("scenario_not_found","Live benchmark scenario is not registered.");
        if(scenario.IsAsync||scenario.ResetLevel==ResetLevel.FreshProcess||!scenario.Variants.Contains(variant))
            throw new LabException("capability_unsupported","Live benchmarks require a synchronous repeatable scenario and registered variant.");
        CompilationControlBinding? binding=compilationControlId is null?null:ControlBinding(compilationControlId);
        var compilation=await LiveCompilationAsync(handshake,scenario,variant,binding,cancellation);
        var modification=LiveModificationBinding(handshake,scenario,variant,compilation,mutationId);
        if(calibrationResultId is not null)
        {
            var (reference,referencePlan,_)=await ReadLiveBenchmarkAsync(calibrationResultId,cancellation);
            if(reference.State!="completed"||reference.OperationsPerIteration<1||
                Wire.Hash(referencePlan.Scenario)!=Wire.Hash(scenario)||Wire.Hash(referencePlan.Runtime)!=Wire.Hash(handshake.Runtime)||
                reference.Target.Id!=handshake.Target.Id)
                throw new LabException("not_comparable","Calibration must come from the same live session, runtime, and scenario inputs.");
            if(policy.OperationsPerIteration is {} count&&count!=reference.OperationsPerIteration)
                throw new LabException("invalid_plan","Explicit operation count differs from the retained calibration.");
            policy=policy with { OperationsPerIteration=reference.OperationsPerIteration };
            policy.Validate();
        }
        var plan=new LiveBenchmarkPlan(Wire.Id("live-benchmark-plan"),handshake.Target,scenario,variant,handshake.Runtime!,modification,
            compilation,binding,policy,maximumDurationSeconds,calibrationResultId,DateTimeOffset.UtcNow);
        Catalog.Put(plan.Id,"live_benchmark_plan",plan); return plan;
    }

    private static void RequireLiveHarness(HarnessHandshake handshake,long epoch)
    {
        if(handshake.Target.Epoch!=epoch) throw new LabException("stale_epoch","Live benchmark target epoch differs.");
        if(handshake.Tainted) throw new LabException("target_tainted","Live target is not quiescent.");
        if(handshake.LiveBenchmarkProtocol!="live-calibrated-batches/v1"||handshake.Runtime is null||handshake.Runtime.OwnedProcess)
            throw new LabException("capability_unsupported","Target requires the calibrated live harness protocol in a persistent process.");
        if(handshake.ProbeHelper?.Active==true) throw new LabException("observer_mismatch","Live benchmarking requires inactive timing probes.");
    }

    private async Task<CompilationRegimeEvidence?> LiveCompilationAsync(HarnessHandshake handshake,ScenarioDescriptor scenario,string variant,
        CompilationControlBinding? binding,CancellationToken cancellation)
    {
        var native=OperatingSystem.IsLinux()&&handshake.Runtime?.NativeProfilerPresent==true?await ProfilerAsync(handshake.Target.Id,cancellation):null;
        ExecutionQualification.Validate(handshake,"quiet-validation",binding is not null,native);
        var control=binding is null?null:await VerifyCompilationControlAsync(binding,cancellation);
        var plan=new ExperimentPlan("live",scenario.Id,scenario.Version,scenario.InputHash,[variant],1,1,0,0,"quiet-validation",false,
            "One persistent process.",false,120,new(),CompilationControl:binding);
        return await CompilationRegimeAsync(handshake.Target,handshake.Runtime,plan,scenario,control,cancellation,calibratedLive:true);
    }

    private LiveModification LiveModificationBinding(HarnessHandshake handshake,ScenarioDescriptor scenario,string variant,
        CompilationRegimeEvidence? compilation,string? mutationId)
    {
        string regime=compilation?.Regime??"original_normal_compile";
        CilCandidateRecord? candidate=null;
        if(regime is "candidate_rejit" or "original_body_rejit")
        {
            if(mutationId is null) throw new LabException("invalid_request","An active ReJIT measurement requires its retained mutation_id.");
            var root=Record<MutationRoot>(mutationId,"mutation");
            var outcome=Record<MutationOutcome>(root.Id+"-outcome","mutation_outcome");
            candidate=CilCandidate(root.CandidateId);
            var operation=Catalog.GetOperation(root.OperationId);
            if(operation.State!=OperationState.Completed||operation.ResultId!=outcome.Id||!outcome.Measurable||outcome.Restored||outcome.Tainted||
                outcome.Failure is not null||outcome.Epoch!=handshake.Target.Epoch||root.Target.Id!=handshake.Target.Id||
                root.ScenarioId!=scenario.Id||candidate.BodyHash!=compilation!.BodyHash||candidate.Scope!=compilation.MethodScope||
                candidate.ContentHash!=root.CandidateHash)
                throw new LabException("mutation_unverified","Live measurement does not match the verified active modification.");
            VerifyMutationJournal(root,candidate);
        }
        else if(mutationId is not null) throw new LabException("mutation_unverified","A mutation_id cannot label an unmodified or restored body.");
        string body=string.IsNullOrEmpty(compilation?.BodyHash)?handshake.Runtime!.EntryAssemblyHash:compilation.BodyHash;
        string scope=compilation?.MethodScope??"registered-scenario:"+scenario.Id;
        string id="live-modification-"+Wire.Hash(new { candidate_id=candidate?.Id,candidate_hash=candidate?.ContentHash,body,scope,variant,regime });
        return new(id,variant,regime,candidate?.Id,candidate?.ContentHash,mutationId,body,scope);
    }

    private async Task VerifyLivePlanAsync(LiveBenchmarkPlan plan,CancellationToken cancellation)
    {
        var handshake=await ScenariosAsync(plan.Target.Id,cancellation);
        RequireLiveHarness(handshake,plan.Target.Epoch);
        var scenario=handshake.Scenarios.SingleOrDefault(s=>s.Id==plan.Scenario.Id);
        if(handshake.Target!=plan.Target||Wire.Hash(handshake.Runtime)!=Wire.Hash(plan.Runtime)||scenario is null||Wire.Hash(scenario)!=Wire.Hash(plan.Scenario))
            throw new LabException("not_comparable","Live benchmark session, inputs, or runtime changed after plan freeze.");
        var compilation=await LiveCompilationAsync(handshake,scenario,plan.Variant,plan.CompilationControl,cancellation);
        if(Wire.Hash(compilation)!=Wire.Hash(plan.Compilation)||
            Wire.Hash(LiveModificationBinding(handshake,scenario,plan.Variant,compilation,plan.Modification.MutationId))!=Wire.Hash(plan.Modification))
            throw new LabException("not_comparable","Live modification or compilation identity changed during measurement.");
    }

    public async Task<Operation> StartLiveBenchmarkAsync(string planId,string grantId,string key,CancellationToken cancellation=default)
    {
        var plan=Record<LiveBenchmarkPlan>(planId,"live_benchmark_plan");
        string hash=Wire.Hash(new { planId,plan_hash=Wire.Hash(plan),grantId });
        if(Catalog.FindAccepted("local",key,hash) is {} replay) return replay;
        await VerifyLivePlanAsync(plan,cancellation);
        var grant=await AuthorizeTargetOrScratchAsync(grantId,plan.Target,Permission.ExecuteRegisteredScenario,null,cancellation);
        return Start("live_benchmark",plan.Target,key,hash,async(operation,token)=>
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
            var endsAt=DateTimeOffset.UtcNow.AddSeconds(plan.MaximumDurationSeconds);
            deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
            using var permit=await activityScheduler.EnterMeasurementAsync(deadline.Token);
            LiveBenchmarkSession? session=null; LiveBenchmarkSummary? summary=null; string? artifactId=null, failure=null;
            bool quiescent=true,submitted=false;
            try
            {
                await VerifyLivePlanAsync(plan,deadline.Token);
                await ReauthorizeTargetOrScratchAsync(grant,plan.Target,Permission.ExecuteRegisteredScenario,null,deadline.Token);
                Catalog.UseGrant(grant);
                var childGrant=grant with { Workspace=Catalog.Root,TargetId=plan.Target.Id,StartTimeUtcTicks=plan.Target.StartTimeUtcTicks,ContentHash=null,MethodScope=null };
                var request=new RunRequest(operation.Id,Wire.Id("live-run"),"live",plan.Scenario.Id,plan.Variant,
                    plan.Policy.OperationsPerIteration??plan.Policy.InitialOperations,0,true,true,plan.Target.Epoch,plan.Target,childGrant,
                    endsAt);
                submitted=true;
                session=await harness.BenchmarkAsync(request,plan.Policy,deadline.Token);
                quiescent=session.Quiescent;
                if(!quiescent) permit.RetainForUncertainTarget(plan.Target);
                summary=LiveBenchmarkStatistics.Summarize(session,plan.Policy);
                if(session.Iterations.Any(r=>r.Measurement.ExecutionId!=operation.Id||r.Measurement.InputHash!=plan.Scenario.InputHash||
                    r.Measurement.ScenarioVersion!=plan.Scenario.Version||r.Measurement.Variant!=plan.Variant))
                    throw new LabException("invalid_measurement","Live iteration identity differs from its frozen plan.");
                if(!session.Correct||!session.Quiescent) failure=session.Failure??"scenario_failed";
                if(summary.Quality=="invalid") failure??="invalid_measurement";
                if(deadline.IsCancellationRequested) failure??="cancelled";
                if(failure is null) await VerifyLivePlanAsync(plan,deadline.Token);
            }
            catch(Exception error) when(error is not OutOfMemoryException)
            {
                failure=error is LabException lab?lab.Code:error is OperationCanceledException?"cancelled":"live_benchmark_failed";
                if(submitted&&session is null) { quiescent=false; permit.RetainForUncertainTarget(plan.Target); }
            }
            if(session is not null)
            {
                var artifact=await Artifacts.WriteJsonAsync(operation.Id,new { plan_id=plan.Id,plan_hash=Wire.Hash(plan),session },"live-benchmark/v1");
                Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"live_benchmark"); artifactId=artifact.Id;
            }
            var result=new LiveBenchmarkResult(Wire.Id("live-benchmark-result"),plan.Id,Wire.Hash(plan),operation.Id,plan.Target,plan.Modification,
                failure is null?"completed":"failed",artifactId,summary,session is null?null:LiveComparability(plan,session),session?.OperationsPerIteration??0,failure);
            Catalog.Put(result.Id,"live_benchmark_result",result);
            Catalog.Transition(operation.Id,!quiescent?OperationState.Tainted:failure is null?OperationState.Completed:OperationState.Failed,result.Id,failure);
            return quiescent;
        });
    }

    private static string LiveComparability(LiveBenchmarkPlan plan,LiveBenchmarkSession session)=>Wire.Hash(new {
        plan.Scenario, runtime=Wire.Hash(plan.Runtime),plan.Target.RuntimeVersion,plan.Target.Architecture,session.Protocol,
        policy=plan.Policy with { InitialOperations=1,OperationsPerIteration=session.OperationsPerIteration },session.OverheadScope,
        control=plan.CompilationControl?.ResultId,callers=plan.Compilation?.CallerBindingsHash,flags=plan.Compilation?.RequestFlags });

    private async Task<(LiveBenchmarkResult Result,LiveBenchmarkPlan Plan,LiveBenchmarkSession Session)> ReadLiveBenchmarkAsync(string id,CancellationToken cancellation)
    {
        var result=Record<LiveBenchmarkResult>(id,"live_benchmark_result"); var plan=Record<LiveBenchmarkPlan>(result.PlanId,"live_benchmark_plan");
        var operation=Catalog.GetOperation(result.OperationId);
        if(result.PlanHash!=Wire.Hash(plan)||result.Target!=plan.Target||Wire.Hash(result.Modification)!=Wire.Hash(plan.Modification)||result.ArtifactId is null)
            throw new LabException("not_comparable","Live result lacks its frozen identity or raw evidence.");
        if(operation.Kind!="live_benchmark"||operation.ResultId!=result.Id||
            result.State=="completed"&&(operation.State!=OperationState.Completed||result.Failure is not null||result.Summary?.Quality is null or "invalid"))
            throw new LabException("not_comparable","Live result does not match its retained operation outcome.");
        var artifact=Record<Artifact>(result.ArtifactId,"artifact");
        if(artifact.Length>1_048_576) throw new LabException("resource_limit","Live iteration report exceeds its read budget.");
        using var document=System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact,cancellation),cancellation));
        var root=document.RootElement;
        var session=Wire.Deserialize<LiveBenchmarkSession>(root.GetProperty("session").GetRawText());
        if(root.GetProperty("plan_id").GetString()!=plan.Id||root.GetProperty("plan_hash").GetString()!=result.PlanHash||
            session.OperationsPerIteration!=result.OperationsPerIteration||
            session.Iterations.Any(r=>r.Measurement.ExecutionId!=result.OperationId||r.Measurement.InputHash!=plan.Scenario.InputHash||
                r.Measurement.ScenarioVersion!=plan.Scenario.Version||r.Measurement.Variant!=plan.Variant)||
            Wire.Hash(LiveBenchmarkStatistics.Summarize(session,plan.Policy))!=Wire.Hash(result.Summary)||LiveComparability(plan,session)!=result.ComparabilityHash)
            throw new LabException("artifact_corrupt","Live benchmark summary differs from retained raw measurements.");
        return (result,plan,session);
    }

    public async Task<IReadOnlyList<LiveBenchmarkGroup>> AggregateLiveBenchmarksAsync(IReadOnlyList<string> resultIds,CancellationToken cancellation=default)
    {
        if(resultIds.Count is <1 or >100||resultIds.Distinct().Count()!=resultIds.Count)
            throw new LabException("invalid_request","Select 1..100 distinct live benchmark results; duplicate measurements cannot be counted twice.");
        var rows=new List<LiveBenchmarkResult>();
        foreach(string id in resultIds)
        {
            var (result,_,_)=await ReadLiveBenchmarkAsync(id,cancellation);
            if(result.State!="completed"||result.Summary?.Quality=="invalid"||result.Summary?.MeanNanoseconds is not >0)
                throw new LabException("not_comparable","Failed, incomplete, or zero-cost live runs cannot be aggregated.");
            rows.Add(result);
        }
        return rows.GroupBy(r=>(r.Modification.Id,r.ComparabilityHash)).Select(group=>
        {
            var values=group.ToArray(); var summaries=values.Select(v=>v.Summary!).ToArray();
            var metrics=summaries[0].TelemetryPerOperation.Keys.Where(k=>summaries.All(s=>s.TelemetryPerOperation.ContainsKey(k)))
                .ToDictionary(k=>k,k=>summaries.Average(s=>s.TelemetryPerOperation[k]));
            double mean=summaries.Average(s=>s.MeanNanoseconds!.Value);
            double? runSd=values.Length>1?Math.Sqrt(summaries.Sum(s=>Math.Pow(s.MeanNanoseconds!.Value-mean,2))/(values.Length-1)):null;
            double? runCv=runSd/mean;
            var warnings=summaries.SelectMany(s=>s.Warnings).ToList();
            if(runCv>Record<LiveBenchmarkPlan>(values[0].PlanId,"live_benchmark_plan").Policy.MaximumCoefficientOfVariation)
                warnings.Add("between_run_variability");
            var distinctWarnings=warnings.Distinct().Order().ToArray();
            return new LiveBenchmarkGroup(group.Key.Id,group.Key.ComparabilityHash!,values[0].Modification with { MutationId=null },values.Select(r=>r.Id).ToArray(),
                values.Select(r=>new LiveBenchmarkObservation(r.Id,r.OperationId,r.Target,r.Modification.MutationId)).ToArray(),
                values.Select(r=>(r.Target.Id,r.Target.StartTimeUtcTicks)).Distinct().Count(),values.Length,summaries.Sum(s=>s.Samples),
                mean,summaries.Min(s=>s.MeanNanoseconds!.Value),summaries.Max(s=>s.MeanNanoseconds!.Value),runSd,runCv,
                summaries.All(s=>s.ThreadAllocatedBytesPerOperation.HasValue)?summaries.Average(s=>s.ThreadAllocatedBytesPerOperation!.Value):null,
                summaries.Average(s=>s.ProcessAllocatedBytesPerOperation!.Value),metrics,distinctWarnings.Length==0?"stable_live_observation":"unstable",distinctWarnings);
        }).ToArray();
    }

    public async Task<LiveBenchmarkContrast> CompareLiveBenchmarksAsync(IReadOnlyList<string> baselineIds,IReadOnlyList<string> candidateIds,CancellationToken cancellation=default)
    {
        if(baselineIds.Intersect(candidateIds).Any()) throw new LabException("not_comparable","Baseline and candidate selections must not overlap.");
        var baseline=await AggregateLiveBenchmarksAsync(baselineIds,cancellation); var candidate=await AggregateLiveBenchmarksAsync(candidateIds,cancellation);
        if(baseline.Count!=1||candidate.Count!=1||baseline[0].ComparabilityHash!=candidate[0].ComparabilityHash)
            throw new LabException("not_comparable","A contrast requires one modification per arm with identical workloads, calibrated batch size, protocol, and observer settings.");
        var b=baseline[0]; var c=candidate[0];
        if(b.Modification.Regime!=c.Modification.Regime&&!(b.Modification.Regime is "original_body_rejit" or "candidate_rejit"&&c.Modification.Regime is "original_body_rejit" or "candidate_rejit"))
            throw new LabException("not_comparable","Compilation regimes differ; compare matched original-body and candidate ReJIT arms.");
        double ratio=c.MeanNanoseconds/b.MeanNanoseconds;
        var warnings=b.Warnings.Concat(c.Warnings).Append("live_process_carryover_not_eliminated").Distinct().ToArray();
        return new(b,c,ratio,(ratio-1)*100,c.ThreadAllocatedBytesPerOperation-b.ThreadAllocatedBytesPerOperation,
            b.Quality=="unstable"||c.Quality=="unstable"?"unstable":"descriptive_only",warnings);
    }
}
