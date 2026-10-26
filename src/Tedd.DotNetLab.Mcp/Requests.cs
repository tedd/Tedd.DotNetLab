using System.Text.Json.Serialization;
using System.ComponentModel;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Application;

namespace Tedd.DotNetLab.Mcp;

[JsonPolymorphic(TypeDiscriminatorPropertyName="action")]
[JsonDerivedType(typeof(DiscoverTargets),"targets")]
[JsonDerivedType(typeof(DiscoverProfiles),"profiles")]
[JsonDerivedType(typeof(DiscoverScenarios),"scenarios")]
[JsonDerivedType(typeof(DiscoverCapabilities),"capabilities")]
[JsonDerivedType(typeof(DiscoverProfiler),"profiler")]
[JsonDerivedType(typeof(DiscoverWorkspace),"workspace")]
[JsonDerivedType(typeof(DiscoverCompatibility),"compatibility")]
public abstract record DiscoverRequest;
public record DiscoverTargets(int Limit=100,string? Cursor=null) : DiscoverRequest;
public record DiscoverProfiles : DiscoverRequest;
public record DiscoverScenarios(string TargetId) : DiscoverRequest;
public record DiscoverCapabilities(string TargetId) : DiscoverRequest;
public record DiscoverProfiler(string TargetId) : DiscoverRequest;
public record DiscoverWorkspace : DiscoverRequest;
public record DiscoverCompatibility(string? TargetId=null) : DiscoverRequest;

[JsonPolymorphic(TypeDiscriminatorPropertyName="action")]
[JsonDerivedType(typeof(InspectRecord),"record")]
[JsonDerivedType(typeof(InspectArtifact),"artifact")]
[JsonDerivedType(typeof(InspectNative),"native")]
[JsonDerivedType(typeof(InspectMethods),"methods")]
[JsonDerivedType(typeof(InspectIl),"il")]
[JsonDerivedType(typeof(InspectOfflineNative),"offline_native")]
[JsonDerivedType(typeof(InspectNativeDiff),"native_diff")]
public abstract record InspectRequest;
public record InspectRecord(string Id,string? Revision=null) : InspectRequest;
public record InspectArtifact(string ArtifactId) : InspectRequest;
public record InspectNative(string TargetId,string MethodId,string ExpectedTargetEpoch,string GrantId,string? ModuleLoadId=null) : InspectRequest;

[JsonPolymorphic(TypeDiscriminatorPropertyName="action")]
[JsonDerivedType(typeof(CreateHypothesis),"create_hypothesis")]
[JsonDerivedType(typeof(ReviseHypothesis),"revise_hypothesis")]
[JsonDerivedType(typeof(CreateCandidate),"create_candidate")]
[JsonDerivedType(typeof(CreatePlan),"create_plan")]
[JsonDerivedType(typeof(CreateWorkItem),"create_work_item")]
[JsonDerivedType(typeof(ReviseWorkItem),"revise_work_item")]
[JsonDerivedType(typeof(RecordDecision),"record_decision")]
[JsonDerivedType(typeof(RegisterCilCandidate),"register_cil_candidate")]
[JsonDerivedType(typeof(RegisterNormalBuild),"register_normal_build")]
[JsonDerivedType(typeof(CreateConfirmationPlan),"create_confirmation_plan")]
[JsonDerivedType(typeof(CreateBaseline),"create_baseline")]
[JsonDerivedType(typeof(SetBaselineAlias),"set_baseline_alias")]
[JsonDerivedType(typeof(PrepareExport),"prepare_export")]
[JsonDerivedType(typeof(PrepareBundleImport),"prepare_bundle_import")]
[JsonDerivedType(typeof(PrepareDeletion),"prepare_deletion")]
[JsonDerivedType(typeof(PrepareRetention),"prepare_retention")]
[JsonDerivedType(typeof(PinEvidence),"pin_evidence")]
[JsonDerivedType(typeof(PrepareReferenceRelease),"prepare_reference_release")]
[JsonDerivedType(typeof(CreateQuantilePlan),"create_quantile_plan")]
[JsonDerivedType(typeof(RegisterQuantileObservations),"register_quantile_observations")]
[JsonDerivedType(typeof(CreateIndividualLatencyPlan),"create_individual_latency_plan")]
[JsonDerivedType(typeof(RegisterQuantileHarnessRuns),"register_quantile_harness_runs")]
[JsonDerivedType(typeof(CreateCompilationControl),"create_compilation_control")]
[JsonDerivedType(typeof(CreateNativeTrial),"create_native_trial")]
[JsonDerivedType(typeof(CreateNativeSeries),"create_native_series")]
[JsonDerivedType(typeof(PrepareProfilerAttachment),"prepare_profiler_attachment")]
public abstract record ExperimentRequest;
public record PrepareProfilerAttachment(string TargetId,string ExpectedTargetEpoch) : ExperimentRequest;
public record CreateHypothesis(string Mechanism,string Prediction,IReadOnlyList<string> EvidenceIds) : ExperimentRequest;
public record ReviseHypothesis(string Id,string ExpectedRevision,string Mechanism,string Prediction,string State,IReadOnlyList<string> EvidenceIds) : ExperimentRequest;
public record CreateCandidate(string Name,string BuildIdentity,IReadOnlyList<string> Components) : ExperimentRequest;
public record CreatePlan(string ScenarioId,string ScenarioVersion,string InputHash,IReadOnlyList<string> Variants,
    int Blocks=10,int OperationsPerRun=1000,int WarmupBatches=1,string Seed="42",string Profile="quiet-validation",
    bool IndependentBlocks=false,string IndependenceJustification="",bool Confirmatory=false,
    int MaximumDurationSeconds=60,AnalysisPolicy? Analysis=null,string PrimaryMetric="scenario.ns_per_operation",
    IReadOnlyList<string>? GuardrailMetrics=null,IReadOnlyList<MetricPolicy>? GuardrailPolicies=null,
    string? BaselineId=null,string? BaselineAliasId=null,FactorialDesign? Factorial=null,MetricPolicy? PrimaryPolicy=null,DiagnosticWindowPolicy? DiagnosticWindows=null,string? CompilationControlId=null,
    [property:Description("Exact target runtime, including preview suffix; must match the harness handshake. Other .NET 10+ Windows x64 versions require quiet managed execution and leased runtime-image identities. Does not select the controller runtime or qualify native profiling.")]
    string? TargetRuntimeVersion=null) : ExperimentRequest;
public record CreateBaseline(IReadOnlyList<string> RunIds,string Role) : ExperimentRequest;
public record SetBaselineAlias(string Name,string BaselineId,string? ExpectedRevision=null) : ExperimentRequest;
public record PrepareExport(IReadOnlyList<string> RecordIds,IReadOnlyList<string> ArtifactIds,bool PrivacyFiltered=true,
    string MaximumBundleBytes="1073741824",int MaximumDurationSeconds=300) : ExperimentRequest;
public record PrepareBundleImport(string ArtifactId,string MaximumDecompressedBytes="1073741824",int MaximumDurationSeconds=300) : ExperimentRequest;
public record PrepareDeletion(IReadOnlyList<string> ArtifactIds,string Reason) : ExperimentRequest;
public record PrepareRetention(string MaximumRawTraceBytes,int MaximumAgeDays) : ExperimentRequest;
public record PinEvidence(string ArtifactId,string OwnerId) : ExperimentRequest;
public record PrepareReferenceRelease(IReadOnlyList<ReferenceSelection> Selection,string Reason) : ExperimentRequest;
public record CreateQuantilePlan(QuantilePlanRequest Design) : ExperimentRequest;
public record RegisterQuantileObservations(string PlanId,string ArtifactId) : ExperimentRequest;
public record RegisterQuantileHarnessRuns(string PlanId,IReadOnlyList<QuantileRunPair> Blocks) : ExperimentRequest;
public record CreateCompilationControl(string CandidateId,string ScenarioId,string HarnessVariant="baseline",int Blocks=10,int OperationsPerRun=1000,int WarmupBatches=1,int MaximumDurationSeconds=60) : ExperimentRequest;
public record CreateNativeTrial(string CandidateId,string CompilationControlId,int Blocks=10,string Seed="42",int MaximumDurationSeconds=120) : ExperimentRequest;
public record CreateNativeSeries(IReadOnlyList<string> TrialPlanIds,AnalysisPolicy? Analysis=null,bool IndependentSessions=false,string IndependenceJustification="",
    string CarryoverJustification="",int MaximumDurationSeconds=300) : ExperimentRequest;
public record CreateIndividualLatencyPlan(string ScenarioId,string ScenarioVersion,string InputHash,string Variant,int Operations=100,int WarmupBatches=1,int MaximumDurationSeconds=60,string? CompilationControlId=null,
    [property:Description("Exact target runtime, including preview suffix; must match the harness handshake. Other .NET 10+ Windows x64 versions require quiet managed execution and leased runtime-image identities. Does not select the controller runtime or qualify native profiling.")]
    string? TargetRuntimeVersion=null) : ExperimentRequest;
public record CreateWorkItem(string Question,int Priority,IReadOnlyList<string> Dependencies,IReadOnlyList<string> EvidenceIds) : ExperimentRequest;
public record ReviseWorkItem(string Id,string ExpectedRevision,string Question,int Priority,string State,IReadOnlyList<string> Dependencies,IReadOnlyList<string> EvidenceIds) : ExperimentRequest;
public record RecordDecision(string ComparisonId,string Decision,string Rationale) : ExperimentRequest;
public record RegisterCilCandidate(string BodyArtifactId,string InspectionId,string BaseIlHash,IReadOnlyList<string> Components) : ExperimentRequest;
public record RegisterNormalBuild(string EntryAssembly,IReadOnlyList<BuildFile> Files) : ExperimentRequest;
public record CreateConfirmationPlan(string SelectedCandidateId,string SourcePatchArtifactId,string SourceEquivalenceRationale,
    string BaselineBuildId,string CandidateBuildId,string BaselineVariant,string CandidateVariant,ExperimentPlan Experiment,
    IReadOnlyList<string> ExploratoryEvidenceIds,string? BaselineId=null,string? BaselineAliasId=null) : ExperimentRequest;

[JsonPolymorphic(TypeDiscriminatorPropertyName="action")]
[JsonDerivedType(typeof(StartCapture),"start_capture")]
[JsonDerivedType(typeof(StartExecution),"start")]
[JsonDerivedType(typeof(OperationStatus),"status")]
[JsonDerivedType(typeof(CancelOperation),"cancel")]
[JsonDerivedType(typeof(StopCapture),"stop_capture")]
[JsonDerivedType(typeof(StartConfirmation),"start_confirmation")]
[JsonDerivedType(typeof(StartExport),"start_export")]
[JsonDerivedType(typeof(StartBundleImport),"import_bundle")]
[JsonDerivedType(typeof(StartImportedCaptureAnalysis),"analyze_imported_capture")]
[JsonDerivedType(typeof(StartDeletion),"start_deletion")]
[JsonDerivedType(typeof(StartArtifactRecovery),"recover_artifacts")]
[JsonDerivedType(typeof(StartCatalogBackup),"backup_catalog")]
[JsonDerivedType(typeof(CheckpointCatalog),"checkpoint_catalog")]
[JsonDerivedType(typeof(StartReferenceRelease),"release_references")]
[JsonDerivedType(typeof(StartStackAnalysis),"analyze_stacks")]
[JsonDerivedType(typeof(StartTraceAnalysis),"analyze_capture")]
[JsonDerivedType(typeof(StartQuantileComparison),"compare_quantiles")]
[JsonDerivedType(typeof(StartIndividualLatencies),"individual_latencies")]
[JsonDerivedType(typeof(StartNativeSeriesComparison),"compare_native_series")]
[JsonDerivedType(typeof(RegisterNativeSeriesResults),"register_native_series_results")]
[JsonDerivedType(typeof(StartProfilerAttachment),"attach_profiler")]
[JsonDerivedType(typeof(ReconcileProfilerAttachment),"reconcile_profiler_attachment")]
[JsonDerivedType(typeof(StartJitDisasm),"capture_jit_disasm")]
public abstract record RunRequest;
public record StartProfilerAttachment(string PlanId,string GrantId,string IdempotencyKey) : RunRequest;
public record ReconcileProfilerAttachment(string OperationId,string GrantId) : RunRequest;
public record StartJitDisasm(string MethodFilter,string ScenarioId,string Variant,string GrantId,string IdempotencyKey,string? BuildId=null,string? ExecutablePath=null,int Operations=1000,int WarmupBatches=5,bool TieredPgo=true,int MaximumDurationSeconds=60) : RunRequest;
public record StartCapture(string TargetId,string ExpectedTargetEpoch,string Profile,int DurationSeconds,string GrantId,string IdempotencyKey) : RunRequest;
public record StartExecution(string TargetId,string ExpectedTargetEpoch,string PlanId,string GrantId,string IdempotencyKey,string? ObservationGrantId=null) : RunRequest;
public record OperationStatus(string OperationId) : RunRequest;
public record CancelOperation(string OperationId) : RunRequest;
public record StopCapture(string OperationId) : RunRequest;
public record StartConfirmation(string PlanId,string GrantId,string IdempotencyKey) : RunRequest;
public record StartExport(string PlanId,string GrantId,string IdempotencyKey) : RunRequest;
public record StartBundleImport(string PlanId,string IdempotencyKey) : RunRequest;
public record StartImportedCaptureAnalysis(string MemberId,string IdempotencyKey) : RunRequest;
public record StartDeletion(string PlanId,string GrantId,string IdempotencyKey) : RunRequest;
public record StartArtifactRecovery(string IdempotencyKey,int MaximumEntries=10000,string MaximumVerifiedBytes="17179869184",int MaximumDurationSeconds=300) : RunRequest;
public record StartCatalogBackup(string IdempotencyKey) : RunRequest;
public record CheckpointCatalog(bool Truncate=false) : RunRequest;
public record StartReferenceRelease(string PlanId,string GrantId,string IdempotencyKey) : RunRequest;
public record StartStackAnalysis(string CaptureId,string IdempotencyKey,StackAnalysisPolicy? Policy=null) : RunRequest;
public record StartTraceAnalysis(string CaptureId,string IdempotencyKey) : RunRequest;
public record StartQuantileComparison(string DatasetId,string IdempotencyKey) : RunRequest;
public record StartIndividualLatencies(string TargetId,string ExpectedTargetEpoch,string PlanId,string GrantId,string IdempotencyKey) : RunRequest;
public record StartNativeSeriesComparison(string DatasetId,string IdempotencyKey) : RunRequest;
public record RegisterNativeSeriesResults(string PlanId,IReadOnlyList<string> ResultIds,string IdempotencyKey) : RunRequest;

[JsonPolymorphic(TypeDiscriminatorPropertyName="view")]
[JsonDerivedType(typeof(QueryRecord),"record")]
[JsonDerivedType(typeof(QueryHistory),"history")]
[JsonDerivedType(typeof(QueryMetric),"metric")]
[JsonDerivedType(typeof(QueryGc),"gc_timeline")]
[JsonDerivedType(typeof(QueryCounters),"counters")]
[JsonDerivedType(typeof(QueryCounterWindow),"counter_window")]
[JsonDerivedType(typeof(QueryAvailability),"available_evidence")]
[JsonDerivedType(typeof(QueryArtifact),"artifact_slice")]
[JsonDerivedType(typeof(QueryRunWindow),"run_window")]
[JsonDerivedType(typeof(QuerySuspensions),"suspensions")]
[JsonDerivedType(typeof(QueryExport),"verify_export")]
[JsonDerivedType(typeof(QueryBundleImport),"verify_bundle_import")]
[JsonDerivedType(typeof(QueryImportedMembers),"imported_members")]
[JsonDerivedType(typeof(QueryControllerResources),"controller_resources")]
[JsonDerivedType(typeof(QueryOperationPolicy),"operation_policy")]
[JsonDerivedType(typeof(QueryRetention),"artifact_retention")]
[JsonDerivedType(typeof(QueryArtifactRecovery),"artifact_recovery")]
[JsonDerivedType(typeof(QueryCatalogBackup),"verify_catalog_backup")]
[JsonDerivedType(typeof(QueryArtifactReferences),"artifact_references")]
[JsonDerivedType(typeof(QueryHotspots),"hotspots")]
[JsonDerivedType(typeof(QuerySampledMethod),"sampled_method")]
[JsonDerivedType(typeof(QueryIndividualLatencies),"individual_latency_slice")]
[JsonDerivedType(typeof(QueryNativeTrial),"verify_native_trial")]
[JsonDerivedType(typeof(QueryNativeSeries),"native_series_comparison")]
[JsonDerivedType(typeof(QueryJitInlining),"jit_inlining")]
[JsonDerivedType(typeof(QueryJitMethods),"jit_methods")]
[JsonDerivedType(typeof(QueryContentions),"contentions")]
[JsonDerivedType(typeof(QueryJitDisasm),"jit_disasm")]
public abstract record QueryRequest;
public record QueryRecord(string Id,string? Revision=null) : QueryRequest;
public record QueryHistory(string Kind,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryMetric(string AnalysisId,string Metric) : QueryRequest;
public record QueryGc(string AnalysisId,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryCounters(string AnalysisId,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryCounterWindow(string AnalysisId,string Name,double StartMilliseconds,double EndMilliseconds,string? Kind=null,string? Source=null,string? Series=null) : QueryRequest;
public record QueryAvailability(string CaptureId) : QueryRequest;
public record QueryArtifact(string ArtifactId,string Offset="0",int Count=16384) : QueryRequest;
public record QueryRunWindow(string AnalysisId,string RunId,double ResidualToleranceMilliseconds=1) : QueryRequest;
public record QuerySuspensions(string AnalysisId,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryExport(string ResultId) : QueryRequest;
public record QueryBundleImport(string ResultId) : QueryRequest;
public record QueryImportedMembers(string ImportId,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryControllerResources(string? OperationId=null) : QueryRequest;
public record QueryOperationPolicy(string OperationId) : QueryRequest;
public record QueryRetention(string ArtifactId) : QueryRequest;
public record QueryArtifactRecovery(string ResultId,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryCatalogBackup(string BackupId) : QueryRequest;
public record QueryArtifactReferences(string ArtifactId,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryHotspots(string ResultId,int Limit=100,string? Cursor=null,string Ranking="inclusive") : QueryRequest;
public record QuerySampledMethod(string ResultId,string Selector) : QueryRequest;
public record QueryIndividualLatencies(string RunId,int Offset=0,int Count=100) : QueryRequest;
public record QueryNativeTrial(string ResultId) : QueryRequest;
public record QueryNativeSeries(string ResultId) : QueryRequest;
public record QueryJitInlining(string AnalysisId,string? Filter=null,bool FailuresOnly=true,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryJitMethods(string AnalysisId,string? Filter=null,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryContentions(string AnalysisId,int Limit=100,string? Cursor=null) : QueryRequest;
public record QueryJitDisasm(string ResultId,string? MethodSubstring=null,string? TierFilter=null,bool IncludeAsm=true,int MaxAsmChars=32768) : QueryRequest;
public record CompareRequest(string ExecutionId,bool NormalBuildConfirmation=false,bool IncludeObjectives=false,bool IncludeFactorial=false);

public record InspectMethods(string ArtifactId,string Filter="",int Limit=100,string? Cursor=null) : InspectRequest;
public record InspectIl(string ArtifactId,string MethodToken) : InspectRequest;
public record InspectOfflineNative(string ArtifactId) : InspectRequest;
public record InspectNativeDiff(string OriginalArtifactId,string CandidateArtifactId,int Limit=100) : InspectRequest;

[JsonPolymorphic(TypeDiscriminatorPropertyName="action")]
[JsonDerivedType(typeof(PreflightCil),"preflight")]
[JsonDerivedType(typeof(ApplyCil),"apply")]
[JsonDerivedType(typeof(RestoreCil),"restore_original")]
[JsonDerivedType(typeof(RecoverMutation),"recover_interrupted")]
[JsonDerivedType(typeof(PrepareProbe),"prepare_probe")]
[JsonDerivedType(typeof(FlushProbe),"flush_probes")]
[JsonDerivedType(typeof(PrepareOriginalControl),"prepare_original_control")]
[JsonDerivedType(typeof(RunCompilationControl),"run_compilation_control")]
[JsonDerivedType(typeof(RunNativeTrial),"run_native_trial")]
public abstract record PatchRequest;
public record PreflightCil(string TargetId,string ExpectedTargetEpoch,string CandidateId,string GrantId) : PatchRequest;
public record ApplyCil(string TargetId,string ExpectedTargetEpoch,string CandidateId,string GrantId,string ExecutionGrantId,
    string ScenarioId,string IdempotencyKey,int SetupOperations=100,int MaximumDurationSeconds=30) : PatchRequest;
public record RestoreCil(string TransactionId,string ExpectedTargetEpoch,string GrantId,string ExecutionGrantId,string IdempotencyKey) : PatchRequest;
public record RecoverMutation(string TransactionId,string ExpectedTargetEpoch,string GrantId,string ExecutionGrantId,string IdempotencyKey) : PatchRequest;
public record PrepareProbe(string InspectionId,string BaseIlHash,bool Timing=false,int Threads=32,int MaximumNesting=64,bool TrackExceptions=false) : PatchRequest;
public record FlushProbe(string TargetId,string ExpectedTargetEpoch,string CandidateId,string GrantId,bool Restart=true) : PatchRequest;
public record PrepareOriginalControl(string InspectionId) : PatchRequest;
public record RunCompilationControl(string PlanId,string MutationGrantId,string ExecutionGrantId,string InspectionGrantId,string IdempotencyKey) : PatchRequest;
public record RunNativeTrial(string PlanId,string OriginalGrantId,string CandidateGrantId,string ExecutionGrantId,string InspectionGrantId,string IdempotencyKey) : PatchRequest;
