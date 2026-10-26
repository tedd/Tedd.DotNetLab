using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using System.Text.Json.Serialization;

namespace Tedd.DotNetLab.Application;

public record BuildFile(string Name,string ArtifactId);
public record NormalBuild(string Id,string EntryAssembly,IReadOnlyList<BuildFile> Files,string ContentHash,string RuntimeVersion,string Architecture);
public record FrozenRecordReference(string Id,long Revision,string Hash);
public record ConfirmationPlan(string Id,string SelectedCandidateId,string SourcePatchArtifactId,string SourceEquivalenceRationale,
    string BaselineBuildId,string CandidateBuildId,string BaselineVariant,string CandidateVariant,ExperimentPlan Experiment,
    IReadOnlyList<string> ExploratoryEvidenceIds,DateTimeOffset FrozenAt,IReadOnlyList<FrozenRecordReference>? FrozenEvidence=null)
{
    public string ContentHash=>Wire.Hash(new { SelectedCandidateId,SourcePatchArtifactId,SourceEquivalenceRationale,
        BaselineBuildId,CandidateBuildId,BaselineVariant,CandidateVariant,Experiment,ExploratoryEvidenceIds,FrozenAt,FrozenEvidence });
}
public record ConfirmationExecution(string Id,string PlanId,string PlanHash,string State,IReadOnlyList<string> SessionIds,
    DateTimeOffset StartedAt,DateTimeOffset? EndedAt=null,string? Failure=null,string? OperationId=null);
public record NormalBuildSession(string Id,string ExecutionId,string PlanId,int BlockOrdinal,int ArmOrdinal,string BuildId,string Variant,
    TargetIdentity Target,RuntimeFootprint Runtime,Measurement Measurement,string MeasurementArtifactId,DateTimeOffset StartedAt,
    DateTimeOffset ExitedAt,int ExitCode,string CompilationRegime="normal-build/fresh-process");
public record ConfirmationDecision(string Id,string PlanId,string ExecutionId,string SelectedCandidateId,string BaselineBuildId,
    string CandidateBuildId,string SourcePatchArtifactId,string ComparisonId,bool PromotionEligible,IReadOnlyList<string> ReasonCodes,
    string Meaning="Eligible evidence for source review; no source edit, commit, deployment, or publication is performed.",
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? ObjectiveVectorId=null);
