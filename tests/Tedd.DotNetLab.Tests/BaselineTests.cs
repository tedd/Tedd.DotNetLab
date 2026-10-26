using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class BaselineTests
{
    private static async Task<(ExperimentPlan Plan,ExecutionRecord Execution,string TargetId)> Execute(LabService service,Process target)
    {
        var identity=service.Discover().Single(item=>item.Identity.ProcessId==target.Id).Identity;
        var scenario=(await service.ScenariosAsync(identity.Id,CancellationToken.None)).Scenarios.Single(item=>item.Id=="cpu-sum");
        var plan=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,10,500,1,42,"quiet-validation",true,
            "Declared independent laboratory blocks.",false,30,new()));
        var grant=service.IssueGrant(identity.Id,Permission.ExecuteRegisteredScenario,120,100);
        var completed=await ServiceTests.Wait(service,(await service.StartExecutionAsync(identity.Id,0,plan.Id,grant.Id,Wire.Id("key"),CancellationToken.None)).Id);
        Assert.True(completed.State==OperationState.Completed,completed.ErrorCode);
        return(plan,service.Catalog.Get<ExecutionRecord>(completed.ResultId!),identity.Id);
    }
    [Fact,Case("UT-EXP-001","FR-031,FR-062","M3","Integration")]
    public async Task AliasRevisionChanges_DoNotRedirectFrozenExecutionOrHistoricalEvidence()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            string frozenId,firstId,aliasId,firstHash;
            await using(var service=new LabService(workspace.Root))
            {
                var source=await Execute(service,target); var tools=new LabTools(service);
                var runs=source.Execution.RunIds.Where(id=>service.Catalog.Get<RunRecord>(id).Measurement.Variant==source.Plan.Variants[0]).ToArray();
                var reply=await tools.Experiment(new CreateBaseline(runs,"original_control"));
                Assert.True(reply.Status=="ok",Wire.Serialize(reply)); var first=reply.Data!.Value.Deserialize<Baseline>(Wire.Json)!;
                firstId=first.Id; firstHash=service.Catalog.Get(firstId).Hash;
                Assert.True(service.Catalog.IsPinned(first.ManifestArtifactId));
                var alias=service.SetBaselineAlias("Primary",first.Id); aliasId=alias.Id;
                var frozen=service.Plan(source.Plan,baselineAliasId:alias.Id); frozenId=frozen.Id;
                Assert.Equal(first.Id,frozen.Baseline!.Id); Assert.Equal(alias.Hash,frozen.Baseline.AliasHash); Assert.Equal(1,frozen.Baseline.AliasRevision);
                var replacement=await service.CreateBaselineAsync(runs,"reviewed_control");
                var changed=service.SetBaselineAlias("primary",replacement.Id,alias.Revision); Assert.Equal(2,changed.Revision);
                Assert.Equal("revision_conflict",Assert.Throws<LabException>(()=>service.SetBaselineAlias("primary",first.Id,alias.Revision)).Code);
                Assert.Throws<LabException>(()=>service.Catalog.Put(first.Id,"baseline",first with { Role="changed" },1));
                var grant=service.IssueGrant(source.TargetId,Permission.ExecuteRegisteredScenario,120,20);
                var completed=await ServiceTests.Wait(service,(await service.StartExecutionAsync(source.TargetId,0,frozen.Id,grant.Id,"frozen-alias",CancellationToken.None)).Id);
                Assert.True(completed.State==OperationState.Completed,completed.ErrorCode);
                Assert.Equal(first.Id,service.Catalog.Get<ExperimentPlan>(frozen.Id).Baseline!.Id);
                Assert.Equal(firstHash,service.Catalog.Get(first.Id).Hash);
                Assert.Equal("invalid_baseline",Assert.Throws<LabException>(()=>service.Plan(source.Plan with {
                    Baseline=frozen.Baseline with { Hash=new string('0',64) } })).Code);
                Assert.Equal("invalid_baseline",Assert.Throws<LabException>(()=>service.Plan(source.Plan with {
                    Baseline=frozen.Baseline with { AliasRevision=null } })).Code);
                Assert.Equal("invalid_plan",Assert.Throws<LabException>(()=>service.Plan(source.Plan with { Variants=[] },baselineId:first.Id)).Code);
                Assert.Equal(replacement.Id,service.Plan(source.Plan,baselineAliasId:aliasId).Baseline!.Id);
                Assert.Equal(20,service.Catalog.Get<ExecutionRecord>(completed.ResultId!).RunIds.Count);
                Assert.Equal(2,service.Catalog.Get(aliasId).Revision);
            }
            await using var reopened=new LabService(workspace.Root);
            var retained=reopened.Catalog.Get<ExperimentPlan>(frozenId);
            Assert.Equal(firstId,retained.Baseline!.Id); Assert.Equal(firstHash,retained.Baseline.Hash);
            Assert.Equal(1,retained.Baseline.AliasRevision); Assert.Equal(2,reopened.Catalog.Get(aliasId).Revision);
        }
        finally { if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); } }
    }
    [Fact,Case("UT-ART-008","FR-031,FR-072","M3","Integration")]
    public async Task Baseline_InvalidMixedExpiredOrTamperedEvidence_CannotAuthorizeExecution()
    {
        using var workspace=new TestWorkspace(); using var target=await CaptureTests.StartTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root);
            var source=await Execute(service,target);
            Assert.Equal("baseline_mismatch",(await Assert.ThrowsAsync<LabException>(()=>service.CreateBaselineAsync(source.Execution.RunIds,"mixed"))).Code);
            var id=source.Execution.RunIds.First(run=>service.Catalog.Get<RunRecord>(run).Measurement.Variant=="baseline");
            Assert.Equal("invalid_baseline",(await Assert.ThrowsAsync<LabException>(()=>service.CreateBaselineAsync([id,id],"duplicate"))).Code);
            var baseline=await service.CreateBaselineAsync([id],"control"); var plan=service.Plan(source.Plan,baselineId:baseline.Id);
            Assert.Equal("baseline_mismatch",Assert.Throws<LabException>(()=>service.Plan(source.Plan with { InputHash="changed" },baselineId:baseline.Id)).Code);
            var reference=service.Catalog.Get<JsonElement>(id+"-measurement");
            string artifactId=reference.GetProperty("artifact_id").GetString()!; Assert.True(service.Catalog.IsPinned(artifactId));
            service.Artifacts.Delete(service.Catalog.Get<Artifact>(artifactId));
            var grant=service.IssueGrant(source.TargetId,Permission.ExecuteRegisteredScenario,120,20);
            int before=service.Catalog.List("execution").Rows.Count;
            Assert.Equal("artifact_expired",(await Assert.ThrowsAsync<LabException>(()=>service.StartExecutionAsync(source.TargetId,0,plan.Id,grant.Id,"expired-baseline",CancellationToken.None))).Code);
            Assert.Equal(before,service.Catalog.List("execution").Rows.Count);
        }
        finally { if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); } }
    }
    [Fact,Case("UT-EXP-014","FR-031,FR-032","M3","Integration")]
    public async Task ChangedRuntimeConfiguration_DeniesBaselineBeforeAnyNewMeasurement()
    {
        using var workspace=new TestWorkspace();
        string directory=Path.Combine(workspace.Root,"isolated-target"); Directory.CreateDirectory(directory);
        string original=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0");
        foreach(string file in Directory.GetFiles(original).Where(file=>file.EndsWith(".dll",StringComparison.Ordinal)||file.EndsWith(".json",StringComparison.Ordinal)))
            File.Copy(file,Path.Combine(directory,Path.GetFileName(file)));
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
        start.ArgumentList.Add(Path.Combine(directory,"LabWorkload.dll")); start.ArgumentList.Add("--harness"); start.ArgumentList.Add(workspace.Root);
        using var target=Process.Start(TestWorkspace.PinRuntime(start))!;
        try
        {
            await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await using var service=new LabService(workspace.Root);
            var source=await Execute(service,target);
            var ids=source.Execution.RunIds.Where(id=>service.Catalog.Get<RunRecord>(id).Measurement.Variant=="baseline").ToArray();
            var baseline=await service.CreateBaselineAsync(ids,"control"); var plan=service.Plan(source.Plan,baselineId:baseline.Id);
            await File.AppendAllTextAsync(Path.Combine(directory,"LabWorkload.runtimeconfig.json"),"\n");
            var grant=service.IssueGrant(source.TargetId,Permission.ExecuteRegisteredScenario,120,20);
            int before=service.Catalog.List("execution").Rows.Count;
            Assert.Equal("baseline_mismatch",(await Assert.ThrowsAsync<LabException>(()=>service.StartExecutionAsync(source.TargetId,0,plan.Id,grant.Id,"changed-settings",CancellationToken.None))).Code);
            Assert.Equal(before,service.Catalog.List("execution").Rows.Count);
        }
        finally { if(!target.HasExited) { target.Kill(true); await target.WaitForExitAsync(); } }
    }
}
