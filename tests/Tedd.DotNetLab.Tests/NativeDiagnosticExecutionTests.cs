using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class NativeDiagnosticExecutionTests
{
    [Theory,InlineData("baseline-context"),InlineData("cpu-diagnostic"),InlineData("allocation-diagnostic"),InlineData("runtime-metrics-context"),
        Case("IT-NATIVE-DIAGNOSTIC-EXECUTION","FR-010,FR-011,FR-012,FR-013,FR-031,FR-032,FR-052,FR-053,FR-070,FR-073","M7","Integration")]
    public async Task CapturedNativeExecutionPreservesVerifiedRegimesAndRequiresCandidateCompilationControl(string profile)
    {
        using var workspace=new TestWorkspace(); using var target=await ProfilerTests.StartNativeTarget(workspace.Root);
        string executionId,analysisId,summaryHash;
        try
        {
            await using(var service=new LabService(workspace.Root))
            {
                var prepared=await CompilationControlTests.Prepare(service,target.Id);
                var handshake=await service.ScenariosAsync(prepared.Target.Id,CancellationToken.None);
                var gc=handshake.Scenarios.Single(s=>s.Id=="gc-window");
                var originalPlan=service.Plan(new("",gc.Id,gc.Version,gc.InputHash,gc.Variants,2,2048,1,94,profile,false,"",false,60,new()));
                var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,180,32);
                var observe=service.IssueGrant(prepared.Target.Id,Permission.Observe,180,2);
                var tools=new LabTools(service);
                var original=await Run(tools,service,new(prepared.Target.Id,"0",originalPlan.Id,execute.Id,"original-diagnostic",observe.Id));
                await VerifyCapture(service,original,profile,"original_normal_compile",null,null);
                var originalSummary=await service.AnalysisAsync(original.AnalysisId!);
                if(profile!="runtime-metrics-context") Assert.NotEmpty(originalSummary.GcCycles);
                if(profile=="allocation-diagnostic") Assert.NotEmpty(originalSummary.Allocations);
                if(profile=="cpu-diagnostic") Assert.True(originalSummary.ManagedSampleCount>0);

                var control=await CompilationAdmissionTests.Control(service,prepared.Target,prepared.Candidate);
                var candidate=await CompilationAdmissionTests.Candidate(service,prepared.InspectionId,prepared.Candidate.OriginalIlHash);
                var mutate=service.IssueMutationGrant(prepared.Target.Id,candidate.Id,180,8);
                var applied=await ServiceTests.Wait(service,(await service.ApplyCilAsync(prepared.Target.Id,control.RestoredEpoch,candidate.Id,
                    mutate.Id,execute.Id,"native-value","apply-diagnostic")).Id);
                Assert.True(applied.State==OperationState.Completed,Wire.Serialize(applied));
                var outcome=service.Catalog.Get<MutationOutcome>(applied.ResultId!);
                var root=service.Catalog.List("mutation").Rows.Select(row=>Wire.Deserialize<MutationRoot>(row.Json)).Single(item=>item.OperationId==applied.Id);
                var value=(await service.ScenariosAsync(prepared.Target.Id,CancellationToken.None)).Scenarios.Single(s=>s.Id=="native-value");
                var draft=new ExperimentPlan("",value.Id,value.Version,value.InputHash,value.Variants,2,1000,1,95,profile,false,"",false,60,new());
                var unqualified=service.Plan(draft);
                var denied=await tools.Run(new StartExecution(prepared.Target.Id,outcome.Epoch.ToString(),unqualified.Id,execute.Id,"missing-control",observe.Id));
                Assert.Equal("compilation_control_required",denied.Code); Assert.Single(service.Catalog.List("execution").Rows);
                var qualified=service.Plan(draft,compilationControlId:control.Id);
                var execution=await Run(tools,service,new(prepared.Target.Id,outcome.Epoch.ToString(),qualified.Id,execute.Id,"candidate-diagnostic",observe.Id));
                await VerifyCapture(service,execution,profile,"candidate_rejit",candidate.BodyHash,control.Id);
                Assert.All(service.Compare(execution.Id),comparison=>Assert.False(comparison.PromotionEligible));
                executionId=execution.Id; analysisId=execution.AnalysisId!; summaryHash=Wire.Hash(await service.AnalysisAsync(analysisId));
                var restored=await ServiceTests.Wait(service,(await service.RestoreCilAsync(root.Id,outcome.Epoch,mutate.Id,execute.Id,"diagnostic-restore")).Id);
                Assert.True(restored.State==OperationState.Completed,Wire.Serialize(restored));
                Assert.True(service.Catalog.Get<MutationOutcome>(restored.ResultId!).Restored);
            }
            target.Kill(true); await target.WaitForExitAsync();
            await using var reopened=new LabService(workspace.Root);
            var retained=reopened.Catalog.Get<ExecutionRecord>(executionId);
            Assert.Equal(analysisId,retained.AnalysisId); Assert.Equal(summaryHash,Wire.Hash(await reopened.AnalysisAsync(analysisId)));
            Assert.All(retained.RunIds.Select(reopened.Catalog.Get<RunRecord>),run=>Assert.Equal("candidate_rejit",run.Compilation!.Regime));
            var window=await reopened.RunWindowAsync(analysisId,retained.RunIds[1]);
            Assert.Equal("target_measurement_markers",window.BoundarySource); Assert.NotNull(window.ClockMapping);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
    private static async Task<ExecutionRecord> Run(LabTools tools,LabService service,StartExecution request)
    {
        var reply=await tools.Run(request); Assert.True(reply.Status=="ok",Wire.Serialize(reply));
        var complete=await ServiceTests.Wait(service,reply.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
        Assert.True(complete.State==OperationState.Completed,Wire.Serialize(complete));
        return service.Catalog.Get<ExecutionRecord>(complete.ResultId!);
    }
    private static async Task VerifyCapture(LabService service,ExecutionRecord execution,string profile,string regime,string? bodyHash,string? controlId)
    {
        var capture=service.Catalog.Get<Capture>(execution.CaptureId!); Assert.Equal(CaptureState.Sealed,capture.State);
        Assert.Equal(execution.Target,capture.Target); Assert.Equal(Profiles.Resolve(profile).Hash,capture.Profile.Hash);
        var summary=await service.AnalysisAsync(execution.AnalysisId!);
        Assert.Equal(capture.Id,summary.CaptureId); Assert.Equal(capture.Profile.Hash,summary.ProfileHash); Assert.True(summary.ParserExecution!.ExitVerified);
        await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(summary.ArtifactId));
        Assert.Equal(4,execution.RunIds.Count);
        foreach(var run in execution.RunIds.Select(service.Catalog.Get<RunRecord>))
        {
            Assert.Equal(regime,run.Compilation!.Regime); Assert.Equal(controlId,run.Compilation.CompilationControlId);
            Assert.True(run.Runtime!.NativeProfilerPresent); Assert.Equal("1",run.Runtime.ProfilingEnabled);
            Assert.Equal(capture.Profile.Hash,run.ProfileHash); Assert.Equal(capture.Target,run.Target);
            Assert.True(run.Measurement.Correct&&run.Measurement.Quiescent);
            if(bodyHash is not null) { Assert.Equal(bodyHash,run.Compilation.BodyHash); Assert.Equal(3,run.Compilation.RequestFlags); }
            foreach(string phase in new[]{"measurement_start","measurement_end"}) Assert.Contains(summary.Markers,m=>m.RunId==run.Id&&m.Phase==phase);
        }
    }
}
