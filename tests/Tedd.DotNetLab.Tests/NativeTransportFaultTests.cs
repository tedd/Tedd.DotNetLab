using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class NativeTransportFaultTests
{
    [Fact, Case("IT-NATIVE-IPC-STOP", "FR-003,FR-016,FR-074", "M7", "Integration")]
    public async Task Shutdown_PartialFrameCannotHoldProfilerWorker()
    {
        using var workspace=new TestWorkspace(); using var targetProcess=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==targetProcess.Id).Identity;
            await using var pipe=new NamedPipeClientStream(".",ProfilerClient.PipeName(target),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await pipe.ConnectAsync(deadline.Token); await pipe.WriteAsync(new byte[]{1},deadline.Token);
            await targetProcess.StandardInput.WriteLineAsync("exit");
            await targetProcess.WaitForExitAsync(deadline.Token); Assert.Equal(0,targetProcess.ExitCode);
        }
        finally { if(!targetProcess.HasExited) { targetProcess.Kill(true); await targetProcess.WaitForExitAsync(); } }
    }
    [Fact, Case("UT-SEC-NATIVE-DEPTH", "FR-061,FR-070,FR-074", "M7", "Integration")]
    public async Task NativeParser_RejectsDeepInputAndRemainsAvailable()
    {
        using var workspace=new TestWorkspace(); using var targetProcess=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==targetProcess.Id).Identity;
            await using(var pipe=new NamedPipeClientStream(".",ProfilerClient.PipeName(target),PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly))
            {
                using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await pipe.ConnectAsync(deadline.Token);
                string nested="0"; for(int i=0;i<40;i++) nested="{\"next\":"+nested+"}";
                byte[] payload=Encoding.UTF8.GetBytes("{\"deep\":"+nested+"}"); byte[] header=new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header,payload.Length);
                await pipe.WriteAsync(header,deadline.Token); await pipe.WriteAsync(payload,deadline.Token);
                var result=await Frames.ReadAsync<ProfilerReply>(pipe,deadline.Token);
                await pipe.WriteAsync(new byte[]{1},deadline.Token);
                Assert.Equal("resource_limit",result.Code);
            }
            Assert.Equal(target.Id,(await new ProfilerClient().HandshakeAsync(target,CancellationToken.None)).TargetId);
        }
        finally { if(!targetProcess.HasExited) { targetProcess.Kill(true); await targetProcess.WaitForExitAsync(); } }
    }
}
