using System.IO.Pipes;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class HarnessListenerLifetimeTests
{
    [Fact,Case("IT-HAR-RAPID-COMMANDS","FR-002,FR-003,FR-037,FR-064","M7","Integration")]
    public async Task IndependentMeasuredCommandsRetainAnOwnedTargetsListener()
    {
        using var workspace=new TestWorkspace(); using var process=await CaptureTests.StartTarget(workspace.Root);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            var target=new Tedd.DotNetLab.Collector.EventPipe.Targets().Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var client=new HarnessClient(); var handshake=await client.HandshakeAsync(target,deadline.Token);
            var scenario=handshake.Scenarios.Single(s=>s.Id=="cpu-sum");
            var grant=new Grant("rapid-grant",workspace.Root,target.Id,target.StartTimeUtcTicks,Permission.ExecuteRegisteredScenario,
                DateTimeOffset.UtcNow.AddMinutes(1),250);
            for(int command=0;command<250;command++)
            {
                var result=await client.ExecuteAsync(new("rapid-execution","run-"+command,"block-"+command,scenario.Id,"candidate",1,0,true,false,
                    0,handshake.Target,grant,grant.ExpiresAt),deadline.Token);
                Assert.Equal("run-"+command,result.RunId); Assert.Equal(1,result.CompletedOperations); Assert.True(result.Correct&&result.Quiescent);
            }
            Assert.False((await client.HandshakeAsync(target,deadline.Token)).Tainted);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("IT-HAR-LISTENER-LIFETIME","FR-003,FR-016,FR-037,FR-064","M7","Integration")]
    public async Task ImmediateIndependentCommandsCannotLoseTheListenerBetweenReplies()
    {
        using var workspace=new TestWorkspace(); var host=new HarnessHost(new ScenarioRegistry(),workspace.Root);
        using var stopping=new CancellationTokenSource(); using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var listener=host.ListenAsync(stopping.Token);
        try
        {
            for(int command=0;command<500;command++)
            {
                await using var pipe=new NamedPipeClientStream(".",HarnessHost.PipeName(host.Target),PipeDirection.InOut,
                    PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.ConnectAsync(deadline.Token); PeerIdentity.VerifyServer(pipe,host.Target);
                await Frames.WriteAsync(pipe,new HarnessControl("unsupported-protocol","handshake",host.Target),deadline.Token);
                var reply=await Frames.ReadAsync<HarnessReply>(pipe,deadline.Token);
                Assert.Equal("error",reply.Status); Assert.Equal("schema_unsupported",reply.Code);
            }
            Assert.False(listener.IsCompleted);
        }
        finally { stopping.Cancel(); await listener.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
}
