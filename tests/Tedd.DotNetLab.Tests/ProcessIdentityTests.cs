using System.Diagnostics;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ProcessIdentityTests
{
    private static string Stat(string counter="101",string name="untrusted ) spaced name")=>
        "17 ("+name+") S "+string.Join(' ',Enumerable.Repeat("0",18))+" "+counter+" 0 0";

    [Fact,Case("UT-ID-LINUX-START","FR-002,FR-070","M7","Unit")]
    public void LinuxKernelCounterPreservesIdentityWithoutTimestampTolerance()
    {
        long first=ProcessIdentity.ParseLinuxStartTime(Stat(),17,1000,100);
        Assert.Equal(DateTime.UnixEpoch.Ticks+1000*TimeSpan.TicksPerSecond+101*100000,first);
        Assert.Equal(first,ProcessIdentity.ParseLinuxStartTime(Stat(name:"another ( name )"),17,1000,100));
        Assert.Equal(first+100000,ProcessIdentity.ParseLinuxStartTime(Stat("102"),17,1000,100));
    }
    [Theory,InlineData("negative"),InlineData("overflow"),InlineData("truncated"),InlineData("pid"),InlineData("frequency"),InlineData("oversized"),
        Case("UT-ID-LINUX-INVALID","FR-002,FR-016,FR-070","M7","Unit")]
    public void MalformedKernelIdentityCannotBecomeAUsableStart(string defect)
    {
        string stat=defect switch { "negative"=>Stat("-1"),"overflow"=>Stat(ulong.MaxValue.ToString()),
            "truncated"=>"17 (name) S 0","oversized"=>new string('x',65537),_=>Stat() };
        Assert.Equal("transport_identity_unavailable",Assert.Throws<LabException>(()=>
            ProcessIdentity.ParseLinuxStartTime(stat,defect=="pid"?18:17,1000,defect=="frequency"?0:100)).Code);
    }
    [Fact,Case("UT-ID-START-HANDLES","FR-002","M7","Integration")]
    public async Task IndependentProcessHandlesRetainTheSameStartIdentity()
    {
        using var first=Process.GetCurrentProcess(); long start=ProcessIdentity.StartTimeUtcTicks(first);
        await Task.Delay(25);
        using var second=Process.GetProcessById(first.Id);
        Assert.Equal(start,ProcessIdentity.StartTimeUtcTicks(second));
        if(OperatingSystem.IsWindows()) Assert.Equal(first.StartTime.ToUniversalTime().Ticks,start);
    }
}
