using System.Text.Json.Serialization;

namespace Tedd.DotNetLab.Contracts;

public enum CapabilityState { Supported, Unsupported, Unknown, Blocked }
public enum Availability { Available, Partial, NotCollected, NotSupported, NotApplicable, Invalid, ArtifactExpired }
public enum Permission { Observe, ExecuteRegisteredScenario, InspectLiveCode, InstallProbes, ReplaceIl, DeleteEvidence, LaunchRegisteredBuild, ExportEvidence, AttachProfiler }
public enum OperationState { Accepted, Running, CancellationRequested, Completed, Failed, Interrupted, Tainted }
public enum CaptureState { Created, Opening, Collecting, Stopping, Draining, Sealed, Incomplete, Failed }
public enum EffectClass { Improved, Regressed, EquivalentWithinMargin, Inconclusive, NotComparable, Invalid }
public enum ResetLevel { LogicalState, WarmCache, ColdCacheControlled, FreshProcess }
public enum ThreadAffinity { Any, ApplicationThread }
public record Capability(CapabilityState State, string Reason);
public record TargetIdentity(string Id, int ProcessId, long StartTimeUtcTicks, string RuntimeInstance,
    string RuntimeVersion, string Architecture, long Epoch = 0);
public record TargetInfo(TargetIdentity Identity, string Name, IReadOnlyDictionary<string, Capability> Capabilities,
    string ObserverState = "other_observers_unknown");
public record Grant(string Id, string Workspace, string TargetId, long StartTimeUtcTicks, Permission Permission,
    DateTimeOffset ExpiresAt, int MaximumOperations, string? ContentHash = null,string? MethodScope=null);
public record ProviderSpec(string Name, long Keywords, int Level, IReadOnlyDictionary<string, string> Arguments);
public record CaptureProfile(string Name, string Version, IReadOnlyList<ProviderSpec> Providers,
    int BufferMiB, bool Rundown, IReadOnlyList<string> DataClasses)
{
    public string Hash => Wire.Hash(new { Name, Version, Providers, BufferMiB, Rundown, DataClasses });
}
public record ResourceLimits(long MaximumCaptureBytes = 2147483648, int MaximumDurationSeconds = 600,
    long DiskReserveBytes = 268435456, int DrainTimeoutSeconds = 30, int MaximumRows = 1000,
    int InlineBytes = 65536,long MaximumParserMemoryBytes=536870912,int MaximumParserDurationSeconds=300,
    int MaximumParserEvents=1000000,long MaximumParserOutputBytes=16777216,int MaximumConcurrentOperations=8,int MaximumQueuedActivities=64);
public record EvidenceQuality(string Coverage, string LossStatus, string ParserVersion,
    IReadOnlyList<string> Warnings, double? SymbolCoverage = null);
public record Provenance(string CaptureId, string ArtifactId, string ParserVersion, string ProfileHash);
public record MetricResult(string Metric, Availability Status, double? Value, string Unit, string Scope,
    string? Reason, EvidenceQuality? Quality = null, Provenance? Provenance = null);
public record Artifact(string Id, string Hash, long Length, string MediaType, string SchemaVersion,
    string Producer, bool Complete);
public record Operation(string Id, string Kind, OperationState State, long Revision, string? TargetId,
    string? ResultId, string? ErrorCode, DateTimeOffset CreatedAt);
public record Capture(string Id, TargetIdentity Target, CaptureProfile Profile, CaptureState State,
    string? ArtifactId, EvidenceQuality? Quality, DateTimeOffset CreatedAt, string? Reason = null);
public record ScenarioDescriptor(string Id, string Version, string InputHash, ResetLevel ResetLevel,
    ThreadAffinity Affinity, bool IsAsync, string CompletedWorkDefinition, IReadOnlyList<string> Variants,
    IReadOnlyList<string>? MutationScopes=null,IReadOnlyDictionary<string,int>? MutationInvocationsPerOperation=null);
public record RunRequest(string ExecutionId, string RunId, string BlockId, string ScenarioId, string Variant,
    int OperationCount, int WarmupBatches, bool ResetAfterWarmup, bool MeasureThreadAllocation,
    long ExpectedEpoch, TargetIdentity Target, Grant Grant, DateTimeOffset Deadline,string? ExpectedMethodScope=null,IReadOnlyList<string>? ExpectedCallerScopes=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)] bool RetainIndividualLatencies=false);
public record IndividualLatencyVector(string ObserverIdentity,string ObservationType,long Frequency,IReadOnlyList<long> ElapsedTicks,
    string Meaning="One completed operation per timer interval; observer costs and waiting remain included; no assumed overhead subtraction.");
public record BatchTelemetry(int Gen0Collections,int Gen1Collections,int Gen2Collections,long HeapSizeBytes,long FragmentedBytes,
    long TotalCommittedBytes,long PinnedObjectsCount,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? ThreadCpuCycles=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? ThreadCpuNanoseconds=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? InstructionsRetired=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? HardwareCpuCycles=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? BranchMisses=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] long? CacheMisses=null);
public record Measurement(string ExecutionId, string RunId, string BlockId, string Variant,
    long StartTicks, long EndTicks, long Frequency, long CompletedOperations, long ProcessAllocationBytes,
    long? ThreadAllocationBytes, int StartThreadId, int EndThreadId, bool Correct, string? Failure,
    string InputHash, string ScenarioVersion, bool Quiescent = true,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] IndividualLatencyVector? IndividualLatencies=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] BatchTelemetry? Telemetry=null)
{
    public double ElapsedNanoseconds => ((double)(EndTicks - StartTicks)) * 1e9 / Frequency;
    public double NanosecondsPerOperation => ElapsedNanoseconds / CompletedOperations;
}
public record ValidationResult(bool Correct, string? Reason = null, bool Quiescent = true);
public record AnalysisPolicy(double AlphaFamily = .05, int FamilySize = 1, double EquivalenceLowerRatio = .99,
    double EquivalenceUpperRatio = 1.01, double UsefulImprovementRatio = .99,
    double MeaningfulRegressionRatio = 1.01, int MinimumBlocks = 10);
public record ExperimentPlan(string Id, string ScenarioId, string ScenarioVersion, string InputHash,
    IReadOnlyList<string> Variants, int Blocks, int OperationsPerRun, int WarmupBatches,
    ulong Seed, string Profile, bool IndependentBlocks, string IndependenceJustification,
    bool Confirmatory, int MaximumDurationSeconds, AnalysisPolicy Analysis,
    string PrimaryMetric = "scenario.ns_per_operation", bool FreshNormalBuild = false,
    IReadOnlyList<string>? GuardrailMetrics = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<MetricPolicy>? GuardrailPolicies = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FrozenBaseline? Baseline=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] FactorialDesign? Factorial=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] MetricPolicy? PrimaryPolicy=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] DiagnosticWindowPolicy? DiagnosticWindows=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CompilationControlBinding? CompilationControl=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? TargetRuntimeVersion=null);
public record CompilationControlBinding(string ResultId,long Revision,string RecordHash,string ArtifactId,string ArtifactHash);
public record FrozenBaseline(string Id,long Revision,string Hash,string? AliasId=null,long? AliasRevision=null,string? AliasHash=null,
    string SamplingPolicy="fresh_matched_control_runs");
public record Interval(double Lower, double Upper, double Coverage);
public record Comparison(string Id, string Estimator, EffectClass EffectClass, string Quality,
    int IndependentBlocks, double? Ratio, double? ChangePercent, double? StandardError,
    double? DegreesOfFreedom, Interval? DifferenceIntervalRatio, Interval? EquivalenceIntervalRatio,
    double? DifferenceP, double? TostP, bool EquivalencePassed, double AlphaLocal,
    bool PromotionEligible, IReadOnlyList<string> ReasonCodes, IReadOnlyList<string> RunIds,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ObjectiveVectorId=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] MetricEffect? PrimaryEffect=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] int? InputRunCount=null);
public record NotebookRecord(string Id, string Kind, long Revision, string Hash, string Json,
    DateTimeOffset CreatedAt);
public record Page<T>(IReadOnlyList<T> Rows, bool Truncated, string? NextCursor);
