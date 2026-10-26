using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    private Task<NativeSnapshotReply> AcquireNativeSnapshotAsync(TargetIdentity target,object request,Grant grant,string owner,CancellationToken cancellation)
    {
        // This wrapper is only used by read-only snapshot callers, outside timing.
        return NativeSnapshotAcquirer.AcquireAsync(async token=>
        {
            Policies.Authorize(grant,Catalog.Root,target,Permission.InspectLiveCode,DateTimeOffset.UtcNow); Catalog.UseGrant(grant);
            return (await profiler.SendAsync(target,request,token)).Deserialize<NativeSnapshotReply>(Wire.Json)!;
        },
            cancellation,(attempt,code)=>Catalog.AppendAudit("native_snapshot_rejected",owner,Wire.Serialize(new { target_id=target.Id,target.Epoch,attempt,code,
                policy="bounded_snapshot_reacquisition/v1",meaning="Rejected bytes are not retained as a consistent snapshot." })));
    }
}
