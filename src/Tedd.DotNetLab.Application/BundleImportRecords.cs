using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record BundleImportPlan(string Id,Artifact Bundle,string ManifestHash,long MaximumDecompressedBytes,int MaximumDurationSeconds,
    string Policy="verified_offline_evidence_only/v1");
public record ImportedEvidenceMember(string Id,string ImportId,string SourcePath,string SourceId,string SourceKind,long? SourceRevision,
    string SourceHash,string ExportedHash,long Length,string MediaType,bool Redacted,string ArtifactId,string ArtifactHash,
    string Qualification="imported_hash_verified_origin_unverified");
public record BundleImportResult(string Id,string OperationId,string PlanId,string PlanHash,string BundleArtifactId,string BundleHash,
    string ManifestArtifactId,string ManifestHash,int Records,int Artifacts,int OmittedPayloads,bool PrivacyFiltered,
    string Qualification="imported_hash_verified_origin_unverified",
    string Meaning="Selected archive payloads retained for offline review; hashes establish byte integrity, not origin, live target identity, grants, or experiment certification.");
