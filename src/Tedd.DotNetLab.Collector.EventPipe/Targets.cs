using System.Diagnostics;
using Tedd.DotNetLab.Contracts;
using Microsoft.Diagnostics.NETCore.Client;

namespace Tedd.DotNetLab.Collector.EventPipe;

public interface ITargetRegistry
{
    IReadOnlyList<TargetInfo> Discover();
    void Verify(TargetIdentity identity,long expectedEpoch);
}
public sealed class Targets : ITargetRegistry
{
    public IReadOnlyList<TargetInfo> Discover()
    {
        var result = new List<TargetInfo>();
        var published=DiagnosticsClient.GetPublishedProcesses().Distinct().Order().Take(1001).ToArray();
        if(published.Length>1000) throw new LabException("resource_limit","Published target inventory exceeds the 1000-target policy.");
        foreach (int pid in published)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                long start = ProcessIdentity.StartTimeUtcTicks(process);
                string id = "target-" + Wire.Hash(new { pid, start })[..24];
                var capabilities = new Dictionary<string, Capability>
                {
                    ["eventpipe"] = new(CapabilityState.Unknown, "Diagnostic endpoint published; session permission/provider validation required."),
                    ["harness"] = new(CapabilityState.Unknown, "Cooperative handshake required."),
                    ["method_metadata"] = new(CapabilityState.Unsupported, "Live metadata adapter not qualified."),
                    ["native_inspection"] = new(CapabilityState.Unsupported, "No certified native profiler."),
                    ["counters"] = new(CapabilityState.Unknown, "Provider coverage must be verified."),
                    ["timing_probes"] = new(CapabilityState.Unsupported, "No certified native profiler."),
                    ["rejit"] = new(CapabilityState.Unsupported, "No certified native profiler."),
                    ["inliner_handling"] = new(CapabilityState.Unsupported, "No certified native profiler."),
                    ["restore"] = new(CapabilityState.Unsupported, "No certified native profiler.")
                };
                result.Add(new(new(id, pid, start, id, "unknown", "unknown"), process.ProcessName, capabilities));
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { /* Target exited or is inaccessible; do not elevate. */ }
            catch(LabException error) when(error.Code is "target_gone" or "permission_denied" or "transport_identity_unavailable")
            { /* Identity unavailable is not a discoverable, safe target. */ }
        }
        return result;
    }
    public void Verify(TargetIdentity identity, long expectedEpoch)
    {
        if (identity.Epoch != expectedEpoch) throw new LabException("stale_epoch", "Target epoch differs.");
        try
        {
            using var process = Process.GetProcessById(identity.ProcessId);
            if (process.HasExited) throw new LabException("target_gone", "Target exited.");
            if (ProcessIdentity.StartTimeUtcTicks(process) != identity.StartTimeUtcTicks)
                throw new LabException("target_identity_changed", "Process ID was reused.");
        }
        catch (ArgumentException) { throw new LabException("target_gone", "Target exited."); }
        catch (System.ComponentModel.Win32Exception) { throw new LabException("permission_denied", "Target identity cannot be verified."); }
    }
}
