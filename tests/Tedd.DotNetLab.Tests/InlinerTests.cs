using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class InlinerTests
{
    [Theory,InlineData(false),InlineData(true)]
    [Case("IT-PROBE-003", "FR-050,FR-052,FR-053,FR-070", "M5", "Integration")]
    public async Task ExistingInliner_RecompilesAndRestoresWithPerCallerEvidence(bool incorrect)
    {
        using var workspace=new TestWorkspace(); using var process=await ProfilerTests.StartNativeTarget(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root);
            var target=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            await service.ProfilerAsync(target.Id,CancellationToken.None);
            foreach(string capability in new[]{"rejit","restore","inliner_handling","timing_probes"})
                Assert.Equal(CapabilityState.Supported,service.Target(target.Id).Capabilities[capability].State);
            var observe=service.IssueGrant(target.Id,Permission.InspectLiveCode,180,9);
            var image=await service.ImportAsync(Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll"),
                "application/vnd.microsoft.portable-executable");
            var leaf=Assert.Single((await service.MethodsAsync(image.Id,"NativeFixture.InlineLeaf",100,null)).Rows);
            var caller=Assert.Single((await service.MethodsAsync(image.Id,"NativeFixture.InlineCaller",100,null)).Rows);
            async Task<(Operation,NativeSnapshotReply)> Inspect(StoredMethod method,long epoch)
            {
                var operation=await ServiceTests.Wait(service,(await service.InspectLiveAsync(target.Id,method.Id,epoch,observe.Id,null,CancellationToken.None)).Id);
                Assert.True(operation.State==OperationState.Completed,operation.ErrorCode);
                var row=service.Catalog.Get<JsonElement>(operation.ResultId!);
                var artifact=service.Catalog.Get<Artifact>(row.GetProperty("manifest_artifact_id").GetString()!);
                return(operation,Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(artifact))));
            }
            var before=await Inspect(caller,0);
            Assert.All(before.Item2.Snapshots,s=>Assert.DoesNotContain(NativeDecoder.Decode(s).Instructions,i=>i.FlowControl=="Call"));
            var acquisition=await Inspect(leaf,0);
            var binding=Assert.Single(acquisition.Item2.Inliners!); Assert.Equal(caller.Token,binding.MethodToken);
            CilCandidateRecord candidate;
            if(incorrect)
            {
                string path=Path.Combine(workspace.Root,"incorrect.json");
                await File.WriteAllTextAsync(path,Wire.Serialize(new CilInput(Wire.Version,[0x0a,0x16,0x2a],"incorrect constant fixture")));
                candidate=await service.RegisterCilAsync((await service.ImportAsync(path,"application/json")).Id,acquisition.Item1.ResultId!,acquisition.Item2.OriginalBodyHash,[]);
            }
            else candidate=await service.RegisterProbeAsync(acquisition.Item1.ResultId!,acquisition.Item2.OriginalBodyHash,false);
            Assert.Single(candidate.Inliners!);
            var permission=service.IssueMutationGrant(target.Id,candidate.Id,180,10);
            var execute=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,180,10);
            var invalidSetup=new Tedd.DotNetLab.Contracts.RunRequest("scope-check","setup","block","native-inliner","candidate",1,0,true,false,
                0,target,execute,DateTimeOffset.UtcNow.AddSeconds(30),candidate.Scope,["foreign/caller/scope"]);
            Assert.Equal("scenario_scope_mismatch",(await Assert.ThrowsAsync<LabException>(()=>new HarnessClient().SetupAsync(invalidSetup,CancellationToken.None))).Code);
            if(incorrect)
            {
                var input=Wire.Deserialize<CilInput>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(candidate.BodyArtifactId))));
                var omitted=permission with {Id="omitted-caller",ContentHash=candidate.BodyHash};
                object Preflight(Grant grant,IReadOnlyList<NativeInlinerBinding> inliners)=>new { schema_version=Wire.Version,action="preflight",
                    target_id=target.Id,process_id=process.Id,start_time_utc_ticks=target.StartTimeUtcTicks,expected_target_epoch=0L,grant,
                    transaction_id="caller-preflight",body=input.Body,base_il_hash=candidate.BaseIlHash,inliners,
                    selector=new {module_mvid=leaf.ModuleMvid,method_token=leaf.Token,module_hash=leaf.ModuleHash,signature=leaf.SignatureHex,module_load_id=candidate.ModuleLoadIdentity} };
                var client=new ProfilerClient();
                Assert.Equal("inliner_coverage_changed",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,Preflight(omitted,[]),CancellationToken.None))).Code);
                var bound=permission with {Id="bound-callers",ContentHash=Wire.Hash(new {body_hash=candidate.BodyHash,inliners=candidate.Inliners})};
                Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(target,Preflight(bound,[]),CancellationToken.None))).Code);
                Assert.Equal(0,(await service.ProfilerAsync(target.Id,CancellationToken.None)).Epoch);
            }
            var apply=await ServiceTests.Wait(service,(await service.ApplyCilAsync(target.Id,0,candidate.Id,permission.Id,execute.Id,"native-inliner","apply")).Id);
            var outcome=service.Catalog.Get<MutationOutcome>(apply.ResultId!);
            Assert.False(outcome.Tainted); Assert.Equal(!incorrect,outcome.Measurable); Assert.Equal(incorrect,outcome.Restored);
            var root=service.Catalog.List("mutation").Rows.Select(r=>Wire.Deserialize<MutationRoot>(r.Json)).Single(r=>r.OperationId==apply.Id);
            if(!incorrect)
            {
                Assert.Equal(OperationState.Completed,apply.State);
                var run=new Tedd.DotNetLab.Contracts.RunRequest("inliner-run","run","block","native-inliner","candidate",100,0,true,false,
                    outcome.Epoch,target with {Epoch=outcome.Epoch},execute,DateTimeOffset.UtcNow.AddSeconds(30),candidate.Scope);
                Assert.True((await new HarnessClient().ExecuteAsync(run,CancellationToken.None)).Correct);
                var flushed=JsonSerializer.SerializeToElement(await service.FlushProbesAsync(target.Id,outcome.Epoch,candidate.Id,permission.Id),Wire.Json);
                Assert.Equal("100",flushed.GetProperty("entered").GetString());
                var compiled=service.Catalog.RecordsByPrefix("mutation_event",root.Id+"-event-").Select(r=>Wire.Deserialize<MutationEvent>(r.Json))
                    .Single(e=>e.Phase=="compilation_observed").Evidence!.Value;
                var affected=Assert.Single(compiled.GetProperty("affected_callers").EnumerateArray());
                Assert.True(affected.GetProperty("compilation_observed").GetBoolean()); Assert.NotEqual("0",affected.GetProperty("rejit_id").GetString());
                var restore=await ServiceTests.Wait(service,(await service.RestoreCilAsync(root.Id,outcome.Epoch,permission.Id,execute.Id,"restore")).Id);
                Assert.Equal(OperationState.Completed,restore.State); outcome=service.Catalog.Get<MutationOutcome>(restore.ResultId!);
            }
            else { Assert.Equal(OperationState.Failed,apply.State); Assert.Equal("correctness_failed",outcome.Failure); }
            Assert.True(outcome.Restored);
            var status=await new Tedd.DotNetLab.Transport.Local.ProfilerClient().SendAsync(target,
                new Tedd.DotNetLab.Transport.Local.ProfilerRequest(Wire.Version,"mutation_status",target.Id,process.Id,target.StartTimeUtcTicks,outcome.Epoch),CancellationToken.None);
            Assert.All(status.GetProperty("affected_callers").EnumerateArray(),r=>Assert.True(r.GetProperty("restore_api_succeeded").GetBoolean()));
            Assert.True(status.GetProperty("restore_api_succeeded").GetBoolean()); Assert.False(status.GetProperty("pending").GetBoolean());
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }
}
