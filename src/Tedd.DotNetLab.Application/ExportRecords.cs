using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record ExportPlan(string Id,IReadOnlyList<FrozenRecordReference> Records,IReadOnlyList<Artifact> Artifacts,bool PrivacyFiltered,
    long MaximumBundleBytes,int MaximumDurationSeconds,string PolicyVersion="local-explicit-export/v1")
{
    public string ContentHash=>Wire.Hash(new { Records,Artifacts,PrivacyFiltered,MaximumBundleBytes,MaximumDurationSeconds,PolicyVersion });
}
public record ExportEntry(string Path,string Kind,string SourceId,string SourceHash,string ExportedHash,long Length,string MediaType,
    string SchemaVersion,bool Redacted,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? SourceRevision=null);
public record ExportOmission(string Kind,string Id,string Reason);
public record ExportManifest(string ExportId,string PlanId,string PlanHash,string SchemaVersion,string PolicyVersion,bool PrivacyFiltered,
    IReadOnlyList<ExportEntry> Entries,IReadOnlyList<ExportOmission> Omissions,
    string Meaning="Only the declared inclusion list is exported. Metadata-only filtering is not a claim that arbitrary diagnostic content was anonymized.");
public record ExportResult(string Id,string OperationId,string PlanId,string PlanHash,string ArtifactId,string ManifestHash,
    int IncludedRecords,int IncludedArtifacts,int OmittedPayloads,bool PrivacyFiltered,string State="verified");
