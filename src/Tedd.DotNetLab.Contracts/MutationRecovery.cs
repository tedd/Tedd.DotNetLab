namespace Tedd.DotNetLab.Contracts;

public record MutationRecoveryResult(string Id,string OperationId,string SourceOperationId,TargetIdentity Target,
    string NativeTransactionId,string CandidateHash,string OriginalBodyHash,string MethodScope,string ModuleLoadIdentity,
    string Phase,string SourceJournalHeadHash,string NativeManifestArtifactId,string ValidationArtifactId,
    bool OriginalBodyVerified,bool HealthVerified,bool PristineRuntime=false,bool PromotionEligible=false);
