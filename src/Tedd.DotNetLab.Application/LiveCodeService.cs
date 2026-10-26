using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    private readonly ProfilerClient profiler=new();
    private static bool VerifiedWindowsAttach(ProfilerHandshake reply)=>OperatingSystem.IsWindows()&&!reply.Startup&&
        reply.AttachmentComplete==true&&reply.EventMaskLow==0x40024;
    public async Task<ProfilerHandshake> ProfilerAsync(string targetId,CancellationToken cancellation)
    {
        var target=Target(targetId);
        targets.Verify(target.Identity,target.Identity.Epoch);
        var reply=await profiler.HandshakeAsync(target.Identity,cancellation);
        if(reply.TargetId!=targetId||reply.ProcessId!=target.Identity.ProcessId||
            reply.StartTimeUtcTicks!=target.Identity.StartTimeUtcTicks)
            throw new LabException("target_identity_changed","Profiler handshake identity differs.");
        bool attached=VerifiedWindowsAttach(reply);
        bool supported=RuntimeSupport.HostSupported&&RuntimeSupport.Supports(reply.Runtime)&&reply.Architecture=="x64"&&
            reply.Interface=="ICorProfilerInfo12"&&reply.Workspace==Catalog.Root&&(reply.Startup||attached)&&reply.NativeInspection=="experimental";
        var capabilities=new Dictionary<string,Capability>(target.Capabilities)
        {
            ["native_inspection"]=new(supported?CapabilityState.Supported:CapabilityState.Unsupported,
                supported?attached?"Pinned Windows x64 attached profiler with completed callback activation; noncollectible nongeneric methods only.":
                    "Pinned Windows/Linux x64 startup profiler; noncollectible nongeneric methods only.":"Profiler is outside the live-inspection qualification matrix.")
        };
        bool mutation=supported&&reply.Rejit=="experimental"&&reply.Restore=="experimental"&&
            (reply.Startup&&reply.InlinerHandling=="experimental"||attached&&reply.InlinerHandling=="verified_noinline_only");
        capabilities["rejit"]=new(mutation?CapabilityState.Supported:CapabilityState.Unsupported,
            mutation?attached?"Disposable attach adapter; verified no-inline static Int32 bodies and scoped untimed correctness required.":
                "Disposable startup adapter; bounded static Int32 bodies and scoped untimed correctness required.":"Target is outside the mutation qualification matrix.");
        capabilities["restore"]=new(mutation?CapabilityState.Supported:CapabilityState.Unsupported,
            mutation?"Original-version restore checks every affected method and application health; it does not restore heap or external effects.":"Qualified restoration is unavailable.");
        capabilities["inliner_handling"]=new(mutation&&reply.InlinerHandling=="experimental"?CapabilityState.Supported:CapabilityState.Unsupported,
            mutation&&reply.InlinerHandling=="experimental"?"Frozen startup/native caller closure, at most 31 callers; unknown coverage blocks preflight.":
                attached?"Attach mutation requires verified no-inline methods; pre-attach caller history is not collected.":"Qualified caller closure is unavailable.");
        capabilities["timing_probes"]=new(mutation&&reply.Startup&&reply.Probes=="experimental"?CapabilityState.Supported:CapabilityState.Unsupported,
            mutation&&reply.Startup&&reply.Probes=="experimental"?"Selected synchronous count/elapsed gateway; approved helper, metadata bindings, and plan preflight required.":"Qualified selected probes are unavailable.");
        Upsert(targetId,"target",target with { Identity=target.Identity with {
            RuntimeInstance=reply.TargetId,RuntimeVersion=reply.Runtime,Architecture=reply.Architecture,Epoch=reply.Epoch },Capabilities=capabilities });
        return reply;
    }
    public async Task<Operation> InspectLiveAsync(string targetId,string methodId,long epoch,string grantId,
        string? moduleLoadId,CancellationToken cancellation)
    {
        await ProfilerAsync(targetId,cancellation);
        var target=Target(targetId);
        if(target.Capabilities["native_inspection"].State!=CapabilityState.Supported)
            throw new LabException("capability_unsupported","Live inspection is outside the qualified matrix.");
        if(target.Identity.Epoch!=epoch) throw new LabException("stale_epoch","Selected execution epoch differs.");
        if(Catalog.Get(methodId).Kind!="method") throw new LabException("invalid_request","Expected a retained method identity.");
        var method=Catalog.Get<StoredMethod>(methodId);
        if(method.Generic||!method.HasBody) throw new LabException("capability_unsupported","Method is outside the native subset.");
        var grant=Authorize(grantId,target.Identity,Permission.InspectLiveCode);
        var request=new { schema_version=Wire.Version,action="snapshot",target_id=targetId,process_id=target.Identity.ProcessId,
            start_time_utc_ticks=target.Identity.StartTimeUtcTicks,expected_target_epoch=epoch,grant,
            selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=method.ModuleHash,
                signature=method.SignatureHex,module_load_id=moduleLoadId??"" } };
        return Start("native_inspection",target.Identity,Wire.Id("inspect"),Wire.Hash(request),async(operation,token)=>
        {
            var reply=await AcquireNativeSnapshotAsync(target.Identity,request,grant,operation.Id,token);
            if(reply.CodeRewrittenForInspection||reply.Perturbation.RuntimeSuspended)
                throw new LabException("invalid_snapshot","Unexpected inspection perturbation.");
            var artifacts=new List<string>();
            foreach(var snapshot in reply.Snapshots)
            {
                if(snapshot.TargetId!=targetId||snapshot.Epoch!=epoch.ToString(System.Globalization.CultureInfo.InvariantCulture)||
                    snapshot.ModuleHash!=method.ModuleHash||snapshot.ModuleMvid!=method.ModuleMvid||snapshot.MethodToken!=method.Token||
                    snapshot.Signature!=method.SignatureHex||snapshot.RuntimeVersion!=target.Identity.RuntimeVersion)
                    throw new LabException("invalid_snapshot","Snapshot identity differs from the selected method.");
                NativeDecoder.Validate(snapshot);
                string incoming=Artifacts.CreateIncoming(operation.Id);
                await File.WriteAllTextAsync(incoming,Wire.Serialize(snapshot),token);
                string producer=RuntimeSupport.Producer(target.Identity.RuntimeVersion);
                var artifact=await Artifacts.SealAsync(incoming,"application/vnd.dotnetlab.native-snapshot+json",
                    producer,true,4194304,token);
                Upsert(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"live_inspection"); artifacts.Add(artifact.Id);
            }
            var manifest=await Artifacts.WriteJsonAsync(operation.Id,reply,RuntimeSupport.Producer(target.Identity.RuntimeVersion));
            Upsert(manifest.Id,"artifact",manifest); Catalog.Pin(manifest.Id,"live_inspection");
            string id=Wire.Id("inspection");
            Catalog.Put(id,"inspection",new { id,target=target.Identity,method_id=methodId,artifact_ids=artifacts,
                manifest_artifact_id=manifest.Id,reply.Perturbation,qualification="verified_live_acquisition",
                proves_earlier_run_code=false });
            Catalog.Transition(operation.Id,OperationState.Completed,id);
            return true;
        });
    }
}
