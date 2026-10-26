using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Application;

public record RetentionPolicy(long MaximumRawTraceBytes,int MaximumAgeDays);
public record DeletionPlan(string Id,IReadOnlyList<Artifact> Artifacts,string Reason,RetentionPolicy? Policy=null,
    long RetainedRawTraceBytes=0,long ProjectedRawTraceBytes=0,bool QuotaPressure=false,IReadOnlyList<string>? ProtectedArtifactIds=null)
{
    public string ContentHash=>Wire.Hash(new { Artifacts,Reason,Policy,RetainedRawTraceBytes,ProjectedRawTraceBytes,QuotaPressure,ProtectedArtifactIds });
}
public record DeletionResult(string Id,string PlanId,string PlanHash,int DeletedArtifacts,long DeletedBytes,string State="completed");

public sealed partial class LabService
{
    public DeletionPlan PrepareDeletion(IReadOnlyList<string> artifactIds,string reason)
    {
        if(artifactIds.Count is <1 or >1000||artifactIds.Distinct().Count()!=artifactIds.Count||string.IsNullOrWhiteSpace(reason)||reason.Length>1024)
            throw new LabException("invalid_plan","Deletion requires a distinct bounded selection and reason.");
        var artifacts=artifactIds.Order(StringComparer.Ordinal).Select(id=>Record<Artifact>(id,"artifact")).ToArray();
        foreach(var artifact in artifacts) CheckDeletionProtection(artifact.Id);
        var plan=new DeletionPlan(Wire.Id("deletion-plan"),artifacts,reason);
        Catalog.Put(plan.Id,"deletion_plan",plan); return plan;
    }
    public DeletionPlan PrepareRetention(RetentionPolicy policy)
    {
        if(policy.MaximumRawTraceBytes<0||policy.MaximumAgeDays is <0 or >36500)
            throw new LabException("invalid_plan","Invalid raw-trace retention quota or age.");
        var traces=new List<(Artifact Artifact,DateTimeOffset Created)>(); string? cursor=null;
        do
        {
            var page=Catalog.List("artifact",100,cursor);
            foreach(var record in page.Rows)
            {
                var artifact=Wire.Deserialize<Artifact>(record.Json);
                if(artifact.MediaType=="application/vnd.microsoft.nettrace"&&Catalog.Retention(artifact.Id).DeletionState is null)
                    traces.Add((artifact,record.CreatedAt));
                if(traces.Count>10000) throw new LabException("resource_limit","Retention selection exceeds 10000 raw traces.");
            }
            cursor=page.NextCursor;
        } while(cursor is not null);
        long total=traces.Sum(t=>t.Artifact.Length),projected=total; var selected=new List<Artifact>(); var protectedIds=new List<string>();
        var cutoff=DateTimeOffset.UtcNow.AddDays(-policy.MaximumAgeDays);
        foreach(var trace in traces.OrderBy(t=>t.Created).ThenBy(t=>t.Artifact.Id,StringComparer.Ordinal))
        {
            var protection=Catalog.Retention(trace.Artifact.Id);
            if(protection.Pinned||protection.RetainedOwners>0) { protectedIds.Add(trace.Artifact.Id); continue; }
            if(trace.Created<cutoff||projected>policy.MaximumRawTraceBytes)
            {
                if(selected.Count==1000) throw new LabException("resource_limit","Retention requires more than 1000 deletions; use smaller sweeps.");
                selected.Add(trace.Artifact); projected-=trace.Artifact.Length;
            }
        }
        var plan=new DeletionPlan(Wire.Id("deletion-plan"),selected,"raw_trace_retention",policy,total,projected,
            projected>policy.MaximumRawTraceBytes,protectedIds);
        Catalog.Put(plan.Id,"deletion_plan",plan); return plan;
    }
    private void CheckDeletionProtection(string id)
    {
        var state=Catalog.Retention(id);
        if(state.DeletionState is not null) throw new LabException("artifact_expired","Artifact deletion has been committed.");
        if(state.Pinned||state.RetainedOwners>0) throw new LabException("evidence_protected","Retained owners or pins prevent deletion.");
    }
    private static TargetIdentity DeletionTarget(DeletionPlan plan)=>new(plan.Id,0,0,plan.Id,"workspace","local");
    // Human CLI only; authority binds the complete frozen inclusion list and retention policy.
    public Grant IssueDeletionGrant(string planId,int seconds,int maximumOperations)
    {
        var plan=Record<DeletionPlan>(planId,"deletion_plan");
        if(seconds is <1 or >3600||maximumOperations is <1 or >1000) throw new LabException("invalid_request","Invalid deletion grant budget.");
        var grant=new Grant(Wire.Id("grant"),Catalog.Root,plan.Id,0,Permission.DeleteEvidence,DateTimeOffset.UtcNow.AddSeconds(seconds),maximumOperations,plan.ContentHash);
        Catalog.Put(grant.Id,"grant",grant); return grant;
    }
    public Operation StartDeletion(string planId,string grantId,string key)
    {
        var plan=Record<DeletionPlan>(planId,"deletion_plan"); var grant=Record<Grant>(grantId,"grant");
        Policies.Authorize(grant,Catalog.Root,DeletionTarget(plan),Permission.DeleteEvidence,DateTimeOffset.UtcNow,plan.ContentHash);
        return Start("deletion",DeletionTarget(plan),key,Wire.Hash(new { planId,plan_hash=plan.ContentHash,grantId }),async(operation,cancellation)=>
        {
            cancellation.ThrowIfCancellationRequested();
            Policies.Authorize(grant,Catalog.Root,DeletionTarget(plan),Permission.DeleteEvidence,DateTimeOffset.UtcNow,plan.ContentHash);
            // All protection checks and intents commit together before the first filesystem removal.
            if(plan.Artifacts.Count>0) Catalog.BeginArtifactDeletion(plan.Id,plan.ContentHash,plan.Artifacts.Select(a=>a.Id).ToArray(),grant);
            else Catalog.UseGrant(grant);
            // Once committed, deletion must finish despite cancellation; startup retries interrupted intents.
            RecoverArtifactDeletions();
            var result=new DeletionResult(Wire.Id("deletion"),plan.Id,plan.ContentHash,plan.Artifacts.Count,plan.Artifacts.Sum(a=>a.Length));
            Catalog.Put(result.Id,"deletion_result",result);
            Catalog.Transition(operation.Id,OperationState.Completed,result.Id);
            await Task.CompletedTask; return true;
        });
    }
    private void RecoverArtifactDeletions()
    {
        foreach(var intent in Catalog.PendingArtifactDeletions())
        {
            var artifact=Record<Artifact>(intent.ArtifactId,"artifact");
            Artifacts.Delete(artifact); Catalog.CompleteArtifactDeletion(artifact.Id);
        }
    }
    public object PinEvidence(string artifactId,string ownerId)
    {
        var artifact=Record<Artifact>(artifactId,"artifact");
        Catalog.RetainArtifact(artifact.Id,ownerId); return Catalog.Retention(artifact.Id);
    }
}
