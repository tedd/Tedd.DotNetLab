namespace Tedd.DotNetLab.Contracts;

public enum ContrastScale { Absolute, Log }
public record FactorialDesign(string Variant00,string Variant10,string Variant01,string Variant11,string ComponentAId,string ComponentBId,
    string CombinationId,IReadOnlyDictionary<string,ContrastScale> InteractionScales);
public record FactorialMetricBlock(string BlockId,string ClusterId,MetricValue Value00,MetricValue Value10,MetricValue Value01,MetricValue Value11,
    IReadOnlyList<string> RunIds);
public record InteractionEstimate(string Metric,string Unit,ContrastScale Scale,string Estimator,Availability Availability,string Quality,
    int MatchedBlocks,int IndependentUnits,double? Contrast,double? StandardError,double? DegreesOfFreedom,Interval? Interval,
    double? DifferenceP,double AlphaFamily,double AlphaLocal,int FamilySize,IReadOnlyList<string> ReasonCodes,IReadOnlyList<string> RunIds,
    string Meaning="Interaction is a four-arm contrast on the declared scale; it is not a sum of percentage improvements.");
public record FactorialResult(string Id,string ExecutionId,string PlanId,string PlanHash,string Algorithm,FactorialDesign Design,
    IReadOnlyDictionary<string,string> EffectVectorIds,IReadOnlyList<InteractionEstimate> Interactions,IReadOnlyList<string> RunIds,
    bool PromotionEligible=false,string Meaning="Standalone, joint, and conditional effects are retained; source promotion requires separate normal-build confirmation.");
public record FactorialIndex(string Id,string ExecutionId,string PlanId,string PlanHash,string Algorithm,string ArtifactId,FactorialDesign Design,
    IReadOnlyDictionary<string,string> EffectVectorIds,IReadOnlyList<InteractionEstimate> InteractionsWithoutRunIds,int InputRunCount,bool PromotionEligible=false);
