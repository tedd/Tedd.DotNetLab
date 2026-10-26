using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace Tedd.DotNetLab.Harness;

[EventSource(Name = "Tedd.DotNetLab-Harness")]
public sealed class HarnessMarkers : EventSource
{
    public static HarnessMarkers Log { get; } = new();
    [Event(1, Level = EventLevel.Informational)]
    public void Boundary(string runId,string blockId,string executionId,long ticks,long frequency,string phase)
    {
        if (IsEnabled()) WriteEvent(1,runId,blockId,executionId,ticks,frequency,phase);
    }
    [NonEvent]
    public void Clock(string run,string block,string execution,string phase)
    {
        if (IsEnabled()) Boundary(run,block,execution,Stopwatch.GetTimestamp(),Stopwatch.Frequency,phase);
    }
    [NonEvent]
    public void Measurement(string run,string block,string execution,long start,long end)
    {
        if(!IsEnabled()) return;
        Boundary(run,block,execution,start,Stopwatch.Frequency,"measurement_start");
        Boundary(run,block,execution,end,Stopwatch.Frequency,"measurement_end");
    }
}
