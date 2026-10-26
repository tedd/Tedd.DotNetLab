using System.Diagnostics;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class MachineMeasurementTests
{
    [Fact,Case("IT-MACHINE-MUTEX-001","FR-004,FR-015","M1","Integration")]
    public async Task DifferentProcessesCannotMeasureOrParseConcurrentlyAndCancellationDoesNotUnlockOwner()
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var start=RuntimeSupport.StartInfo(RuntimeSupport.Current);
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.ControllerFault","Release","net10.0","Tedd.DotNetLab.ControllerFault.dll"));
        start.ArgumentList.Add("--measurement-mutex-worker");
        start.RedirectStandardInput=true; start.RedirectStandardOutput=true; start.RedirectStandardError=true;
        using var worker=Process.Start(start)!;
        try
        {
            Assert.Equal("acquired",await worker.StandardOutput.ReadLineAsync(timeout.Token));
            var first=new ParserMeasurementGate(); var second=new ParserMeasurementGate();
            using var cancelled=CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
            var measurement=first.EnterMeasurementAsync(cancelled.Token); var parser=second.EnterParserAsync(timeout.Token);
            await Task.Delay(100,timeout.Token); Assert.False(measurement.IsCompleted); Assert.False(parser.IsCompleted);
            cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>measurement);
            Assert.False(parser.IsCompleted); Assert.Equal(0,first.State.ActiveMeasurements);
            worker.StandardInput.Close(); await worker.WaitForExitAsync(timeout.Token);
            Assert.Equal(0,worker.ExitCode); using var permit=await parser.WaitAsync(timeout.Token);
        }
        finally { if(!worker.HasExited) { worker.Kill(true); await worker.WaitForExitAsync(); } }
    }

    [Fact,Case("UT-MACHINE-MUTEX-002","FR-004,FR-053","M1","Unit")]
    public async Task UncertainTargetBlocksOtherControllersUntilVerifiedExit()
    {
        bool gone=false;
        var first=new ParserMeasurementGate(_=>Volatile.Read(ref gone)); var second=new ParserMeasurementGate();
        var target=new TargetIdentity("target",1,2,"runtime","10.0.12","x64");
        var lease=await first.EnterMeasurementAsync(); lease.RetainForUncertainTarget(target); lease.Dispose();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var next=second.EnterMeasurementAsync(timeout.Token);
        await Task.Delay(100,timeout.Token); Assert.False(next.IsCompleted);
        Volatile.Write(ref gone,true); using var acquired=await next;
        Assert.Equal(0,first.State.UncertainMeasurements);
    }
}
