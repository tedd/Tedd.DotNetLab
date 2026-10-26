using System.Security.Cryptography;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Probes;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    public async Task<CilCandidateRecord> RegisterProbeAsync(string inspectionId,string baseIlHash,bool timing,int threads=32,int maximumNesting=64,bool trackExceptions=false)
    {
        if(threads is <1 or >64||maximumNesting is <1 or >128) throw new LabException("resource_limit","Probe configuration exceeds policy.");
        if(Catalog.Get(inspectionId).Kind!="inspection") throw new LabException("invalid_request","Probe requires a live inspection record.");
        var inspection=Catalog.Get<JsonElement>(inspectionId);
        if(inspection.GetProperty("qualification").GetString()!="verified_live_acquisition") throw new LabException("invalid_request","Imported snapshots cannot authorize live instrumentation.");
        var target=inspection.GetProperty("target").Deserialize<TargetIdentity>(Wire.Json)!;
        var handshake=await ScenariosAsync(target.Id,CancellationToken.None);
        if(handshake.Target.Epoch!=target.Epoch) throw new LabException("stale_epoch","Inspection epoch differs from the target.");
        var helper=handshake.ProbeHelper??throw new LabException("capability_unsupported","Approved helper is absent from startup integration.");
        string approvedHash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(typeof(ProbeRuntime).Assembly.Location)));
        if(helper.Version!=ProbeRuntime.Version||helper.ModuleHash!=approvedHash)
            throw new LabException("helper_identity_changed","Target helper differs from the approved controller build.");
        var method=Catalog.Get<StoredMethod>(inspection.GetProperty("method_id").GetString()!);
        string sourceId=inspection.GetProperty("manifest_artifact_id").GetString()!;
        var sourceArtifact=Catalog.Get<Artifact>(sourceId);
        var source=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(sourceArtifact)));
        var versions=source.Snapshots.Where(s=>s.CurrentIlHash==baseIlHash).ToArray();
        var loads=versions.Select(v=>v.ModuleLoadIdentity).Distinct().ToArray();
        if(loads.Length!=1) throw new LabException("stale_candidate","Probe base is absent or ambiguous.");
        byte[] original=baseIlHash==source.OriginalBodyHash?source.OriginalBody:versions.FirstOrDefault(v=>v.CurrentIlBody is not null)?.CurrentIlBody
            ??throw new LabException("not_collected","Selected current CIL body was not retained.");
        var image=await CodeArtifactAsync("art-"+method.ModuleHash,"application/vnd.microsoft.portable-executable");
        var policy=NativeBodyPolicy.Resolve(original,method,image.Path); NativeBodyPolicy.Verify(original,policy);
        var references=PeInspector.ProbeReferences(image.Path);
        FaultProbePolicy? fault=null;
        if(trackExceptions)
        {
            if(references.ExceptionalExitToken==0) throw new LabException("capability_unsupported","The approved exceptional-exit reference is absent.");
            var signature=PeInspector.ProbeReturnSignature(image.Path,policy.LocalCount);
            fault=new(references.ExceptionalExitToken,signature.Token,signature.Count,policy.LocalCount);
        }
        var config=new ProbeConfiguration(helper.Version,helper.ModuleHash,helper.ModuleMvid,references.EnterToken,references.ExitToken,
            timing,threads,maximumNesting,original,trackExceptions?"normal-exit-and-managed-fault-unwind":"normal-completion/synchronous-int32",policy,fault);
        var body=RewriteProbe(method,config).Body;
        if(body.Length>4096) throw new LabException("resource_limit","Probe body exceeds native qualification limits.");
        var artifact=await Artifacts.WriteJsonAsync(Wire.Id("probe-input"),new CilInput(Wire.Version,body,"trusted selected synchronous probe rewrite"),"probe-rewriter/1");
        Upsert(artifact.Id,"artifact",artifact);
        var candidate=new CilCandidateRecord(Wire.Id("probe"),Wire.Version,target,method,loads[0],inspectionId,sourceId,artifact.Id,
            Convert.ToHexStringLower(SHA256.HashData(body)),baseIlHash,source.OriginalBodyHash,"trusted selected synchronous probe rewrite",[],config,Inliners:source.Inliners??[]);
        Catalog.Put(candidate.Id,"cil_candidate",candidate); Catalog.Pin(artifact.Id,"probe_plan"); Catalog.Pin(sourceId,"probe_original");
        return candidate;
    }
    private static ProbeRewrite RewriteProbe(StoredMethod method,ProbeConfiguration configuration)
    {
        var policy=configuration.Policy??new(method.ModuleMvid,method.Token,method.SignatureHex,method.Static,method.Generic,method.Constructor,0);
        policy=policy with { Calls=[..policy.Calls??[],new(configuration.EnterToken,1,false,true),new(configuration.ExitToken,1,false,true)] };
        if(configuration.Fault is {} fault)
        {
            policy=policy with { Calls=[..policy.Calls!,new(fault.ExceptionalExitToken,1,false,true)] };
            return CilFaultProbeRewriter.Rewrite(configuration.BaseBody,policy,0,configuration.EnterToken,configuration.ExitToken,fault);
        }
        return CilProbeRewriter.Rewrite(configuration.BaseBody,policy,0,configuration.EnterToken,configuration.ExitToken);
    }
    private Task<JsonElement> ProbeControlAsync(TargetIdentity target,long epoch,CilCandidateRecord candidate,Grant parent,string transaction,string action,CancellationToken cancellation)
    {
        var config=candidate.Probe??throw new LabException("invalid_request","Candidate has no probe plan.");
        var grant=parent with { Id="helper-"+Wire.Hash(new {parent.Id,transaction,candidate.ContentHash})[..32],MaximumOperations=16 };
        return harness.ProbeAsync(target with { Epoch=epoch },new(action,epoch,grant,candidate.ContentHash,candidate.Scope,1,config.Timing,
            config.Threads,config.MaximumNesting),cancellation);
    }
    public async Task<object> FlushProbesAsync(string targetId,long epoch,string candidateId,string grantId,bool restart=true,CancellationToken cancellation=default)
    {
        var candidate=CilCandidate(candidateId);
        if(candidate.Probe is null) throw new LabException("invalid_request","Expected a selected probe plan.");
        await ProfilerAsync(targetId,cancellation); var target=Target(targetId).Identity;
        if(target.Epoch!=epoch) throw new LabException("stale_epoch","Probe flush epoch differs.");
        var grant=MutationGrant(grantId,target,candidate); string id=Wire.Id("probe-flush");
        var operation=Catalog.Accept("local",id,Wire.Hash(new {targetId,epoch,candidateId,grantId,restart}),"probe_flush",targetId,Workspace.Policy.Id);
        try { Catalog.AcquireLease(targetId,operation.Id); }
        catch(LabException error) { Catalog.Transition(operation.Id,OperationState.Failed,error:error.Code); throw; }
        Catalog.Transition(operation.Id,OperationState.Running);
        try
        {
            Catalog.UseGrant(grant);
            var snapshot=await ProbeControlAsync(target,epoch,candidate,grant,id,"stop",cancellation);
            var artifact=await Artifacts.WriteJsonAsync(operation.Id,snapshot,"probe-helper/1"); Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"probe_evidence");
            string evidenceId=Wire.Id("probe-evidence");
            Catalog.Put(evidenceId,"probe_evidence",new { id=evidenceId,target,candidate_id=candidate.Id,candidate.ContentHash,artifact_id=artifact.Id,
                mode=candidate.Probe.Timing?"inclusive_elapsed":"count",exception_coverage=candidate.Probe.ExceptionCoverage,
                inliner_coverage=candidate.Inliners is {Count:>0}?"frozen-caller-closure/compilation-verified":"excluded_or_observed_empty",
                affected_callers=candidate.Inliners??[],snapshot });
            if(restart) await ProbeControlAsync(target,epoch,candidate,grant,id,"begin",cancellation);
            Catalog.Transition(operation.Id,OperationState.Completed,evidenceId);
            return new { evidence_id=evidenceId,artifact_id=artifact.Id,complete=snapshot.GetProperty("complete").GetBoolean(),
                entered=snapshot.GetProperty("rows").EnumerateArray().Sum(r=>r.GetProperty("entered").GetInt64FromString()),restarted=restart };
        }
        catch(Exception error) when(error is not OutOfMemoryException)
        { Catalog.Transition(operation.Id,OperationState.Failed,error:error is LabException failure?failure.Code:"probe_flush_failed"); throw; }
        finally { Catalog.ReleaseLease(targetId,operation.Id,true); }
    }
}
