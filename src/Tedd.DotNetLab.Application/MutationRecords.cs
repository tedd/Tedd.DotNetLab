using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record CilInput(string SchemaVersion,byte[] Body,string Transformation);
public record ProbeConfiguration(string Version,string HelperHash,string HelperMvid,uint EnterToken,uint ExitToken,
    bool Timing,int Threads,int MaximumNesting,byte[] BaseBody,string ExceptionCoverage="nonthrowing_leaf_only",CilPolicy? Policy=null,
    FaultProbePolicy? Fault=null);
public record CilCandidateRecord(string Id,string SchemaVersion,TargetIdentity SourceTarget,StoredMethod Method,
    string ModuleLoadIdentity,string SourceInspectionId,string SourceManifestArtifactId,string BodyArtifactId,
    string BodyHash,string BaseIlHash,string OriginalIlHash,string Transformation,IReadOnlyList<string> Components,ProbeConfiguration? Probe=null,CilPolicy? Policy=null,
    IReadOnlyList<NativeInlinerBinding>? Inliners=null)
{
    public string Scope=>Method.ModuleMvid+"/"+Method.Token+"/"+Method.ModuleHash;
    public string ContentHash=>Wire.Hash(new { SchemaVersion,SourceTarget,Method,ModuleLoadIdentity,SourceInspectionId,
        SourceManifestArtifactId,BodyArtifactId,BodyHash,BaseIlHash,OriginalIlHash,Transformation,Components,Probe,Policy,Inliners });
}
public record MutationRoot(string Id,string OperationId,string CandidateId,string CandidateHash,TargetIdentity Target,
    string ScenarioId,string GrantId,string ExecutionGrantId,string NativeTransactionId,int SetupOperations,
    int MaximumDurationSeconds,string? RestoresTransactionId=null,int? ExpectedInvocationsPerOperation=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CompilationControlPlanId=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? NativeTrialPlanId=null);
public record MutationEvent(string Id,string TransactionId,int Sequence,string Phase,string? PreviousHash,
    long Epoch,JsonElement? Evidence,string? ErrorCode,DateTimeOffset CreatedAt);
public record MutationOutcome(string Id,string CandidateId,string Phase,long Epoch,bool Measurable,bool Restored,
    bool Tainted,string? Failure,bool PromotionEligible=false,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? CompilationControlResultId=null);
