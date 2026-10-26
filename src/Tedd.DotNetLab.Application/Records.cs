using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using System.Text.Json.Serialization;

namespace Tedd.DotNetLab.Application;

public record ExecutionRecord(string Id,string PlanId,string PlanHash,TargetIdentity Target,string State,
    IReadOnlyList<string> RunIds,DateTimeOffset StartedAt,DateTimeOffset? EndedAt=null,string? Failure=null,string? CaptureId=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? AnalysisId=null);
public record RunRecord(string Id,string ExecutionId,string PlanId,string PlanHash,int BlockOrdinal,int ArmOrdinal,
    TargetIdentity Target,ScenarioDescriptor Scenario,string ProfileHash,string ObserverState,Measurement Measurement,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] RuntimeFootprint? Runtime=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] CompilationRegimeEvidence? Compilation=null);
public record CompilationRegimeEvidence(string Regime,long Epoch,string MethodScope,string BodyHash,string RejitId,int RequestFlags,
    string CallerBindingsHash,string InlinerCoverage,string? CompilationControlId=null,
    string Meaning="Observed active profiler state around measurement; tiering/PGO history and exact per-invocation native-version attribution remain unestablished.");
public record AnalysisIndex(string Id,string CaptureId,string ArtifactId,string ParserVersion,EvidenceQuality Quality);
public record Hypothesis(string Id,string Mechanism,string Prediction,string State,IReadOnlyList<string> EvidenceIds);
public record Candidate(string Id,string Name,string BuildIdentity,IReadOnlyList<string> Components);
public record WorkItem(string Id,string Question,int Priority,string State,IReadOnlyList<string> Dependencies,IReadOnlyList<string> EvidenceIds);
public record ManualDecision(string Id,string ComparisonId,string Action,string Rationale);
public record Baseline(string Id,IReadOnlyList<string> RunIds,string Role,string ManifestArtifactId,BaselineFingerprint Fingerprint,
    IReadOnlyList<string> UnknownFields);
public record BaselineAlias(string Id,string Name,string BaselineId,long BaselineRevision,string BaselineHash);
public record BaselineFingerprint(string ScenarioId,string ScenarioVersion,string InputHash,string Variant,ResetLevel ResetLevel,
    int OperationsPerRun,int WarmupBatches,string ProfileHash,string ObserverState,string RuntimeVersion,string Architecture,
    string CodeIdentityHash,string RuntimeSettingsHash,long Epoch,string CompilationRegime,string? NativeProcessScope=null);
public record BaselineSource(FrozenRecordReference Run,string MeasurementArtifactId,string FingerprintHash);
public record BaselineManifest(string Id,string Role,BaselineFingerprint Fingerprint,IReadOnlyList<BaselineSource> Sources,
    IReadOnlyList<string> UnknownFields,string SchemaVersion=Wire.Version);
