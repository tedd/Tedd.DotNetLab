using System.Text.Json;
using Tedd.DotNetLab.Artifacts;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record RecoveryFinding(string Identity,string Kind,string Integrity,long? Length=null,string? Reason=null);
public record ArtifactRecoveryReport(string Id,IReadOnlyList<RecoveryFinding> Findings,long VerifiedBytes,int VisitedEntries,
    bool Truncated,string Version="artifact-recovery/v1");
public record ArtifactRecoveryIndex(string Id,string ArtifactId,int Findings,int RecoveredArtifacts,long VerifiedBytes,bool Truncated);

public sealed partial class LabService
{
    public Operation StartArtifactRecovery(string key,int maximumEntries=10000,long maximumVerifiedBytes=17179869184,int maximumDurationSeconds=300)
    {
        if(maximumEntries is <1 or >10000||maximumVerifiedBytes is <1 or >1099511627776||maximumDurationSeconds is <1 or >600||maximumDurationSeconds>Limits.MaximumDurationSeconds)
            throw new LabException("resource_limit","Invalid artifact recovery entry, byte, or duration budget.");
        string scope="artifact-recovery";
        return Start(scope,new(scope,0,0,scope,"workspace","local"),key,Wire.Hash(new { maximumEntries,maximumVerifiedBytes,maximumDurationSeconds }),async(operation,cancellation)=>
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(maximumDurationSeconds));
            var findings=new List<RecoveryFinding>(); var known=new Dictionary<string,Artifact>(); var verified=new Dictionary<string,long>();
            long verifiedBytes=0; int visited=0,recovered=0; bool truncated=false;
            async Task<string> Verify(Artifact artifact)
            {
                if(artifact.Length<0) return "artifact_manifest_corrupt";
                if(Catalog.Retention(artifact.Id).DeletionState is not null) return "artifact_expired";
                if(verified.TryGetValue(artifact.Hash,out long length)&&length==artifact.Length) return "verified";
                if(artifact.Length>maximumVerifiedBytes-verifiedBytes) { truncated=true; return "not_verified_budget"; }
                // Charge attempted bytes, including failed integrity checks, against the verification budget.
                verifiedBytes+=artifact.Length;
                try { await Artifacts.VerifyAsync(artifact.Hash,artifact.Length,deadline.Token); verified[artifact.Hash]=artifact.Length; return "verified"; }
                catch(LabException error) when(error.Code is "artifact_expired" or "artifact_corrupt" or "invalid_path")
                { return error.Code=="artifact_expired"?"artifact_missing":error.Code; }
                catch(IOException) { return "io_unavailable"; }
            }
            string? cursor=null;
            do
            {
                var page=Catalog.List("artifact",100,cursor);
                foreach(var record in page.Rows)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if(visited++==maximumEntries) { truncated=true; break; }
                    var artifact=Wire.Deserialize<Artifact>(record.Json);
                    if(record.Hash!=Wire.Hash(artifact)||artifact.Id!=record.Id||artifact.Id!="art-"+artifact.Hash)
                    { findings.Add(new(record.Id,"catalog_artifact","artifact_manifest_corrupt")); continue; }
                    known[artifact.Id]=artifact;
                    findings.Add(new(artifact.Id,"catalog_artifact",await Verify(artifact),artifact.Length));
                }
                if(truncated&&visited>maximumEntries) break;
                cursor=page.NextCursor;
            } while(cursor is not null);
            if(visited<=maximumEntries)
            foreach(var entry in Artifacts.RecoveryFiles(maximumEntries-visited==0?1:maximumEntries-visited))
            {
                deadline.Token.ThrowIfCancellationRequested();
                if(entry.Kind=="scan_truncated"||visited++==maximumEntries) { truncated=true; break; }
                if(entry.Kind=="directory") continue;
                if(entry.Kind=="sealed_file")
                {
                    string id="art-"+entry.Hash;
                    if(known.ContainsKey(id)) continue;
                    var unknown=new Artifact(id,entry.Hash!,entry.Length!.Value,"application/octet-stream",Wire.Version,"unclaimed",false);
                    string state=await Verify(unknown);
                    findings.Add(new(entry.RelativePath,"orphan_file",state=="verified"?"orphan_unclaimed":state,entry.Length,
                        "No durable catalog ownership or completion claim is inferred from a filename."));
                    continue;
                }
                if(entry.Kind!="seal_receipt") { findings.Add(new(entry.RelativePath,entry.Kind,entry.Kind=="unsafe_path"?"invalid_path":"unclaimed",entry.Length)); continue; }
                try
                {
                    var receipt=await Artifacts.ReadSealReceiptAsync(entry,deadline.Token); var artifact=receipt.Artifact;
                    if(known.TryGetValue(artifact.Id,out var linked))
                    {
                        findings.Add(new(entry.RelativePath,"seal_receipt",Wire.Hash(linked)==receipt.ManifestHash?"catalog_linked":"receipt_conflict",entry.Length));
                        continue;
                    }
                    if(Artifacts.ReceiptHasPartial(entry))
                    { findings.Add(new(entry.RelativePath,"seal_receipt","receipt_before_move",entry.Length,
                        "The original partial file is present; this observation does not establish whether its writer is still active.")); continue; }
                    string state=await Verify(artifact);
                    if(state=="verified")
                    {
                        Catalog.Put(artifact.Id,"artifact",artifact); known[artifact.Id]=artifact; recovered++;
                        Catalog.AppendAudit("artifact_orphan_recovered",artifact.Id,receipt.ManifestHash);
                        state="recovered_manifest";
                    }
                    findings.Add(new(entry.RelativePath,"seal_receipt",state,entry.Length));
                }
                catch(IOException)
                { findings.Add(new(entry.RelativePath,"seal_receipt","io_unavailable",entry.Length)); }
                catch(Exception error) when(error is JsonException or LabException or ArgumentException)
                { findings.Add(new(entry.RelativePath,"seal_receipt","receipt_invalid",entry.Length,error is LabException lab?lab.Code:"malformed_or_unreadable")); }
            }
            string idReport=Wire.Id("artifact-recovery"); var report=new ArtifactRecoveryReport(idReport,findings,verifiedBytes,Math.Min(visited,maximumEntries),truncated);
            deadline.Token.ThrowIfCancellationRequested();
            var artifactReport=await Artifacts.WriteJsonAsync(operation.Id,report,"artifact-recovery/v1",cancellation:deadline.Token); Upsert(artifactReport.Id,"artifact",artifactReport);
            Catalog.Pin(artifactReport.Id,"artifact_recovery_report");
            var index=new ArtifactRecoveryIndex(idReport,artifactReport.Id,findings.Count,recovered,verifiedBytes,truncated);
            Catalog.Put(index.Id,"artifact_recovery",index); Catalog.Transition(operation.Id,OperationState.Completed,index.Id); return true;
        });
    }
    public async Task<ArtifactRecoveryReport> ArtifactRecoveryAsync(string resultId)
    {
        var index=Record<ArtifactRecoveryIndex>(resultId,"artifact_recovery"); var artifact=Record<Artifact>(index.ArtifactId,"artifact");
        if(artifact.Length>16777216) throw new LabException("resource_limit","Recovery report exceeds its read budget.");
        return Wire.Deserialize<ArtifactRecoveryReport>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact)));
    }
}
