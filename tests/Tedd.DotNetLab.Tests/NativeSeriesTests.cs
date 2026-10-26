using System.Diagnostics;
using System.Text.Json;
using System.Security.Cryptography;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Storage;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class NativeSeriesTests
{
    [Fact,Case("UT-STAT-009,UT-STAT-016","FR-032,FR-033,FR-034","M6","Unit")]
    public void SessionAggregationAndInference_AgreeWithIndependentScipyOracle()
    {
        using var fixture=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics","native-series-golden.json")));
        Assert.Equal(fixture.RootElement.GetProperty("oracle").GetProperty("script_sha256").GetString(),Convert.ToHexStringLower(
            SHA256.HashData(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,"Fixtures","statistics","native_series_reference.py")))));
        var aggregates=new List<CostPair>();
        foreach(var session in fixture.RootElement.GetProperty("sessions").EnumerateArray())
        {
            string id=session.GetProperty("id").GetString()!;
            var baseline=session.GetProperty("baseline").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
            var candidate=session.GetProperty("candidate").EnumerateArray().Select(v=>v.GetDouble()).ToArray();
            var aggregate=NativeSessionEngine.Aggregate(id,baseline.Select((b,i)=>new CostPair(i.ToString(),id,b,candidate[i])).ToArray());
            Assert.Equal(session.GetProperty("geometric_baseline").GetDouble(),aggregate.Baseline,10);
            Assert.Equal(session.GetProperty("geometric_candidate").GetDouble(),aggregate.Candidate,10); aggregates.Add(aggregate);
        }
        var result=StatisticalEngine.Paired(aggregates,new()); var expected=fixture.RootElement.GetProperty("expected");
        Assert.Equal(expected.GetProperty("ratio").GetDouble(),result.Ratio!.Value,12);
        Assert.Equal(expected.GetProperty("se").GetDouble(),result.StandardError!.Value,12);
        Assert.Equal(expected.GetProperty("df").GetDouble(),result.DegreesOfFreedom!.Value);
        Assert.Equal(10,result.IndependentBlocks); Assert.Equal(EffectClass.Improved,result.EffectClass); Assert.False(result.PromotionEligible);
        Assert.Equal(expected.GetProperty("difference_p").GetDouble(),result.DifferenceP!.Value,12);
        Assert.Equal(expected.GetProperty("tost_p").GetDouble(),result.TostP!.Value,12);
        Assert.Equal(expected.GetProperty("difference_interval")[0].GetDouble(),result.DifferenceIntervalRatio!.Lower,12);
        Assert.Equal(expected.GetProperty("difference_interval")[1].GetDouble(),result.DifferenceIntervalRatio.Upper,12);
        Assert.Equal(expected.GetProperty("equivalence_interval")[0].GetDouble(),result.EquivalenceIntervalRatio!.Lower,12);
        Assert.Equal(expected.GetProperty("equivalence_interval")[1].GetDouble(),result.EquivalenceIntervalRatio.Upper,12);
        Assert.Equal("descriptive_only",StatisticalEngine.Paired(aggregates,new(),independent:false).Quality);
        Assert.Throws<LabException>(()=>NativeSessionEngine.Aggregate("session",[new("a","other",1,2)]));
        Assert.Throws<LabException>(()=>NativeSessionEngine.Aggregate("session",[new("a","session",1,2),new("a","session",2,3)]));
    }
    [Fact,Case("UT-EXP-019,UT-STAT-016","FR-031,FR-035,FR-062","M6","Integration")]
    public void FreezeIsAtomicAgainstAcceptedTrialsAndOtherSeriesMembership()
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root);
        var first=catalog.Put("first","native_trial_plan",new { id="first" }); var second=catalog.Put("second","native_trial_plan",new { id="second" });
        var references=new[]{new CatalogSourceReference(first.Id,first.Revision,first.Hash),new CatalogSourceReference(second.Id,second.Revision,second.Hash)};
        catalog.Accept("local","native-trial-second","request","mutation","target");
        Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>catalog.FreezeNativeSeries("rejected",new { id="rejected" },references)).Code);
        Assert.Empty(catalog.List("native_series_plan").Rows); Assert.Empty(catalog.List("native_series_member").Rows);
        catalog.FreezeNativeSeries("frozen",new { id="frozen" },references.Take(1).ToArray());
        Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>catalog.FreezeNativeSeries("another",new { id="another" },references.Take(1).ToArray())).Code);
        Assert.Single(catalog.List("native_series_plan").Rows); Assert.Single(catalog.List("native_series_member").Rows);
    }
    private static async Task<(NativeTrialPlan Plan,CilCandidateRecord Original,CilCandidateRecord Candidate)> Prepare(LabService service,Process process,int seed)
    {
        var source=await CompilationControlTests.Prepare(service,process.Id);
        var controlPlan=await service.CreateCompilationControlAsync(source.Candidate.Id,"native-value",blocks:2);
        var mutate=service.IssueMutationGrant(source.Target.Id,source.Candidate.Id,300,1); var execute=service.IssueGrant(source.Target.Id,Permission.ExecuteRegisteredScenario,300,6);
        var inspect=service.IssueGrant(source.Target.Id,Permission.InspectLiveCode,300,12);
        var complete=await ServiceTests.Wait(service,(await service.StartCompilationControlAsync(controlPlan.Id,mutate.Id,execute.Id,inspect.Id,Wire.Id("series-control"))).Id);
        Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete));
        var outcome=service.Catalog.Get<MutationOutcome>(complete.ResultId!);
        var candidate=await CompilationAdmissionTests.Candidate(service,source.InspectionId,source.Candidate.OriginalIlHash);
        var plan=await service.CreateNativeTrialAsync(candidate.Id,outcome.CompilationControlResultId!,blocks:2,seed:(ulong)seed);
        return(plan,source.Candidate,candidate);
    }
    private static async Task<NativeTrialResult> Execute(LabService service,(NativeTrialPlan Plan,CilCandidateRecord Original,CilCandidateRecord Candidate) source)
    {
        var original=service.IssueMutationGrant(source.Plan.Target.Id,source.Original.Id,300,2); var candidate=service.IssueMutationGrant(source.Plan.Target.Id,source.Candidate.Id,300,2);
        var execute=service.IssueGrant(source.Plan.Target.Id,Permission.ExecuteRegisteredScenario,300,12); var inspect=service.IssueGrant(source.Plan.Target.Id,Permission.InspectLiveCode,300,24);
        var complete=await ServiceTests.Wait(service,(await service.StartNativeTrialAsync(source.Plan.Id,original.Id,candidate.Id,execute.Id,inspect.Id,Wire.Id("series-trial"))).Id);
        Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete)); return service.Catalog.Get<NativeTrialResult>(complete.ResultId!);
    }
    [Fact,Case("IT-E2E-002,UT-STAT-016,UT-EXP-019","FR-031,FR-032,FR-033,FR-035,FR-052,FR-055","M6","Integration")]
    public async Task TenFrozenSessions_AggregateFortyRunsIntoTenUnitsAndRetainVerifiedInputsAcrossRestart()
    {
        using var workspace=new TestWorkspace(); var processes=new List<Process>(); string datasetId,comparisonId;
        try
        {
            await using(var service=new LabService(workspace.Root))
            {
                var sources=new List<(NativeTrialPlan Plan,CilCandidateRecord Original,CilCandidateRecord Candidate)>();
                for(int i=0;i<10;i++) { var process=await ProfilerTests.StartNativeTarget(workspace.Root); processes.Add(process); sources.Add(await Prepare(service,process,700+i)); }
                var sameSession=await service.CreateNativeTrialAsync(sources[0].Candidate.Id,sources[0].Plan.CompilationControl.ResultId,blocks:2);
                Assert.Equal("invalid_plan",(await Assert.ThrowsAsync<LabException>(()=>service.CreateNativeSeriesAsync([sources[0].Plan.Id,sameSession.Id]))).Code);
                Assert.Equal("invalid_plan",(await Assert.ThrowsAsync<LabException>(()=>service.CreateNativeSeriesAsync(sources.Select(s=>s.Plan.Id).ToArray(),independentSessions:true))).Code);
                var tools=new LabTools(service);
                var response=await tools.Experiment(new CreateNativeSeries(sources.Select(s=>s.Plan.Id).ToArray(),IndependentSessions:true,
                    IndependenceJustification:"Distinct disposable process sessions are the declared experimental units; inner repeated blocks remain dependent.",
                    CarryoverJustification:"Fixture logical reset and equal warm-up bracket randomized body order; tiering/PGO history remains explicitly unreset."));
                Assert.True(response.Status=="ok",Wire.Serialize(response)); var series=response.Data!.Value.Deserialize<NativeSeriesPlan>(Wire.Json)!;
                var results=new List<NativeTrialResult>(); foreach(var source in sources) results.Add(await Execute(service,source));
                Assert.Equal("invalid_plan",(await Assert.ThrowsAsync<LabException>(()=>service.CreateNativeSeriesAsync([sources[0].Plan.Id]))).Code);
                Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.RegisterNativeSeriesResultsAsync(series.Id,results.Take(9).Select(r=>r.Id).ToArray()))).Code);
                Assert.Equal("not_comparable",(await Assert.ThrowsAsync<LabException>(()=>service.RegisterNativeSeriesResultsAsync(series.Id,results.AsEnumerable().Reverse().Select(r=>r.Id).ToArray()))).Code);
                var registered=await tools.Run(new RegisterNativeSeriesResults(series.Id,results.Select(r=>r.Id).ToArray(),"register-session-results"));
                Assert.True(registered.Status=="ok",Wire.Serialize(registered)); var registration=registered.Data!.Value.Deserialize<Operation>(Wire.Json)!;
                var registeredDone=await ServiceTests.Wait(service,registration.Id); Assert.True(registeredDone.State==OperationState.Completed,Wire.Serialize(registeredDone));
                var dataset=service.Catalog.Get<NativeSeriesDataset>(registeredDone.ResultId!); datasetId=dataset.Id;
                Assert.Equal(registration.Id,service.StartNativeSeriesRegistration(series.Id,results.Select(r=>r.Id).ToArray(),"register-session-results").Id);
                Assert.Equal(10,dataset.ObservedSessions); Assert.Equal(40,dataset.InputRunCount);
                var accepted=await tools.Run(new StartNativeSeriesComparison(dataset.Id,"session-inference")); Assert.True(accepted.Status=="ok",Wire.Serialize(accepted));
                var operation=accepted.Data!.Value.Deserialize<Operation>(Wire.Json)!;
                var completed=await ServiceTests.Wait(service,operation.Id); Assert.True(completed.State==OperationState.Completed,Wire.Serialize(completed));
                var comparison=await service.ReadNativeSeriesComparisonAsync(completed.ResultId!); comparisonId=comparison.Id;
                Assert.Equal(operation.Id,service.StartNativeSeriesComparison(dataset.Id,"session-inference").Id);
                Assert.Equal(10,comparison.Comparison.IndependentBlocks); Assert.Equal(9,comparison.Comparison.DegreesOfFreedom); Assert.NotNull(comparison.Comparison.DifferenceIntervalRatio);
                Assert.Equal(40,comparison.InputRunCount); Assert.Empty(comparison.Comparison.RunIds); Assert.False(comparison.Comparison.PromotionEligible);
                Assert.Equal(NativeSessionEngine.Version,comparison.Comparison.Estimator); Assert.True(service.Catalog.IsPinned(comparison.ArtifactId));
                var decision=service.Decision(comparison.Comparison.Id,"require_normal_build_confirmation","Native ReJIT evidence does not establish a deployable source result.");
                Assert.Equal(comparison.Comparison.Id,decision.ComparisonId);
                Assert.Equal(comparison.Id,(await service.CompareNativeSeriesAsync(dataset.Id)).Id);
                Assert.Equal("invalid_plan",(await Assert.ThrowsAsync<LabException>(()=>service.CreateNativeSeriesAsync(sources.Select(s=>s.Plan.Id).ToArray()))).Code);
            }
            foreach(var process in processes) { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
            await using var reopened=new LabService(workspace.Root);
            var compared=await new LabTools(reopened).Query(new QueryNativeSeries(comparisonId)); Assert.True(compared.Status=="ok",Wire.Serialize(compared));
            Assert.Equal(comparisonId,compared.Data!.Value.GetProperty("id").GetString());
            var datasetRecord=reopened.Catalog.Get<NativeSeriesDataset>(datasetId); var result=reopened.Catalog.Get<NativeTrialResult>(datasetRecord.TrialResults[0].Id);
            var run=reopened.Catalog.Get<NativeTrialRun>(result.RunIds[0]); reopened.Artifacts.Delete(reopened.Catalog.Get<Artifact>(run.ArtifactId));
            Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>reopened.CompareNativeSeriesAsync(datasetId))).Code);
            Assert.Equal(comparisonId,reopened.Catalog.Get<NativeSeriesComparison>(comparisonId).Id);
            Assert.Equal(comparisonId,(await reopened.ReadNativeSeriesComparisonAsync(comparisonId)).Id);
        }
        finally
        {
            foreach(var process in processes) { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); process.Dispose(); }
        }
    }
    [Fact,Case("UT-STAT-016,UT-EXP-011","FR-032,FR-033,FR-035","M6","Integration")]
    public async Task UndeclaredSingleSession_RemainsDescriptiveAndLateSelectionIsRejected()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var source=await Prepare(service,process,1900);
            var plan=await service.CreateNativeSeriesAsync([source.Plan.Id]);
            var result=await Execute(service,source);
            var dataset=await service.RegisterNativeSeriesResultsAsync(plan.Id,[result.Id]);
            var comparison=await service.CompareNativeSeriesAsync(dataset.Id);
            Assert.Equal(1,comparison.ObservedSessions); Assert.Equal(4,comparison.InputRunCount);
            Assert.Equal("descriptive_only",comparison.Comparison.Quality); Assert.Equal(EffectClass.Inconclusive,comparison.Comparison.EffectClass);
            Assert.Null(comparison.Comparison.DifferenceIntervalRatio); Assert.False(comparison.Comparison.EquivalencePassed||comparison.Comparison.PromotionEligible);
            Assert.Contains("independence_not_established",comparison.Assumptions); Assert.Contains("carryover_adequacy_not_established",comparison.Assumptions);
            Assert.Equal("invalid_plan",(await Assert.ThrowsAsync<LabException>(()=>service.CreateNativeSeriesAsync([source.Plan.Id]))).Code);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
