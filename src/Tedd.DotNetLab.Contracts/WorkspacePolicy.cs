namespace Tedd.DotNetLab.Contracts;

public record WorkspacePolicyRevision(string Id,long Revision,string SchemaVersion,string Hash,ResourceLimits Limits,DateTimeOffset CreatedAt);
public record WorkspaceDescriptor(string Id,string Root,int SchemaVersion,string PolicyRevisionId,string PolicyHash,DateTimeOffset CreatedAt);
public record WorkspaceContext(WorkspaceDescriptor Workspace,WorkspacePolicyRevision Policy);
public record OperationPolicyBinding(string OperationId,string Availability,WorkspacePolicyRevision? Policy,
    string Meaning="Immutable workspace policy selected at operation acceptance; absence on legacy operations does not establish historical settings.");
