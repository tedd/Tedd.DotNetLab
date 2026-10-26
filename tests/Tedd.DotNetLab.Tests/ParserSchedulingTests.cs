using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ParserSchedulingTests
{
    private static readonly TargetIdentity Target=new("target",1,2,"runtime","10.0.12","x64");
    [Fact,Case("UT-CAP-022","FR-004,FR-015,FR-073","M1","Unit")]
    public async Task MeasurementsAndParsersAreExclusiveInQueueOrder()
    {
        var gate=new ParserMeasurementGate(); var first=await gate.EnterMeasurementAsync(); var second=gate.EnterMeasurementAsync();
        Assert.Equal(1,gate.State.ActiveMeasurements); var parser=gate.EnterParserAsync(); var later=gate.EnterMeasurementAsync();
        Assert.False(second.IsCompleted); Assert.False(parser.IsCompleted); Assert.False(later.IsCompleted);
        first.Dispose(); using(var active=await second) { Assert.Equal(1,gate.State.ActiveMeasurements); Assert.False(parser.IsCompleted); }
        using(var active=await parser) { Assert.True(gate.State.ParserActive); Assert.False(later.IsCompleted); }
        using var last=await later; Assert.False(gate.State.ParserActive); Assert.Equal(1,gate.State.ActiveMeasurements);
    }
    [Fact,Case("UT-PARSER-SCHEDULE-001","FR-004,FR-038","M1","Unit")]
    public async Task CancelledWaiterCannotReleaseAnActivePermitOrBlockItsSuccessor()
    {
        var gate=new ParserMeasurementGate(); var measurement=await gate.EnterMeasurementAsync(); using var cancel=new CancellationTokenSource();
        var cancelled=gate.EnterParserAsync(cancel.Token); var successor=gate.EnterParserAsync(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>cancelled); Assert.Equal(1,gate.State.ActiveMeasurements); Assert.False(successor.IsCompleted);
        measurement.Dispose(); using var acquired=await successor; Assert.True(gate.State.ParserActive); Assert.Equal(0,gate.State.Queued);
    }
    [Fact,Case("UT-PARSER-SCHEDULE-002","FR-004,FR-053","M1","Unit")]
    public async Task UnknownQuiescenceRemainsExcludedUntilMatchingProof()
    {
        var gate=new ParserMeasurementGate(_=>false); var measurement=await gate.EnterMeasurementAsync(); measurement.RetainForUncertainTarget(Target); measurement.Dispose();
        var parser=gate.EnterParserAsync(); Assert.False(parser.IsCompleted); Assert.Equal(1,gate.State.UncertainMeasurements);
        gate.AcknowledgeQuiescence(Target with { StartTimeUtcTicks=3 }); Assert.False(parser.IsCompleted);
        gate.AcknowledgeQuiescence(Target with { Epoch=9 }); using var acquired=await parser.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0,gate.State.UncertainMeasurements); Assert.Equal(0,gate.State.ActiveMeasurements);
    }
    [Fact,Case("UT-PARSER-SCHEDULE-003","FR-002,FR-004","M1","Unit")]
    public async Task VerifiedTargetExitReleasesUnknownMeasurementWithoutInventingHealth()
    {
        bool exited=false; var gate=new ParserMeasurementGate(_=>exited); var measurement=await gate.EnterMeasurementAsync();
        measurement.RetainForUncertainTarget(Target); measurement.Dispose(); var parser=gate.EnterParserAsync(); Assert.False(parser.IsCompleted);
        exited=true; using var acquired=await parser.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal(0,gate.State.UncertainMeasurements);
    }
    [Fact,Case("UT-PARSER-SCHEDULE-004","FR-004,FR-038","M1","Unit")]
    public async Task ClosingCancelsQueuedWorkAndDoesNotReleaseAnActiveMeasurement()
    {
        var gate=new ParserMeasurementGate(); var measurement=await gate.EnterMeasurementAsync(); var queued=gate.EnterParserAsync(); gate.Close();
        Assert.Equal("controller_stopping",(await Assert.ThrowsAsync<LabException>(()=>queued)).Code);
        Assert.Equal(1,gate.State.ActiveMeasurements); Assert.Equal("controller_stopping",(await Assert.ThrowsAsync<LabException>(()=>gate.EnterParserAsync())).Code);
        measurement.Dispose(); measurement.Dispose(); Assert.Equal(0,gate.State.ActiveMeasurements);
    }
    private static async Task WaitQueued(ParserMeasurementGate gate)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(gate.State.Queued==0) await Task.Delay(10,timeout.Token);
    }
    [Fact,Case("IT-MACHINE-MUTEX-003","FR-004,FR-015,FR-070","M1","Integration")]
    public async Task CaptureGrantIsRevalidatedAfterWaitingForMeasurementAdmission()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        var gate=new ParserMeasurementGate();
        try
        {
            await using var service=new LabService(workspace.Root,activityScheduler:gate);
            var identity=service.Discover().Single(item=>item.Identity.ProcessId==target.Id).Identity;
            var grant=service.IssueGrant(identity.Id,Permission.Observe,1,1);
            using var active=await gate.EnterMeasurementAsync();
            var capture=service.StartCapture(identity.Id,0,"baseline-context",1,grant.Id,"queued-capture");
            await WaitQueued(gate);
            var remaining=grant.ExpiresAt-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(50);
            if(remaining>TimeSpan.Zero) await Task.Delay(remaining);
            active.Dispose();
            var failed=await ServiceTests.Wait(service,capture.Id);
            Assert.Equal(OperationState.Failed,failed.State); Assert.Equal("permission_denied",failed.ErrorCode);
            Assert.Empty(service.Catalog.List("capture").Rows);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
    [Fact,Case("IT-PARSER-SCHEDULE-001","FR-004,FR-015,FR-037","M2","Integration")]
    public async Task RealHarnessExecutionWaitsForAnActiveParserPermit()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root); var gate=new ParserMeasurementGate();
        try
        {
            await using var service=new LabService(workspace.Root,activityScheduler:gate);
            var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var scenario=(await service.ScenariosAsync(identity.Id,CancellationToken.None)).Scenarios.Single(s=>s.Id=="cpu-sum");
            var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,1,10,0,1,"quiet-validation",false,"descriptive",false,30,new()));
            var grant=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,60,2); var parsing=await gate.EnterParserAsync();
            var accepted=await service.StartExecutionAsync(identity.Id,0,plan.Id,grant.Id,"scheduled-measurement",CancellationToken.None);
            await WaitQueued(gate); Assert.Empty(service.Catalog.List("run").Rows); Assert.True(gate.State.ParserActive);
            parsing.Dispose(); var done=await ServiceTests.Wait(service,accepted.Id); Assert.Equal(OperationState.Completed,done.State);
            Assert.Equal(2,service.Catalog.List("run").Rows.Count); Assert.Equal(0,gate.State.ActiveMeasurements);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
    [Fact,Case("IT-PARSER-SCHEDULE-002","FR-004,FR-015,FR-026,FR-038","M1","Integration")]
    public async Task RetainedTraceJobDoesNotStartWorkerDuringMeasurementPermit()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(); var gate=new ParserMeasurementGate(); int starts=0;
        try
        {
            await using var service=new LabService(workspace.Root,traceParser:new TraceWorkerClient(workerStarted:(_,_)=>Interlocked.Increment(ref starts)),activityScheduler:gate);
            var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity; var grant=service.IssueGrant(identity.Id,Permission.Observe,60,1);
            var capture=await ServiceTests.Wait(service,service.StartCapture(identity.Id,0,"baseline-context",1,grant.Id,"source").Id); Assert.Equal(OperationState.Completed,capture.State);
            starts=0; var measurement=await gate.EnterMeasurementAsync(); var accepted=service.StartTraceAnalysis(capture.ResultId!,"deferred-analysis");
            await WaitQueued(gate); Assert.Equal(0,starts); Assert.Equal(OperationState.Running,service.Status(accepted.Id).State);
            measurement.Dispose(); var done=await ServiceTests.Wait(service,accepted.Id); Assert.Equal(OperationState.Completed,done.State); Assert.Equal(1,starts);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
}
