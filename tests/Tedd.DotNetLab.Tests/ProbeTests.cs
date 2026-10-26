using Tedd.DotNetLab.Probes;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ProbeTests
{
    [Fact, Case("UT-PROBE-001", "FR-050,FR-051", "M5", "Unit")]
    public void CountAndTiming_SeparateNormalExceptionalRecursionAndInflight()
    {
        ProbeRuntime.Begin(2,true);
        ProbeRuntime.WarmCurrentThread();
        ProbeRuntime.Enter(0); ProbeRuntime.Enter(0); ProbeRuntime.Enter(1);
        Assert.Throws<InvalidOperationException>(()=>ProbeRuntime.Stop());
        ProbeRuntime.ExitExceptional(1); ProbeRuntime.Exit(0); ProbeRuntime.Exit(0);
        var result=ProbeRuntime.Stop();
        Assert.True(result.Complete);
        Assert.Equal(0,result.FirstUseThreads);
        var recursion=Assert.Single(result.Rows,r=>r.MethodId==0);
        Assert.Equal(2,recursion.Entered); Assert.Equal(2,recursion.NormallyCompleted); Assert.Equal(0,recursion.InFlight);
        Assert.True(recursion.NormalElapsedTicks>0);
        Assert.Equal(1,Assert.Single(result.Rows,r=>r.MethodId==1).ExceptionallyCompleted);
    }
    [Fact, Case("UT-PROBE-003", "FR-050", "M5", "Unit")]
    public void NestingOverflow_IsExplicitAndNeverClaimsCompleteTiming()
    {
        ProbeRuntime.Begin(1,true,maximumNesting:1);
        ProbeRuntime.Enter(0); ProbeRuntime.Enter(0); ProbeRuntime.Exit(0); ProbeRuntime.Exit(0);
        var result=ProbeRuntime.Stop(); var row=Assert.Single(result.Rows);
        Assert.False(result.Complete); Assert.Equal(1,row.Overflow); Assert.Equal(1,row.Dropped);
        Assert.Equal(1,result.FirstUseThreads);
        Assert.Equal(2,row.Entered); Assert.Equal(1,row.NormallyCompleted); Assert.Equal(0,row.InFlight);
    }
    [Fact, Case("UT-PROBE-004", "FR-050", "M5", "Unit")]
    public void ThreadBudget_IsBoundedAndReportsDroppedInvocations()
    {
        ProbeRuntime.Begin(1,false,threads:1); ProbeRuntime.WarmCurrentThread();
        var thread=new Thread(()=>{ ProbeRuntime.Enter(0); ProbeRuntime.Exit(0); }); thread.Start(); thread.Join();
        ProbeRuntime.Enter(0); ProbeRuntime.Exit(0);
        var result=ProbeRuntime.Stop();
        Assert.False(result.Complete); Assert.Equal(1,result.DroppedThreads);
        Assert.Null(Assert.Single(result.Rows).NormalElapsedTicks);
    }
    [Fact, Case("UT-PROBE-005", "FR-050", "M5", "Integration")]
    public void ConcurrentFlush_RejectsLiveFramesAndRetainsEveryCompletedCount()
    {
        ProbeRuntime.Begin(1,false,threads:4);
        using var ready=new CountdownEvent(4); using var finish=new ManualResetEventSlim();
        var threads=Enumerable.Range(0,4).Select(_=>new Thread(()=>
        {
            ProbeRuntime.WarmCurrentThread(); ProbeRuntime.Enter(0); ready.Signal(); finish.Wait(); ProbeRuntime.Exit(0);
            for(int i=0;i<100;i++) { ProbeRuntime.Enter(0); ProbeRuntime.Exit(0); }
        })).ToArray();
        foreach(var thread in threads) thread.Start(); ready.Wait();
        Assert.Throws<InvalidOperationException>(()=>ProbeRuntime.Stop());
        finish.Set(); foreach(var thread in threads) thread.Join();
        var result=ProbeRuntime.Stop();
        Assert.True(result.Complete); Assert.Equal(404,result.Rows.Sum(r=>r.Entered));
        Assert.Equal(404,result.Rows.Sum(r=>r.NormallyCompleted)); Assert.Equal(4,result.Rows.Count);
    }
    [Fact, Case("UT-PROBE-012", "FR-015", "M5", "Performance")]
    public void WarmedAndDisabledHelpers_HaveNoSteadyStateManagedAllocation()
    {
        ProbeRuntime.Begin(1,true); ProbeRuntime.WarmCurrentThread();
        for(int i=0;i<1000;i++) { ProbeRuntime.Enter(0); ProbeRuntime.Exit(0); }
        long before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++) { ProbeRuntime.Enter(0); ProbeRuntime.Exit(0); }
        long allocated=GC.GetAllocatedBytesForCurrentThread()-before;
        Assert.Equal(0,allocated); Assert.True(ProbeRuntime.Stop().Complete);
        before=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<10000;i++) { ProbeRuntime.Enter(0); ProbeRuntime.Exit(0); }
        Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
    }
}
