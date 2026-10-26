using Tedd.DotNetLab.Artifacts;
using Tedd.DotNetLab.Collector.EventPipe;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Storage;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CaptureFaultTests
{
    [Fact, Case("UT-CAP-011", "FR-011", "M1", "Unit")]
    public async Task Capture_ShortReadsAndDelayedDrain_PreservesBytes()
    {
        using var workspace = new TestWorkspace();
        using var catalog = new Catalog(workspace.Root);
        var store = new ArtifactStore(workspace.Root);
        var fake = new FakeSession(Enumerable.Range(0,128).Select(i => (byte)i).ToArray());
        var target = new TargetIdentity("fake",1,1,"fake","fake","fake");
        var op = catalog.Accept("test","short","short","capture","fake");
        var collector = new EventPipeCollector(catalog,store,new Targets(),new FakeFactory(fake),new());
        var capture = await collector.CaptureAsync(op.Id,target,"baseline-context",1,CancellationToken.None,false);
        Assert.Equal(CaptureState.Sealed,capture.State);
        Assert.Equal(1,fake.Stops);
        var artifact = catalog.Get<Artifact>(capture.ArtifactId!);
        Assert.Equal(fake.Bytes,await File.ReadAllBytesAsync(await store.VerifiedPathAsync(artifact)));
    }
    [Fact, Case("UT-CAP-017", "FR-016", "M1", "Unit")]
    public async Task Capture_ByteCap_PreservesPartialArtifact()
    {
        using var workspace = new TestWorkspace();
        using var catalog = new Catalog(workspace.Root);
        var store = new ArtifactStore(workspace.Root);
        var fake = new FakeSession(new byte[128]);
        var op = catalog.Accept("test","cap","cap","capture","fake");
        var collector = new EventPipeCollector(catalog,store,new Targets(),new FakeFactory(fake),
            new ResourceLimits(MaximumCaptureBytes:20));
        var capture = await collector.CaptureAsync(op.Id,new("fake",1,1,"fake","fake","fake"),
            "baseline-context",1,CancellationToken.None,false);
        Assert.Equal(CaptureState.Incomplete,capture.State);
        Assert.Equal("resource_limit",capture.Reason);
        Assert.InRange(catalog.Get<Artifact>(capture.ArtifactId!).Length,1,20);
    }
    private sealed class FakeFactory(FakeSession session) : ICaptureSessionFactory
    {
        public Task<ICaptureSession> OpenAsync(TargetIdentity target,CaptureProfile profile,CancellationToken token) =>
            Task.FromResult<ICaptureSession>(session);
    }
    private sealed class FakeSession(byte[] bytes) : ICaptureSession
    {
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public byte[] Bytes { get; } = bytes;
        public int Stops { get; private set; }
        public Stream Stream { get; } = new ChunkStream(bytes);
        public Task StopAsync(CancellationToken token)
        {
            Stops++;
            ((ChunkStream)Stream).Stop();
            stopped.TrySetResult();
            return Task.CompletedTask;
        }
        public void Dispose() => Stream.Dispose();
    }
    private sealed class ChunkStream(byte[] bytes) : Stream
    {
        private int offset;
        private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Stop() => stopped.TrySetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken token = default)
        {
            if (offset == bytes.Length)
            {
                await stopped.Task.WaitAsync(token);
                await Task.Delay(30,token);
                return 0;
            }
            int count = Math.Min(7,Math.Min(buffer.Length,bytes.Length-offset));
            bytes.AsMemory(offset,count).CopyTo(buffer);
            offset += count;
            return count;
        }
        protected override void Dispose(bool disposing) { Stop(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => bytes.Length;
        public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override int Read(byte[] b,int o,int c) => throw new NotSupportedException();
        public override void Flush() { }
        public override long Seek(long o,SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b,int o,int c) => throw new NotSupportedException();
    }
}
