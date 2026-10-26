using System.IO.Pipes;
using System.Diagnostics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class PeerIdentityTests
{
    [Theory, InlineData(false), InlineData(true), Case("UT-ID-PEER-START", "FR-002,FR-003,FR-070", "M7", "Integration")]
    public async Task ConnectedPeer_RequiresMatchingPidAndStart(bool staleStart)
    {
        using var process=Process.GetCurrentProcess();
        string name="dotnetlab-peer-"+Guid.NewGuid().ToString("N");
        var target=new TargetIdentity("peer",process.Id,ProcessIdentity.StartTimeUtcTicks(process)-(staleStart?1:0),"peer","10.0.12","x64");
        await using var server=new NamedPipeServerStream(name,PipeDirection.InOut,1,
            PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        await using var client=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected=server.WaitForConnectionAsync(timeout.Token);
        await client.ConnectAsync(timeout.Token); await connected;
        if(staleStart)
            Assert.Equal("target_identity_changed",Assert.Throws<LabException>(()=>PeerIdentity.VerifyServer(client,target)).Code);
        else
        {
            PeerIdentity.VerifyServer(client,target);
            Assert.True(client.IsConnected);
        }
    }
    [Fact, Case("UT-SEC-PEER", "FR-002,FR-070,FR-074", "M7", "Integration")]
    public async Task CurrentUserPipe_CannotImpersonateAnotherTargetProcess()
    {
        var target=new TargetIdentity("fixture",int.MaxValue,DateTimeOffset.UtcNow.Ticks,"fixture","10.0.12","x64");
        await using var server=new NamedPipeServerStream(ProfilerClient.PipeName(target),PipeDirection.InOut,1,
            PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var connected=server.WaitForConnectionAsync(timeout.Token);
        var request=new ProfilerClient().HandshakeAsync(target,timeout.Token);
        await connected;
        Assert.Equal("target_identity_changed",(await Assert.ThrowsAsync<LabException>(()=>request)).Code);
        byte[] buffer=new byte[1];
        Assert.Equal(0,await server.ReadAsync(buffer,timeout.Token));
    }
}
