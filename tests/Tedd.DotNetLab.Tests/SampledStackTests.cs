using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class SampledStackTests
{
    private static SampleFrame Frame(string id)=>new(id,"Namespace.Type."+id,"module","managed_rundown",123,0x06000001);
    [Fact,Case("UT-MET-004","FR-023","M1","Unit")]
    public void Recursion_CountsOneInclusivePresencePerSampleAndOneLeafSelf()
    {
        var accumulator=new StackAccumulator(); var a=Frame("A"); var b=Frame("B");
        accumulator.Add([a,a,b,a]); accumulator.Add([b,a]); var evidence=accumulator.Snapshot();
        var method=evidence.Methods.Single(m=>m.Frame.Id=="A"); Assert.Equal(1,method.SelfCount); Assert.Equal(2,method.InclusiveCount);
        Assert.Equal(1,method.InclusiveShare); Assert.Equal(.5,method.SelfShare); Assert.Equal(2,evidence.ObservedSamples);
    }
    [Fact,Case("UT-MET-005","FR-023","M1","Unit")]
    public void NestedInclusiveShares_CanExceedOneWithoutNormalization()
    {
        var accumulator=new StackAccumulator(); accumulator.Add([Frame("A"),Frame("B"),Frame("C")]);
        var evidence=accumulator.Snapshot(); Assert.Equal(3,evidence.Methods.Sum(m=>m.InclusiveShare)); Assert.Equal(1,evidence.Methods.Sum(m=>m.SelfShare));
    }
    [Fact,Case("UT-MET-006","FR-013,FR-023","M1","Unit")]
    public void UnresolvedLeaf_StaysUnnamedAndReducesSymbolCoverage()
    {
        var accumulator=new StackAccumulator(); accumulator.Add([new("unknown",null,"native","unresolved",ulong.MaxValue),Frame("A")]); accumulator.Add([]);
        var evidence=accumulator.Snapshot(); Assert.Equal(.5,evidence.SymbolCoverage); Assert.Equal(1,evidence.MissingStacks);
        var unknown=evidence.Methods.Single(m=>m.Frame.Id=="unknown"); Assert.Null(unknown.Frame.Name); Assert.Equal(ulong.MaxValue,unknown.Frame.Address);
        Assert.Equal(1,unknown.SelfCount); Assert.Equal(.5,unknown.SelfShare); Assert.Contains("unresolved_stack_frames",evidence.Warnings);
        Assert.Throws<LabException>(()=>new StackAccumulator().Add([new("bad","guessed","native","unresolved",1)]));
    }
    [Fact,Case("UT-CAP-017","FR-016,FR-023","M1","Unit")]
    public void DepthAndCardinalityCaps_DoNotFabricateCompleteCoverage()
    {
        var accumulator=new StackAccumulator(2,1); accumulator.Add([Frame("A"),Frame("B")]);
        var evidence=accumulator.Snapshot(); Assert.Equal(1,evidence.TruncatedStacks); Assert.Single(evidence.Methods); Assert.Contains("stack_depth_cap",evidence.Warnings);
        Assert.Equal("resource_limit",Assert.Throws<LabException>(()=>new StackAccumulator(1).Add([Frame("A"),Frame("B")])).Code);
    }
    [Fact,Case("UT-MET-004","FR-023","M1","Property")]
    public void PermutingSamples_PreservesCountsAndShares()
    {
        var random=new Random(47291); var samples=new List<SampleFrame[]>();
        for(int i=0;i<200;i++) samples.Add(Enumerable.Range(0,random.Next(1,8)).Select(_=>Frame("M"+random.Next(5))).ToArray());
        var first=new StackAccumulator(); var second=new StackAccumulator();
        foreach(var sample in samples) first.Add(sample);
        foreach(var sample in samples.AsEnumerable().Reverse()) second.Add(sample);
        Assert.Equal(Wire.Hash(first.Snapshot()),Wire.Hash(second.Snapshot()));
    }
    [Fact,Case("IT-CAP-001,UT-MET-003,UT-CAP-006","FR-011,FR-013,FR-014,FR-023","M1","Integration")]
    public async Task CpuCapture_ResolvesManagedRundownAndPreservesSamplingMeaningAcrossRestart()
    {
        using var workspace=new TestWorkspace(); string resultId,traceId;
        await using(var service=new LabService(workspace.Root))
        {
            using var target=await CaptureTests.StartTarget();
            try
            {
                var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                var grant=service.IssueGrant(identity.Id,Permission.Observe,120,1);
                var captured=await ServiceTests.Wait(service,service.StartCapture(identity.Id,identity.Epoch,"cpu-diagnostic",3,grant.Id,"cpu-capture").Id);
                Assert.Equal(OperationState.Completed,captured.State); var capture=service.Catalog.Get<Capture>(captured.ResultId!); traceId=capture.ArtifactId!;
                var rawSummary=await service.AnalysisAsync(service.Catalog.List("analysis").Rows.Single().Id);
                Assert.True(rawSummary.ManagedSampleCount>0,Wire.Serialize(rawSummary.EventCounts));
                var tools=new LabTools(service); var response=await tools.Run(new StartStackAnalysis(capture.Id,"stacks")); Assert.Equal("ok",response.Status);
                var operation=Wire.Deserialize<Operation>(response.Data!.Value.GetRawText()); var complete=await ServiceTests.Wait(service,operation.Id);
                Assert.True(complete.State==OperationState.Completed,complete.State==OperationState.Completed?Wire.Serialize(complete):
                    Wire.Serialize(complete)+Environment.NewLine+service.Catalog.Get(operation.Id+"-failure").Json); resultId=complete.ResultId!;
                var report=await service.StackAnalysisAsync(resultId);
                Assert.NotNull(report.ParserExecution); Assert.True(report.ParserExecution.ExitVerified);
                Assert.NotEqual(Environment.ProcessId,report.ParserExecution.ProcessId);
                Assert.Equal("sampled_watchdog_and_final_worker_check; transient_write_overshoot_possible",report.ParserExecution.TemporaryByteEnforcement);
                Assert.True(report.ParserExecution.ObservedPeakTemporaryBytes>=0);
                Assert.True(report.Evidence.ObservedSamples>0,Wire.Serialize(report)); Assert.NotEmpty(report.Evidence.Methods);
                Assert.True(report.Evidence.ResolvedFrameOccurrences>0,Wire.Serialize(report));
                Assert.Equal(report.Evidence.ObservedSamples-report.Evidence.MissingStacks,report.Evidence.Methods.Sum(m=>m.SelfCount));
                Assert.Equal(operation.Id,service.StartStackAnalysis(capture.Id,"stacks").Id);
                Assert.Equal("ok",(await tools.Query(new QueryHotspots(resultId,2))).Status);
                var originalCaptureRecord=service.Catalog.Get(capture.Id);
                var selected=await service.SampledMethodAsync(resultId,report.Evidence.Methods[0].Frame.Id);
                Assert.Single(selected.Matches); Assert.Equal(report.Evidence.ObservedSamples,selected.ObservedSamples);
                Assert.Equal("post_capture_exact_method_identity_or_name",selected.Filtering);
                Assert.Equal("all_observed_samples_in_the_retained_analysis",selected.SampleDenominator);
                Assert.Contains("does not change provider enablement or reduce producer emission cost",selected.Meaning);
                Assert.Equal(new Provenance(capture.Id,traceId,report.ParserVersion,capture.Profile.Hash),selected.Provenance);
                Assert.Equal(originalCaptureRecord,service.Catalog.Get(capture.Id));
                Assert.Equal(capture.Profile.Hash,service.Catalog.Get<Capture>(capture.Id).Profile.Hash);
                Assert.Single(service.Catalog.List("capture").Rows);
                var absent=await service.SampledMethodAsync(resultId,"Unobserved.DeclaredMethod"); Assert.Equal(0,absent.SelectedSelfCount); Assert.Equal(0,absent.SelectedInclusiveCount);
                Assert.Equal(Availability.Partial,absent.Status); Assert.Empty(absent.Matches);
                Assert.Equal(selected.Provenance,absent.Provenance); Assert.Equal(selected.ObservedSamples,absent.ObservedSamples);
                var capped=await ServiceTests.Wait(service,service.StartStackAnalysis(capture.Id,"capped",new(MaximumSamples:1)).Id);
                Assert.Equal(OperationState.Completed,capped.State); var partial=await service.StackAnalysisAsync(capped.ResultId!);
                Assert.Equal(1,partial.Evidence.ObservedSamples); Assert.Contains("stack_sample_cap",partial.Quality.Warnings);
                service.Artifacts.Delete(service.Catalog.Get<Artifact>(traceId));
                var expired=await ServiceTests.Wait(service,service.StartStackAnalysis(capture.Id,"expired").Id); Assert.Equal("artifact_expired",expired.ErrorCode);
                Assert.False(Directory.EnumerateDirectories(Path.Combine(workspace.Root,"incoming"),"stack-conversion",SearchOption.AllDirectories).Any());
                Assert.False(Directory.EnumerateDirectories(Path.Combine(workspace.Root,"incoming"),"stack-conversion-*",SearchOption.AllDirectories).Any());
            }
            finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
        }
        await using var reopened=new LabService(workspace.Root); var retained=await reopened.StackAnalysisAsync(resultId);
        Assert.True(retained.Evidence.ObservedSamples>0); Assert.Equal(traceId,retained.TraceArtifactId);
        Assert.True(retained.ParserExecution!.ExitVerified);
        Assert.True(reopened.Catalog.IsPinned(reopened.Catalog.Get<StackAnalysisIndex>(resultId).ArtifactId));
    }
}
