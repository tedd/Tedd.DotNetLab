namespace Tedd.DotNetLab.Contracts;

public record DiagnosticWindowPolicy(double ResidualToleranceMilliseconds=1,string ParserVersion="traceevent-runtime/3",string WindowAlgorithm="target-clock-window/v1");
public record DiagnosticEvidenceLink(string ArtifactId,string Hash,string Algorithm,int InputRuns);

public enum MetricDirection { LowerIsBetter, HigherIsBetter }
public enum EffectScale { PositiveRatio, AbsoluteDifference }
public enum ParetoRelation { CandidateDominates, BaselineDominates, Unresolved }
public record MetricPolicy(string Metric,string Unit,string ObservationKind,MetricDirection Direction,EffectScale Scale,
    double EquivalenceLower,double EquivalenceUpper,double UsefulImprovement,double MeaningfulRegression,
    int MinimumBlocks=10,bool DescriptiveOnly=false);
public record MetricValue(string Metric,Availability Status,double? Value,string Unit,string ObservationKind,string Scope,string? Reason=null);
public record MetricPair(string BlockId,string ClusterId,MetricValue Baseline,MetricValue Candidate,string BaselineRunId,string CandidateRunId);
public record MetricEffect(string Metric,string Unit,string ObservationKind,string Scope,MetricPolicy Policy,string Estimator,
    Availability Availability,EffectClass EffectClass,string Quality,int IndependentBlocks,double? Effect,string EffectUnit,
    double? StandardError,string StandardErrorScale,double? DegreesOfFreedom,Interval? DifferenceInterval,Interval? EquivalenceInterval,
    double? DifferenceP,double? TostP,bool EquivalencePassed,bool NonInferiorityPassed,double AlphaFamily,double AlphaLocal,int FamilySize,
    IReadOnlyList<string> ReasonCodes,IReadOnlyList<string> Assumptions,IReadOnlyList<string> RunIds);
public record ObjectiveVector(string Id,string ExecutionId,string PlanId,string PlanHash,string BaselineVariant,string CandidateVariant,
    string Algorithm,IReadOnlyList<MetricEffect> Effects,ParetoRelation Pareto,bool PromotionEligible,IReadOnlyList<string> ReasonCodes,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DiagnosticEvidenceLink? DiagnosticEvidence=null);
public record ObjectiveVectorIndex(string Id,string ExecutionId,string PlanId,string PlanHash,string BaselineVariant,string CandidateVariant,
    string Algorithm,string DetailArtifactId,int InputRunCount,IReadOnlyList<MetricEffect> EffectsWithoutRunIds,
    ParetoRelation Pareto,bool PromotionEligible,IReadOnlyList<string> ReasonCodes,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] DiagnosticEvidenceLink? DiagnosticEvidence=null);
