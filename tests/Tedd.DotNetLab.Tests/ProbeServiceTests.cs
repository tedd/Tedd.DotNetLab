using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ProbeServiceTests
{
    [Theory,InlineData(false,"Value",1,false),InlineData(true,"Value",1,false),InlineData(false,"Recursive",6,false),InlineData(true,"Recursive",6,false),
        InlineData(false,"Loop",1,false),InlineData(true,"Loop",1,false),InlineData(false,"Value",1,true),InlineData(true,"Value",1,true),
        InlineData(false,"Recursive",6,true),InlineData(true,"Recursive",6,true),InlineData(false,"Loop",1,true),InlineData(true,"Loop",1,true),
        InlineData(false,"Divide",1,true),InlineData(true,"Divide",1,true)]
    [Case("IT-PROBE-MCP", "FR-050,FR-051,FR-053,FR-060", "M5", "Integration")]
    public async Task PublicProbePlan_PreservesEvidenceAndRestores(bool timing,string methodName,int multiplier,bool trackExceptions)
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var tools=new LabTools(service);
            var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            await service.ProfilerAsync(target.Id,CancellationToken.None);
            var observe=service.IssueGrant(target.Id,Permission.InspectLiveCode,90,3);
            var image=await service.ImportAsync(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"),
                "application/vnd.microsoft.portable-executable");
            string scenarioId="native-"+methodName.ToLowerInvariant();
            var method=Assert.Single((await service.MethodsAsync(image.Id,"NativeFixture."+methodName,100,null)).Rows);
            var inspection=await ServiceTests.Wait(service,(await service.InspectLiveAsync(target.Id,method.Id,0,observe.Id,null,CancellationToken.None)).Id);
            var record=service.Catalog.Get<JsonElement>(inspection.ResultId!);
            var artifact=service.Catalog.Get<Artifact>(record.GetProperty("manifest_artifact_id").GetString()!);
            var source=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(artifact)));
            if(methodName=="Divide")
            {
                var denied=await tools.Patch(new PrepareProbe(inspection.ResultId!,source.OriginalBodyHash,timing));
                Assert.Equal("capability_unsupported",denied.Code);
            }
            var prepared=await tools.Patch(new PrepareProbe(inspection.ResultId!,source.OriginalBodyHash,timing,TrackExceptions:trackExceptions));
            Assert.Equal("ok",prepared.Status); var plan=prepared.Data!.Value.Deserialize<CilCandidateRecord>(Wire.Json)!;
            if(trackExceptions)
            {
                Assert.NotNull(plan.Probe!.Fault);
                Assert.Equal("invalid_cil",(await Assert.ThrowsAsync<LabException>(()=>service.RegisterCilAsync(plan.BodyArtifactId,
                    inspection.ResultId!,source.OriginalBodyHash,[]))).Code);
            }
            var permission=service.IssueMutationGrant(target.Id,plan.Id,90,10);
            Assert.Equal(Permission.InstallProbes,permission.Permission);
            var execute=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,90,10);
            var apply=await tools.Patch(new ApplyCil(target.Id,"0",plan.Id,permission.Id,execute.Id,scenarioId,"install-probe"));
            Assert.Equal("ok",apply.Status); var operation=apply.Data!.Value.Deserialize<Operation>(Wire.Json)!;
            operation=await ServiceTests.Wait(service,operation.Id);
            Assert.True(operation.State==OperationState.Completed,operation.ErrorCode);
            var outcome=service.Catalog.Get<MutationOutcome>(operation.ResultId!); Assert.True(outcome.Measurable);
            var current=target with { Epoch=outcome.Epoch };
            var run=new Tedd.DotNetLab.Contracts.RunRequest("probe-exec","probe-run","probe-block",scenarioId,"candidate",500,0,true,false,
                current.Epoch,current,execute,DateTimeOffset.UtcNow.AddSeconds(30),plan.Scope);
            Assert.True((await new HarnessClient().ExecuteAsync(run,CancellationToken.None)).Correct);
            var flush=await tools.Patch(new FlushProbe(target.Id,current.Epoch.ToString(),plan.Id,permission.Id));
            Assert.True(flush.Status=="ok",flush.Code+": "+flush.Message); Assert.Equal((500*multiplier).ToString(),flush.Data!.Value.GetProperty("entered").GetString());
            Assert.True(flush.Data.Value.GetProperty("complete").GetBoolean());
            var evidence=service.Catalog.Get<JsonElement>(flush.Data.Value.GetProperty("evidence_id").GetString()!);
            Assert.Equal(timing?"inclusive_elapsed":"count",evidence.GetProperty("mode").GetString());
            var raw=service.Catalog.Get<Artifact>(evidence.GetProperty("artifact_id").GetString()!);
            Assert.True(service.Catalog.IsPinned(raw.Id)); await service.Artifacts.VerifyAsync(raw.Hash,raw.Length);
            var snapshot=Wire.Deserialize<Tedd.DotNetLab.Probes.ProbeSnapshot>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(raw)));
            Assert.Equal(methodName=="Divide"?250:500*multiplier,snapshot.Rows.Sum(r=>r.NormallyCompleted));
            Assert.Equal(methodName=="Divide"?250:0,snapshot.Rows.Sum(r=>r.ExceptionallyCompleted));
            Assert.Equal(trackExceptions?"normal-exit-and-managed-fault-unwind":"normal-completion/synchronous-int32",
                evidence.GetProperty("exception_coverage").GetString());
            Assert.All(snapshot.Rows,r=>
            {
                Assert.Equal(0,r.InFlight); Assert.Equal(0,r.Overflow); Assert.Equal(0,r.Dropped);
                if(timing) { Assert.True(r.NormalElapsedTicks>=0); Assert.True(r.ExceptionalElapsedTicks>=0); }
                else { Assert.Null(r.NormalElapsedTicks); Assert.Null(r.ExceptionalElapsedTicks); }
            });
            var handshake=await service.ScenariosAsync(target.Id,CancellationToken.None);
            var scenario=handshake.Scenarios.Single(s=>s.Id==scenarioId);
            var quiet=service.Plan(new("",scenario.Id,scenario.Version,scenario.InputHash,scenario.Variants,1,10,0,42,
                "quiet-validation",false,"",false,30,new()));
            Assert.Equal("observer_mismatch",(await Assert.ThrowsAsync<LabException>(()=>service.StartExecutionAsync(target.Id,current.Epoch,
                quiet.Id,execute.Id,"quiet-with-probes",CancellationToken.None))).Code);
            var diagnostic=service.Plan(quiet with { Id="",Profile="targeted-probes" });
            var measured=await ServiceTests.Wait(service,(await service.StartExecutionAsync(target.Id,current.Epoch,diagnostic.Id,execute.Id,
                "targeted-execution",CancellationToken.None)).Id);
            Assert.Equal(OperationState.Completed,measured.State);
            var session=service.Catalog.Get<ExecutionRecord>(measured.ResultId!);
            Assert.All(session.RunIds,id=>Assert.Contains(plan.ContentHash,service.Catalog.Get<RunRecord>(id).ObserverState));
            var second=await tools.Patch(new FlushProbe(target.Id,current.Epoch.ToString(),plan.Id,permission.Id));
            Assert.Equal("ok",second.Status); Assert.Equal((20*multiplier).ToString(),second.Data!.Value.GetProperty("entered").GetString());
            var root=service.Catalog.List("mutation").Rows.Select(r=>Wire.Deserialize<MutationRoot>(r.Json)).Single(r=>r.OperationId==operation.Id);
            var restore=await tools.Patch(new RestoreCil(root.Id,current.Epoch.ToString(),permission.Id,execute.Id,"restore-probe"));
            Assert.Equal("ok",restore.Status); var restored=await ServiceTests.Wait(service,restore.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
            Assert.Equal(OperationState.Completed,restored.State); Assert.True(service.Catalog.Get<MutationOutcome>(restored.ResultId!).Restored);
            Assert.False((await service.ScenariosAsync(target.Id,CancellationToken.None)).ProbeHelper!.Active);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
