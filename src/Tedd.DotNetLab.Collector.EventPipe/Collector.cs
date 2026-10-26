using System.Diagnostics.Tracing;
using Tedd.DotNetLab.Artifacts;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Storage;
using Microsoft.Diagnostics.NETCore.Client;

namespace Tedd.DotNetLab.Collector.EventPipe;

public interface ICaptureSession : IDisposable
{
    Stream Stream { get; }
    Task StopAsync(CancellationToken cancellation);
}
public interface ICaptureSessionFactory
{
    Task<ICaptureSession> OpenAsync(TargetIdentity target, CaptureProfile profile, CancellationToken cancellation);
}
public sealed class EventPipeSessionFactory : ICaptureSessionFactory
{
    public async Task<ICaptureSession> OpenAsync(TargetIdentity target, CaptureProfile profile, CancellationToken cancellation)
    {
        var providers = profile.Providers.Select(p => new EventPipeProvider(p.Name, (EventLevel)p.Level,
            p.Keywords, new Dictionary<string,string>(p.Arguments))).ToArray();
        var session = await new DiagnosticsClient(target.ProcessId).StartEventPipeSessionAsync(providers,
            profile.Rundown, profile.BufferMiB, cancellation);
        return new Adapter(session);
    }
    private sealed class Adapter(EventPipeSession session) : ICaptureSession
    {
        public Stream Stream => session.EventStream;
        public Task StopAsync(CancellationToken cancellation) => session.StopAsync(cancellation);
        public void Dispose() => session.Dispose();
    }
}
public sealed class EventPipeCollector(Catalog catalog, ArtifactStore artifacts, ITargetRegistry targets,
    ICaptureSessionFactory factory, ResourceLimits limits)
{
    public async Task<Capture> CaptureAsync(string operationId, TargetIdentity target, string profileName,
        int seconds, CancellationToken cancellation, bool verifyTarget = true, Task? stopRequested = null, TaskCompletionSource? collecting = null)
    {
        if (seconds < 1 || seconds > limits.MaximumDurationSeconds)
            throw new LabException("resource_limit", "Capture duration exceeds policy.");
        var profile = Profiles.Resolve(profileName);
        if (profile.Providers.Count == 0) throw new LabException("invalid_profile", "Quiet validation has no EventPipe session.");
        if (verifyTarget) targets.Verify(target, target.Epoch);
        if (DiskSpace.AvailableBytes(catalog.Root) < limits.DiskReserveBytes)
            throw new LabException("resource_limit", "Disk reserve is insufficient.");
        var capture = new Capture(Wire.Id("cap"), target, profile, CaptureState.Created, null, null, DateTimeOffset.UtcNow);
        long revision = catalog.Put(capture.Id, "capture", capture).Revision;
        void Move(CaptureState state)
        {
            Lifecycle.Transition(capture.State, state);
            capture = capture with { State = state };
            revision = catalog.Put(capture.Id, "capture", capture, revision).Revision;
        }
        string incoming = artifacts.CreateIncoming(operationId);
        string? reason = null;
        ICaptureSession? session = null;
        bool complete = false;
        Task? reader = null;
        using var drainCancellation = new CancellationTokenSource();
        try
        {
            Move(CaptureState.Opening);
            using var openDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            openDeadline.CancelAfter(TimeSpan.FromSeconds(10));
            session = await factory.OpenAsync(target, profile, openDeadline.Token);
            if (verifyTarget) targets.Verify(target, target.Epoch); // identity must still match after opening
            Move(CaptureState.Collecting);
            await using (var file = new FileStream(incoming, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                reader = SpoolAsync(session.Stream, file, drainCancellation.Token); collecting?.TrySetResult();
                using var duration = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var timer = Task.Delay(TimeSpan.FromSeconds(seconds), duration.Token);
                Task first = await Task.WhenAny(reader, timer, stopRequested ?? Task.Delay(Timeout.Infinite, duration.Token));
                if (first == reader) reason = "stream_ended_before_requested_duration";
                if (cancellation.IsCancellationRequested) reason = "cancelled";
                duration.Cancel();
                Move(CaptureState.Stopping);
                using var stopDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(limits.DrainTimeoutSeconds));
                try { await session.StopAsync(stopDeadline.Token); }
                catch (Exception e) when (e is not OutOfMemoryException)
                { reason ??= e is OperationCanceledException ? "stop_timeout" : "stop_failed"; }
                Move(CaptureState.Draining);
                drainCancellation.CancelAfter(TimeSpan.FromSeconds(limits.DrainTimeoutSeconds));
                try { await reader.WaitAsync(TimeSpan.FromSeconds(limits.DrainTimeoutSeconds)); }
                catch (LabException e) { reason = e.Code; }
                catch (Exception e) when (e is not OutOfMemoryException)
                { reason ??= e is OperationCanceledException or TimeoutException ? "drain_timeout" : "stream_read_failed"; }
                if (verifyTarget)
                {
                    try { targets.Verify(target, target.Epoch); }
                    catch (LabException e) { reason ??= e.Code; }
                }
                await file.FlushAsync(CancellationToken.None);
                file.Flush(true);
            }
            complete = reason is null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        { reason ??= e is LabException error ? error.Code : e is OperationCanceledException ? "cancelled" : "capture_failed"; }
        finally
        {
            drainCancellation.Cancel();
            session?.Dispose();
            if (reader is not null)
            {
                try { await reader.WaitAsync(TimeSpan.FromSeconds(limits.DrainTimeoutSeconds)); } catch (Exception e) when (e is not OutOfMemoryException) { reason ??= "stream_read_failed"; }
            }
        }
        Artifact? artifact = null;
        if (File.Exists(incoming))
        {
            try
            {
                artifact = await artifacts.SealAsync(incoming, "application/vnd.microsoft.nettrace", "eventpipe/1",
                    complete, limits.MaximumCaptureBytes);
                try { catalog.Get(artifact.Id); } catch (LabException e) when (e.Code == "record_not_found") { catalog.Put(artifact.Id, "artifact", artifact); }
            }
            catch (Exception e) when (e is not OutOfMemoryException) { reason ??= "artifact_seal_failed"; complete = false; }
        }
        CaptureState final = complete ? CaptureState.Sealed : artifact is not null ? CaptureState.Incomplete : CaptureState.Failed;
        capture = capture with { State = final, ArtifactId = artifact?.Id, Reason = reason,
            Quality = new(complete ? "capture_complete_unparsed" : "partial", "unknown", "unparsed", reason is null ? [] : [reason]) };
        catalog.Put(capture.Id, "capture", capture, revision);
        if (capture.State == CaptureState.Failed) collecting?.TrySetException(new LabException(reason ?? "capture_failed", "Capture did not start.")); return capture;
    }
    private async Task SpoolAsync(Stream input, FileStream file, CancellationToken cancellation)
    {
        byte[] buffer = new byte[65536];
        long total = 0;
        while (true)
        {
            int read = await input.ReadAsync(buffer, cancellation);
            if (read == 0) break;
            if (total > limits.MaximumCaptureBytes - read)
                throw new LabException("resource_limit", "Capture byte cap reached.");
            if (!DiskSpace.Fits(catalog.Root,read,limits.DiskReserveBytes))
                throw new LabException("resource_limit", "Disk reserve reached.");
            await file.WriteAsync(buffer.AsMemory(0, read), cancellation);
            total += read;
        }
    }
}
