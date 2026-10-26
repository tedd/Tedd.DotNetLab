using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Artifacts;
using Tedd.DotNetLab.Collector.EventPipe;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Storage;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CaptureTests
{
    internal static async Task WaitForDiagnosticPublicationAsync(Process process)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient.GetPublishedProcesses().Contains(process.Id))
        {
            if(process.HasExited) throw new InvalidOperationException("Owned target exited before its diagnostic endpoint was published.");
            await Task.Delay(20,deadline.Token);
        }
        using var current=Process.GetProcessById(process.Id);
        if(ProcessIdentity.StartTimeUtcTicks(current)!=ProcessIdentity.StartTimeUtcTicks(process))
            throw new InvalidOperationException("Owned target identity changed during diagnostic startup.");
    }
    [Fact, Case("UT-GC-005", "FR-022", "M1", "Unit")]
    public void Intervals_ClippedOverlaps_AreUnioned()
    {
        TimeInterval[] values = [new(90,110), new(150,180), new(170,210)];
        Assert.Equal(60, Intervals.UnionOverlap(values,100,200));
        Assert.Equal(0, Intervals.UnionOverlap([new(90,100),new(200,210)],100,200));
    }
    [Fact, Case("UT-TIME-004", "FR-012", "M1", "Unit")]
    public void ClockMapping_LargeTicks_UsesCenteredFit()
    {
        long start = long.MaxValue - 100000;
        var mapping = Intervals.Fit([new(start,2),new(start+1000,3),new(start+2000,4)], .001);
        Assert.Equal(3.5, mapping.Map(start+1500), 10);
        Assert.Throws<LabException>(() => Intervals.Fit([new(10,1),new(20,2),new(30,6)],.01));
    }
    [Fact, Case("IT-CAP-001", "FR-011,FR-022", "M1", "Integration")]
    public async Task Capture_RealGcStimulus_RetainsRetrospectiveEvidence()
    {
        using var workspace = new TestWorkspace();
        using var catalog = new Catalog(workspace.Root);
        var store = new ArtifactStore(workspace.Root);
        using var target = await StartTarget();
        var targets = new Targets();
        var identity = targets.Discover().Single(t => t.Identity.ProcessId == target.Id).Identity;
        var operation = catalog.Accept("test","capture",Wire.Hash(identity),"capture",identity.Id);
        var collector = new EventPipeCollector(catalog,store,targets,new EventPipeSessionFactory(),new());
        var capture = await collector.CaptureAsync(operation.Id,identity,"baseline-context",2,CancellationToken.None);
        Assert.Equal(CaptureState.Sealed,capture.State);
        var artifact = catalog.Get<Artifact>(capture.ArtifactId!);
        target.Kill(true);
        await target.WaitForExitAsync();
        var summary = new TraceAnalyzer().Analyze(await store.VerifiedPathAsync(artifact),capture);
        Assert.NotEmpty(summary.GcCycles);
        Assert.Contains(summary.GcCycles,c => c.EndMilliseconds.HasValue);
        Assert.Equal("none_reported",summary.Quality.LossStatus);
        Assert.Equal(Availability.NotCollected,new TraceAnalyzer().Metric(summary,capture,"method.average_ns").Status);
        Assert.Equal(Availability.NotCollected,new TraceAnalyzer().Metric(summary,capture,"allocation.sampled_bytes").Status);
        Assert.Equal(artifact.Hash,catalog.Get<Artifact>(capture.ArtifactId!).Hash);
    }
    [Fact, Case("IT-CAP-004", "FR-013", "M1", "Integration")]
    public async Task Capture_TargetExit_PreservesIncompleteTrace()
    {
        using var workspace = new TestWorkspace();
        using var catalog = new Catalog(workspace.Root);
        var store = new ArtifactStore(workspace.Root);
        using var target = await StartTarget();
        var targets = new Targets();
        var identity = targets.Discover().Single(t => t.Identity.ProcessId == target.Id).Identity;
        var operation = catalog.Accept("test","death","death","capture",identity.Id);
        var collector = new EventPipeCollector(catalog,store,targets,new EventPipeSessionFactory(),new());
        var collecting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = collector.CaptureAsync(operation.Id,identity,"baseline-context",10,CancellationToken.None,collecting:collecting);
        try
        {
            await collecting.Task.WaitAsync(TimeSpan.FromSeconds(20));
            await Task.Delay(750);
            target.Kill(true);
            await target.WaitForExitAsync();
            var capture = await task.WaitAsync(TimeSpan.FromSeconds(40));
            Assert.True(capture.State==CaptureState.Incomplete,Wire.Serialize(capture));
            Assert.NotNull(capture.ArtifactId);
            Assert.False(catalog.Get<Artifact>(capture.ArtifactId!).Complete);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
    [Fact, Case("UT-ID-001", "FR-002", "M1", "Integration")]
    public void Target_ReusedPidIdentity_IsRejected()
    {
        using var process = Process.GetCurrentProcess();
        var identity = new TargetIdentity("fake",process.Id,process.StartTime.ToUniversalTime().Ticks-1,"fake","unknown","unknown");
        Assert.Equal("target_identity_changed",Assert.Throws<LabException>(() => new Targets().Verify(identity,0)).Code);
    }
    public static async Task<Process> StartTarget(string? workspace = null)
    {
        string dll = Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(dll); if (workspace is not null) { start.ArgumentList.Add("--harness"); start.ArgumentList.Add(workspace); }
        var process = Process.Start(TestWorkspace.PinRuntime(start))!;
        try
        {
        string? ready = await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
        if (ready is null)
        {
            string error = await process.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(10));
            throw new InvalidOperationException("Workload failed to start: " + error);
        }
        if(workspace is not null)
        {
            using var announcement=JsonDocument.Parse(ready);
            var declared=announcement.RootElement.GetProperty("target").Deserialize<TargetIdentity>(Wire.Json)!;
            using var verifiedProcess=Process.GetProcessById(process.Id);
            long observed=ProcessIdentity.StartTimeUtcTicks(verifiedProcess);
            string observedId="target-"+Wire.Hash(new { pid=process.Id,start=observed })[..24];
            if(declared.StartTimeUtcTicks!=observed||declared.Id!=observedId)
            {
                throw new InvalidOperationException($"Harness and controller process identity differ: declared {declared.StartTimeUtcTicks}, observed {observed}.");
            }
        }
        await WaitForDiagnosticPublicationAsync(process);
        return process;
        }
        catch
        {
            if(!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            process.Dispose();
            throw;
        }
    }
}
