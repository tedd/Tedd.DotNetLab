using System.Text.Json;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class WindowTests
{
    private static Capture Capture()=>new("capture",new("target",1,1,"runtime","10.0.12","X64"),Profiles.Resolve("baseline-context"),CaptureState.Sealed,"artifact",null,DateTimeOffset.UtcNow);
    private static Measurement Measurement()=>new("exec","run","block","baseline",100,200,1000,10,0,0,1,1,true,null,"input","1");
    private static TraceSummary Summary(Capture capture)=>new("analysis",capture.Id,"artifact",capture.Profile.Hash,TraceAnalyzer.Version,
        new("parsed_capture","none_reported",TraceAnalyzer.Version,[]),[new(1,1,0,"test","BackgroundGC",50,220)],[],[],[],
        [new("run","block","exec",0,1000,0,"clock_before"),new("run","block","exec",300,1000,300,"clock_after"),
         new("run","block","exec",100,1000,250,"measurement_start"),new("run","block","exec",200,1000,251,"measurement_end")],0,new Dictionary<string,long>(),
        [new(1,90,110,"SuspendForGC","complete"),new(1,150,180,"SuspendForGCPrep","complete"),new(1,170,210,"SuspendForGC","complete"),
         new(1,100,200,"SuspendForDebugger","complete")],new(0,400));
    [Fact,Case("UT-GC-005","FR-012,FR-022","M1","Unit")]
    public void TargetTicks_UnionGcOverlap_DoesNotUseLateMarkerEmissionOrCycleDuration()
    {
        var capture=Capture(); var window=WindowAnalyzer.Analyze(Summary(capture),capture,Measurement());
        Assert.Equal(Availability.Available,window.Availability); Assert.Equal(60e6,window.GcSuspensionOverlap.Value);
        Assert.Equal(100,window.StartMilliseconds); Assert.Equal(200,window.EndMilliseconds); Assert.Equal(1,window.OverlappingGcCycles);
        Assert.Equal("target_measurement_markers",window.BoundarySource); Assert.Equal(0,window.ClockMapping!.MaximumResidualMilliseconds);
        Assert.Null(window.ClockFitFailure); Assert.DoesNotContain("clock_fit_failure",Wire.Serialize(window));
    }
    [Fact,Case("UT-TIME-010","FR-012,FR-013","M1","Unit")]
    public void MissingSemanticMarkers_UsesExplicitSealedTickRecoveryOnlyWithValidClockCoverage()
    {
        var capture=Capture(); var source=Summary(capture);
        var missing=source with { Markers=source.Markers.Where(marker=>marker.Phase.StartsWith("clock_",StringComparison.Ordinal)).ToArray() };
        var window=WindowAnalyzer.Analyze(missing,capture,Measurement());
        Assert.Equal("persisted_measurement_clock_recovery",window.BoundarySource);
        Assert.Contains("missing_measurement_marker_recovered_from_sealed_ticks",window.Warnings);
        Assert.Equal(60e6,window.GcSuspensionOverlap.Value);
        var absent=WindowAnalyzer.Analyze(source with { Markers=[] },capture,Measurement());
        Assert.Equal(Availability.NotCollected,absent.Availability); Assert.Null(absent.StartMilliseconds);
    }
    [Fact,Case("UT-GC-007","FR-013,FR-022","M1","Unit")]
    public void PartialSuspensionAndTraceLoss_PreserveIncompleteEvidenceWithoutInventedEndpoints()
    {
        var capture=Capture(); var source=Summary(capture);
        var incomplete=source with { SuspensionEvidence=source.SuspensionEvidence!.Concat([new SuspensionInterval(1,160,null,"SuspendForGC","missing_end")]).ToArray() };
        var result=WindowAnalyzer.Analyze(incomplete,capture,Measurement());
        Assert.Equal(Availability.Partial,result.Availability); Assert.Equal(60e6,result.GcSuspensionOverlap.Value);
        Assert.Contains("incomplete_suspension_may_overlap_run",result.Warnings);
        Assert.Equal(Availability.Partial,WindowAnalyzer.Analyze(source with { Quality=source.Quality with { LossStatus="unknown" } },capture,Measurement()).Availability);
        Assert.Equal(Availability.NotCollected,WindowAnalyzer.Analyze(source with { SuspensionEvidence=[],Suspensions=[] },capture,Measurement()).Availability);
    }
    [Fact,Case("UT-TIME-005","FR-013,FR-032","M1","Unit")]
    public void InvalidResidualFrequencyOrMarkerIdentity_DoesNotProducePreciseOverlap()
    {
        var capture=Capture(); var source=Summary(capture);
        var drift=source with { Markers=source.Markers.Take(2).Concat([new HarnessMarker("r2","b2","exec",150,1000,200,"clock_before")]).ToArray() };
        var rejected=WindowAnalyzer.Analyze(drift,capture,Measurement());
        Assert.Equal(Availability.Invalid,rejected.Availability); Assert.Null(rejected.ClockMapping); Assert.Null(rejected.GcSuspensionOverlap.Value);
        var failure=Assert.IsType<ClockFitFailure>(rejected.ClockFitFailure);
        Assert.Equal("excessive_residual",failure.Failure); Assert.Equal(3,failure.PairCount); Assert.Equal(1,failure.ToleranceMilliseconds);
        Assert.Equal(1,failure.EstimatedMillisecondsPerTick); Assert.Equal(100d/3,failure.MaximumResidualMilliseconds!.Value,10);
        Assert.Equal(new ClockPoint(150,200),failure.WorstObservedPair);
        Assert.NotEqual(WindowAnalyzer.Analyze(source,capture,Measurement()).Id,rejected.Id);
        Assert.Equal(Wire.Hash(rejected),Wire.Hash(Wire.Deserialize<RunWindow>(Wire.Serialize(rejected))));
        Assert.Equal(Availability.Invalid,WindowAnalyzer.Analyze(source with { Markers=source.Markers.Select(marker=>marker with { Frequency=100 }).ToArray() },capture,Measurement()).Availability);
        Assert.Equal(Availability.Invalid,WindowAnalyzer.Analyze(source with { Markers=source.Markers.Select(marker=>marker.Phase=="measurement_end"?marker with { Ticks=199 }:marker).ToArray() },capture,Measurement()).Availability);
    }
    [Theory,InlineData("count","insufficient_pairs"),InlineData("pair","invalid_pair"),InlineData("separation","no_tick_separation"),
        InlineData("slope","nonpositive_slope"),InlineData("overflow","nonfinite_fit"),InlineData("tolerance","invalid_tolerance"),
        Case("UT-TIME-FIT-DIAGNOSTICS","FR-012,FR-013,FR-021,FR-074","M1","Unit")]
    public void FailedFitsRetainTheirFailureStageWithoutNonfiniteWireValues(string fixture,string reason)
    {
        ClockPoint[] points=fixture switch
        {
            "count"=>[new(0,0)],"pair"=>[new(0,0),new(1,double.NaN)],"separation"=>[new(0,0),new(0,1)],
            "slope"=>[new(0,1),new(1,0)],"overflow"=>[new(0,-double.MaxValue),new(1,double.MaxValue)],_=>[new(0,0),new(1,1)]
        };
        ClockFitFailure? failure=null;
        Assert.Equal("clock_mapping_invalid",Assert.Throws<LabException>(()=>Intervals.Fit(points,fixture=="tolerance"?double.NaN:1,out failure)).Code);
        Assert.Equal(reason,failure!.Failure); string json=Wire.Serialize(failure); Assert.DoesNotContain("NaN",json); Assert.DoesNotContain("Infinity",json);
        Assert.Equal(failure,Wire.Deserialize<ClockFitFailure>(json));
    }
    [Fact,Case("UT-GC-009","FR-022","M1","Unit")]
    public void ReorderedEvents_KeepRuntimeGcIdentityAndOtherSuspensions()
    {
        GcObservation[] gc=[new(1,4,2,"test","BackgroundGC",200,false),new(2,4,1,"test","NonConcurrentGC",90,true),new(1,4,2,"test","BackgroundGC",30,true),new(2,4,1,"test","NonConcurrentGC",100,false)];
        SuspensionObservation[] pauses=[new(1,180,false),new(1,150,true,"SuspendForGC"),new(2,170,false),new(2,160,true,"SuspendForDebugger"),new(3,200,true,"SuspendForGC")];
        var result=TemporalReconstruction.Reconstruct(gc,pauses);
        Assert.Equal(2,result.GcCycles.Count); Assert.Equal(200,result.GcCycles.Single(cycle=>cycle.RuntimeInstance==1).EndMilliseconds);
        Assert.Contains(result.Suspensions,interval=>interval.Reason=="SuspendForDebugger"&&interval.Completeness=="complete");
        Assert.Null(result.Suspensions.Single(interval=>interval.RuntimeInstance==3).EndMilliseconds);
        var duplicate=gc[0] with { RecordIdentity="known-event" };
        Assert.Contains("known_duplicate_record",TemporalReconstruction.Reconstruct([duplicate,duplicate],[]).Warnings);
    }
    [Fact,Case("UT-TIME-008","FR-012,FR-022","M1","Unit")]
    public void HalfOpenBoundariesAndInvalidIntervals_AreExplicit()
    {
        Assert.Equal(0,Intervals.UnionOverlap([new(50,100),new(200,250)],100,200));
        Assert.Throws<LabException>(()=>Intervals.UnionOverlap([new(double.NaN,150)],100,200));
        var capture=Capture(); var source=Summary(capture) with { SuspensionEvidence=[new(1,200,220,"SuspendForGC","complete")] };
        Assert.Equal(0,WindowAnalyzer.Analyze(source,capture,Measurement()).GcSuspensionOverlap.Value);
    }
    [Fact,Case("IT-HAR-001","FR-012,FR-020,FR-022","M1","Integration")]
    public async Task CapturedGcStimulus_MapsActualMeasuredTicksAndSupportsRetrospectiveWindowQuery()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            string runId,analysisId,traceId,windowId;
            await using(var service=new LabService(workspace.Root))
            {
                var identity=service.Discover().Single(item=>item.Identity.ProcessId==target.Id).Identity;
                var scenario=(await service.ScenariosAsync(identity.Id,CancellationToken.None)).Scenarios.Single(item=>item.Id=="gc-window");
                var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,10,256,1,42,"baseline-context",false,"",false,60,new()));
                var execute=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,120,20);
                var observe=service.IssueGrant(identity.Id,Permission.Observe,120,1);
                var completed=await ServiceTests.Wait(service,(await service.StartExecutionAsync(identity.Id,0,plan.Id,execute.Id,"gc-window",CancellationToken.None,observe.Id)).Id);
                Assert.True(completed.State==OperationState.Completed,completed.ErrorCode);
                var execution=service.Catalog.Get<ExecutionRecord>(completed.ResultId!); runId=execution.RunIds[execution.RunIds.Count/2];
                var index=service.Catalog.List("analysis").Rows.Select(row=>Wire.Deserialize<AnalysisIndex>(row.Json)).Single(item=>item.CaptureId==execution.CaptureId);
                analysisId=index.Id; var summary=await service.AnalysisAsync(analysisId); traceId=summary.ArtifactId;
                Assert.Contains(summary.Markers,marker=>marker.Phase=="measurement_start"&&marker.RunId==runId);
                Assert.NotEmpty(summary.SuspensionEvidence!);
                var reply=await new LabTools(service).Query(new QueryRunWindow(analysisId,runId)); Assert.True(reply.Status=="ok",Wire.Serialize(reply));
                var window=reply.Data!.Value.Deserialize<RunWindow>(Wire.Json)!; windowId=window.Id;
                Assert.True(window.Availability is Availability.Available or Availability.Partial,Wire.Serialize(window));
                Assert.Equal("target_measurement_markers",window.BoundarySource); Assert.NotNull(window.ClockMapping);
                Assert.True(window.GcSuspensionOverlap.Value>0,Wire.Serialize(window));
            }
            if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); }
            await using var reopened=new LabService(workspace.Root);
            Assert.Equal(windowId,(await reopened.RunWindowAsync(analysisId,runId)).Id);
            reopened.Artifacts.Delete(reopened.Catalog.Get<Artifact>(traceId));
            Assert.Equal(Availability.ArtifactExpired,(await reopened.RunWindowAsync(analysisId,runId)).Availability);
        }
        finally { if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); } }
    }
}
