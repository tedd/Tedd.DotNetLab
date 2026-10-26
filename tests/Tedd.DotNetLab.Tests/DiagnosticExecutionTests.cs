using System.Text.Json;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Xunit;
using Xunit.Abstractions;

namespace Tedd.DotNetLab.Tests;

public sealed class DiagnosticExecutionTests(ITestOutputHelper output)
{
    [Theory,InlineData("baseline-context"),InlineData("cpu-diagnostic"),InlineData("allocation-diagnostic"),InlineData("runtime-metrics-context"),
        Case("IT-MANAGED-DIAGNOSTIC-EXECUTION","FR-010,FR-011,FR-012,FR-013,FR-024,FR-031,FR-037,FR-070,FR-073","M7","Integration")]
    public async Task PublicDiagnosticExecutionRequiresObservationAndRetainsCaptureBoundMeasurements(string profile)
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        string executionId,summaryHash,rawArtifactId,analysisId,runId;
        try
        {
            var scheduler=new ParserMeasurementGate();
            await using(var service=new LabService(workspace.Root,activityScheduler:scheduler))
            {
                var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                var handshake=await service.ScenariosAsync(identity.Id,CancellationToken.None);
                identity=handshake.Target;
                var scenario=handshake.Scenarios.Single(s=>s.Id=="gc-window");
                var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,2,2048,1,93,profile,false,"",false,60,new()));
                var execute=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,120,4);
                var tools=new LabTools(service); var request=new StartExecution(identity.Id,"0",plan.Id,execute.Id,"diagnostic-execution");
                var denied=await tools.Run(request); Assert.Equal("permission_denied",denied.Code);
                denied=await tools.Run(request with { ObservationGrantId=execute.Id }); Assert.Equal("permission_denied",denied.Code);
                Assert.Empty(service.Catalog.List("operation").Rows); Assert.Empty(service.Catalog.List("capture").Rows);
                var observe=service.IssueGrant(identity.Id,Permission.Observe,120,1); request=request with { ObservationGrantId=observe.Id };
                var accepted=await tools.Run(request); Assert.True(accepted.Status=="ok",Wire.Serialize(accepted));
                var operation=accepted.Data!.Value.Deserialize<Operation>(Wire.Json)!;
                var completed=await ServiceTests.Wait(service,operation.Id); Assert.True(completed.State==OperationState.Completed,Wire.Serialize(completed));
                var replay=await tools.Run(request); Assert.Equal(operation.Id,replay.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
                var execution=service.Catalog.Get<ExecutionRecord>(completed.ResultId!); executionId=execution.Id;
                Assert.Equal(4,execution.RunIds.Count); analysisId=Assert.IsType<string>(execution.AnalysisId); runId=execution.RunIds[1];
                var capture=service.Catalog.Get<Capture>(execution.CaptureId!); Assert.Equal(CaptureState.Sealed,capture.State);
                Assert.Equal(identity,capture.Target); Assert.Equal(Profiles.Resolve(profile).Hash,capture.Profile.Hash);
                var summary=await service.AnalysisAsync(analysisId); summaryHash=Wire.Hash(summary); rawArtifactId=summary.ArtifactId;
                Assert.Equal(capture.Id,summary.CaptureId); Assert.Equal(capture.Profile.Hash,summary.ProfileHash);
                Assert.True(summary.ParserExecution!.ExitVerified); Assert.NotEqual(Environment.ProcessId,summary.ParserExecution.ProcessId);
                foreach(string id in execution.RunIds)
                {
                    var run=service.Catalog.Get<RunRecord>(id);
                    Assert.Equal(capture.Profile.Hash,run.ProfileHash); Assert.Equal(identity,run.Target);
                    Assert.Equal(Wire.Hash(plan),run.PlanHash); Assert.Equal(2048,run.Measurement.CompletedOperations);
                    Assert.True(run.Measurement.Correct&&run.Measurement.Quiescent);
                    foreach(string phase in new[]{"measurement_start","measurement_end"})
                        Assert.Contains(summary.Markers,m=>m.RunId==id&&m.ExecutionId==execution.Id&&m.Phase==phase);
                }
                if(profile!="runtime-metrics-context") Assert.NotEmpty(summary.GcCycles);
                if(profile=="allocation-diagnostic") Assert.NotEmpty(summary.Allocations);
                if(profile=="cpu-diagnostic") Assert.True(summary.ManagedSampleCount>0);
                Assert.All(service.Compare(execution.Id),comparison=>Assert.False(comparison.PromotionEligible));
                Assert.False(scheduler.State.ParserActive); Assert.Equal(0,scheduler.State.ActiveMeasurements);
                await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(summary.ArtifactId));
            }
            target.Kill(true); await target.WaitForExitAsync();
            await using var reopened=new LabService(workspace.Root);
            Assert.Equal(analysisId,reopened.Catalog.Get<ExecutionRecord>(executionId).AnalysisId);
            Assert.Equal(summaryHash,Wire.Hash(await reopened.AnalysisAsync(analysisId)));
            var window=await reopened.RunWindowAsync(analysisId,runId);
            if(window.ClockFitFailure is { Failure:"excessive_residual" } rejected)
            {
                output.WriteLine(Wire.Serialize(window));
                Assert.Equal(Availability.Invalid,window.Availability); Assert.Null(window.ClockMapping);
                Assert.True(rejected.MaximumResidualMilliseconds>window.ResidualToleranceMilliseconds);
                Assert.Equal("clock_mapping_invalid",window.GcSuspensionOverlap.Reason); Assert.Null(window.GcSuspensionOverlap.Value);
            }
            else
            {
                Assert.True(window.BoundarySource=="target_measurement_markers",Wire.Serialize(window)); Assert.NotNull(window.ClockMapping);
            }
            await reopened.Artifacts.VerifiedPathAsync(reopened.Catalog.Get<Artifact>(rawArtifactId));
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
}
