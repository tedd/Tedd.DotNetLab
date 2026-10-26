using System.Text;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Storage;

namespace Tedd.DotNetLab.Application;

public record ReferenceSelection(string ArtifactId,string OwnerId);
public record ReferenceReleasePlan(string Id,IReadOnlyList<ArtifactReferenceSnapshot> References,string Reason)
{
    public string ContentHash=>Wire.Hash(new { References,Reason });
}

public sealed partial class LabService
{
    public ReferenceReleasePlan PrepareReferenceRelease(IReadOnlyList<ReferenceSelection> selection,string reason)
    {
        if(selection is null||selection.Count is <1 or >100||selection.Distinct().Count()!=selection.Count||string.IsNullOrWhiteSpace(reason)||Encoding.UTF8.GetByteCount(reason)>1024||
            selection.Any(r=>r is null||string.IsNullOrEmpty(r.OwnerId)||Encoding.UTF8.GetByteCount(r.OwnerId)>128))
            throw new LabException("invalid_plan","Reference release requires 1..100 distinct owner/artifact pairs and a bounded reason.");
        var references=new List<ArtifactReferenceSnapshot>();
        foreach(var pair in selection.OrderBy(r=>r.ArtifactId,StringComparer.Ordinal).ThenBy(r=>r.OwnerId,StringComparer.Ordinal))
        {
            var artifact=Record<Artifact>(pair.ArtifactId,"artifact");
            if(artifact.Id!=pair.ArtifactId||artifact.Id!="art-"+artifact.Hash) throw new LabException("artifact_corrupt","Artifact identity differs from its manifest.");
            references.Add(Catalog.ReferenceSnapshot(pair.ArtifactId,pair.OwnerId));
        }
        var plan=new ReferenceReleasePlan(Wire.Id("reference-release-plan"),references,reason);
        Catalog.Put(plan.Id,"reference_release_plan",plan); return plan;
    }
    private static TargetIdentity ReferenceReleaseTarget(ReferenceReleasePlan plan)=>new(plan.Id,0,0,plan.Id,"workspace","local");
    // Local CLI only. A release grant cannot authorize removing bytes or releasing a system pin.
    public Grant IssueReferenceReleaseGrant(string planId,int seconds,int maximumOperations)
    {
        var plan=Record<ReferenceReleasePlan>(planId,"reference_release_plan");
        if(seconds is <1 or >3600||maximumOperations is <1 or >1000) throw new LabException("invalid_request","Invalid reference-release grant budget.");
        var grant=new Grant(Wire.Id("grant"),Catalog.Root,plan.Id,0,Permission.DeleteEvidence,DateTimeOffset.UtcNow.AddSeconds(seconds),maximumOperations,plan.ContentHash);
        Catalog.Put(grant.Id,"grant",grant); return grant;
    }
    public Operation StartReferenceRelease(string planId,string grantId,string key)
    {
        var plan=Record<ReferenceReleasePlan>(planId,"reference_release_plan"); var grant=Record<Grant>(grantId,"grant");
        Policies.Authorize(grant,Catalog.Root,ReferenceReleaseTarget(plan),Permission.DeleteEvidence,DateTimeOffset.UtcNow,plan.ContentHash);
        return Start("reference_release",ReferenceReleaseTarget(plan),key,Wire.Hash(new { planId,plan_hash=plan.ContentHash,grantId }),async(operation,cancellation)=>
        {
            cancellation.ThrowIfCancellationRequested();
            Catalog.ReleaseArtifactReferences(plan.Id,plan.ContentHash,plan.References,grant,operation.Id);
            await Task.CompletedTask; return true;
        });
    }
}
