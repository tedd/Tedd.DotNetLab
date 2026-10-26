using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ControllerResourceTests
{
    [Fact,Case("UT-RESOURCE-001","FR-073,FR-020,FR-074","M7","Integration")]
    public async Task ExplicitSnapshotIdentifiesControllerAndSeparatesClockCpuAndMemoryKinds()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root);
        var response=await new LabTools(service).Query(new QueryControllerResources()); Assert.Equal("ok",response.Status);
        var snapshot=response.Data!.Value.Deserialize<ControllerResourceSnapshot>(Wire.Json)!;
        Assert.Equal(Environment.ProcessId,snapshot.ProcessId); Assert.Equal("controller_process",snapshot.Scope);
        Assert.Equal("controller_idle_query",snapshot.Placement); Assert.Equal(Stopwatch.Frequency,snapshot.Frequency);
        Assert.Equal(TimeSpan.TicksPerSecond,snapshot.CpuFrequency); Assert.True(snapshot.WorkingSetBytes>0&&snapshot.PrivateMemoryBytes>0);
        Assert.True(snapshot.ManagedHeapBytes>=0&&snapshot.ApproximateTotalAllocatedBytes>=0&&snapshot.ThreadCount>0);
        Assert.Null(snapshot.OperationId); Assert.Contains("process_start_utc_ticks\":\"",Wire.Serialize(snapshot));
    }
    [Fact,Case("UT-RESOURCE-002","FR-073,FR-037,FR-043","M7","Integration")]
    public async Task OperationFootprintBracketsHarnessTimerAndRemainsAvailableAfterRestart()
    {
        using var workspace=new TestWorkspace(); using var process=await CaptureTests.StartTarget(workspace.Root); string operationId;
        try
        {
            await using(var service=new LabService(workspace.Root))
            {
                var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
                var scenario=(await service.ScenariosAsync(target.Id,CancellationToken.None)).Scenarios.Single(s=>s.Id=="cpu-sum");
                var plan=service.CreateIndividualLatencyPlan(scenario.Id,scenario.Version,scenario.InputHash,"baseline",20);
                var grant=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,60,1);
                var complete=await ServiceTests.Wait(service,(await service.StartIndividualLatenciesAsync(target.Id,0,plan.Id,grant.Id,"resource-run",CancellationToken.None)).Id);
                Assert.Equal(OperationState.Completed,complete.State); operationId=complete.Id;
                var evidence=await service.ControllerResourcesAsync(operationId); var run=service.Catalog.Get<IndividualLatencyRun>(complete.ResultId!);
                Assert.Equal(Availability.Available,evidence.Availability); Assert.Equal(Environment.ProcessId,evidence.Before!.ProcessId); Assert.NotEqual(process.Id,evidence.Before.ProcessId);
                Assert.True(evidence.Before.TimestampTicks<run.Measurement.StartTicks); Assert.True(evidence.After!.TimestampTicks>run.Measurement.EndTicks);
                Assert.Equal("outside_own_target_timing",evidence.Before.Placement); Assert.Equal("outside_own_target_timing",evidence.After.Placement);
                Assert.True(evidence.CpuDeltaTicks>=0&&evidence.ApproximateAllocatedDeltaBytes>=0&&evidence.IntervalTicks>0);
                Assert.Equal(operationId,evidence.Before.OperationId); Assert.Empty(evidence.Warnings);
            }
            await using var reopened=new LabService(workspace.Root);
            var response=await new LabTools(reopened).Query(new QueryControllerResources(operationId)); Assert.Equal("ok",response.Status);
            var retained=response.Data!.Value.Deserialize<ControllerResourceEvidence>(Wire.Json)!; Assert.Equal(Availability.Available,retained.Availability); Assert.NotNull(retained.After);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("UT-RESOURCE-003","FR-073,FR-021","M7","Integration")]
    public async Task DisabledCollectionReportsMissingCountersRatherThanInventingZero()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root,collectControllerResources:false);
        var hypothesis=service.Hypothesis("test","test",[]); var plan=await service.PrepareExportAsync([hypothesis.Id],[],true,1048576,60);
        var grant=service.IssueExportGrant(plan.Id,60,1); var completed=await ServiceTests.Wait(service,service.StartExport(plan.Id,grant.Id,"no-resource-probe").Id);
        Assert.Equal(OperationState.Completed,completed.State); var evidence=await service.ControllerResourcesAsync(completed.Id);
        Assert.Equal(Availability.NotCollected,evidence.Availability); Assert.Null(evidence.Before); Assert.Null(evidence.After); Assert.Null(evidence.CpuDeltaTicks);
        Assert.Null(evidence.ApproximateAllocatedDeltaBytes); Assert.Empty(service.Catalog.List("controller_resource_snapshot").Rows);
    }
    [Fact,Case("UT-RESOURCE-004","FR-073,FR-053","M7","Integration")]
    public async Task UncertainTargetQuiescenceSkipsAfterSnapshotAndPreservesTaint()
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root,mutationJournalCommitted:item=>
            {
                if(item.Phase=="control_original_body_rejit_completed"&&!process.HasExited) process.Kill(true);
            });
            var source=await CompilationControlTests.Prepare(service,process.Id); var plan=await service.CreateCompilationControlAsync(source.Candidate.Id,"native-value",blocks:1);
            var mutation=service.IssueMutationGrant(source.Target.Id,source.Candidate.Id,120,1); var execute=service.IssueGrant(source.Target.Id,Permission.ExecuteRegisteredScenario,120,4);
            var inspect=service.IssueGrant(source.Target.Id,Permission.InspectLiveCode,120,12);
            var complete=await ServiceTests.Wait(service,(await service.StartCompilationControlAsync(plan.Id,mutation.Id,execute.Id,inspect.Id,"resource-taint")).Id);
            Assert.Equal(OperationState.Tainted,complete.State); var evidence=await service.ControllerResourcesAsync(complete.Id);
            Assert.Equal(Availability.NotCollected,evidence.Availability); Assert.NotNull(evidence.Before); Assert.Null(evidence.After); Assert.Null(evidence.CpuDeltaTicks);
            Assert.Equal("controller_resource_unavailable",service.Catalog.Get(complete.Id+"-controller-after").Kind);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
    [Fact,Case("UT-RESOURCE-005","FR-073,FR-013","M7","Integration")]
    public async Task ExplicitSamplingDuringAnOperationLabelsPossibleMeasurementOverlap()
    {
        using var workspace=new TestWorkspace(); using var process=await CaptureTests.StartTarget();
        try
        {
            await using var service=new LabService(workspace.Root); var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            var grant=service.IssueGrant(target.Id,Permission.Observe,60,1);
            var operation=service.StartCapture(target.Id,0,"baseline-context",2,grant.Id,"overlapping-controller-query");
            var sample=service.ControllerResources(); Assert.True(sample.OtherActiveOperations>0);
            Assert.Equal("explicit_query_target_timing_may_overlap",sample.Placement);
            Assert.Equal(OperationState.Completed,(await ServiceTests.Wait(service,operation.Id)).State);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
