using System.Collections.Concurrent;
using System.Security.Cryptography;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

namespace Tedd.DotNetLab.Probes;

public sealed class HarnessProbeSupport(string workspace) : IHarnessProbeSupport
{
    private readonly string root=Path.GetFullPath(workspace);
    private readonly ConcurrentDictionary<string,int> uses=new();
    private readonly ConcurrentDictionary<string,string> fingerprints=new();
    private readonly string moduleHash=Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(typeof(ProbeRuntime).Assembly.Location)));
    private string? activePlan,activeScope;
    private bool timing;
    private readonly object control=new();
    public ProbeHelperDescriptor Describe()
    { lock(control) return new(ProbeRuntime.Version,moduleHash,typeof(ProbeRuntime).Module.ModuleVersionId.ToString("D"),ProbeRuntime.IsActive,activePlan,activeScope,timing); }
    public object Control(TargetIdentity target,ProbeControl request)
    {
        var grant=request.Grant;
        if(request.Methods is <1 or >256||request.Threads is <1 or >64||request.MaximumNesting is <1 or >128||grant.Id.Length>128||uses.Count>=10000&&!uses.ContainsKey(grant.Id))
            throw new LabException("resource_limit","Probe configuration or grant registry exceeds limits.");
        if(target.Epoch!=request.ExpectedEpoch) throw new LabException("stale_epoch","Probe control epoch differs.");
        if(grant.Permission!=Permission.InstallProbes||grant.Workspace!=root||grant.TargetId!=target.Id||
            grant.StartTimeUtcTicks!=target.StartTimeUtcTicks||grant.ExpiresAt<=DateTimeOffset.UtcNow||grant.MaximumOperations<=0||
            grant.ContentHash!=request.PlanHash||grant.MethodScope!=request.MethodScope)
            throw new LabException("permission_denied","Probe helper control grant differs from the selected plan.");
        lock(control)
        {
            string fingerprint=Wire.Hash(grant);
            if(fingerprints.TryGetValue(grant.Id,out var previous)&&previous!=fingerprint)
                throw new LabException("permission_denied","Probe control grant identity was altered.");
            fingerprints[grant.Id]=fingerprint;
            uses.AddOrUpdate(grant.Id,1,(_,old)=>old>=grant.MaximumOperations?
                throw new LabException("permission_denied","Probe helper control budget exhausted."):old+1);
            if(request.Action=="begin")
            {
                if(ProbeRuntime.IsActive) throw new LabException("lease_conflict","A probe session is active.");
                ProbeRuntime.Begin(request.Methods,request.Timing,request.Threads,request.MaximumNesting);
                activePlan=request.PlanHash; activeScope=request.MethodScope;
                timing=request.Timing;
                return Describe();
            }
            if(request.Action=="stop")
            {
                if(activePlan!=request.PlanHash||activeScope!=request.MethodScope||!ProbeRuntime.IsActive)
                    throw new LabException("probe_session_mismatch","Active helper session differs from the plan.");
                var snapshot=ProbeRuntime.Stop(); activePlan=activeScope=null; return snapshot;
            }
            throw new LabException("invalid_request","Unknown probe helper control action.");
        }
    }
}
