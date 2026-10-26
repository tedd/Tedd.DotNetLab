using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class EvidenceAvailabilityTests
{
    private static Capture Capture(string profile)=>new("capture",new("target",1,1,"runtime","10.0.12","x64"),
        Profiles.Resolve(profile),CaptureState.Sealed,"artifact",null,DateTimeOffset.UtcNow);
    private static TraceSummary Empty(Capture capture)=>new("analysis",capture.Id,"artifact",capture.Profile.Hash,TraceAnalyzer.Version,
        new("parsed_capture","none_reported",TraceAnalyzer.Version,[]),[],[],[],[],[],0,new Dictionary<string,long>());
    [Theory]
    [InlineData("baseline-context","gc.cycle_count")]
    [InlineData("allocation-diagnostic","allocation.sampled_bytes")]
    [InlineData("cpu-diagnostic","samples.count")]
    [Case("UT-AVL-001", "FR-020,FR-021,FR-022", "M1", "Unit")]
    public void SilentProvider_IsUnavailableRatherThanAnAssertedZero(string profile,string metric)
    {
        var capture=Capture(profile); var result=new TraceAnalyzer().Metric(Empty(capture),capture,metric);
        Assert.Equal(Availability.NotCollected,result.Status); Assert.Null(result.Value); Assert.NotNull(result.Reason);
    }
    [Fact, Case("UT-AVL-ZERO", "FR-020", "M1", "Unit")]
    public void ExplicitZeroPayload_RemainsDistinctFromAbsentEvidence()
    {
        var capture=Capture("allocation-diagnostic");
        var summary=Empty(capture) with { Allocations=[new(1,0,"fixture")] };
        var result=new TraceAnalyzer().Metric(summary,capture,"allocation.sampled_bytes");
        Assert.Equal(Availability.Available,result.Status); Assert.Equal(0,result.Value);
    }
}
