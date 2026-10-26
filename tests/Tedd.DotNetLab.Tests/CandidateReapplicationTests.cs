using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Transport.Local;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CandidateReapplicationTests
{
    [Theory,InlineData("Value","native-value",false),InlineData("Value","native-value",true),InlineData("InlineLeaf","native-inliner",false),
        Case("UT-TXN-013,UT-TXN-014,IT-REJIT-003","FR-052,FR-053,FR-054,FR-062,FR-072","M6","Integration")]
    public async Task OriginalRestoreAndExplicitPriorCandidateReapplicationHaveDistinctVerifiedTransactions(string methodName,string scenario,bool attach)
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var process=attach?await NativeAttachTests.Start(workspace.Root):await ProfilerTests.StartNativeTarget(workspace.Root);
        string reapplicationRootId,originalRestoreRootId; string[] artifactIds;
        try
        {
            string host=Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll");
            await using(var service=new LabService(workspace.Root,approvedProfilerPath:ProfilerTests.ProfilerPath,profilerAttachWorkerAssemblyPath:host))
            {
                if(attach)
                {
                    var target=service.Discover().Single(row=>row.Identity.ProcessId==process.Id).Identity;
                    var plan=service.PrepareProfilerAttachment(target.Id,0); var grant=service.IssueProfilerAttachmentGrant(plan.Id,120);
                    Assert.Equal(OperationState.Completed,(await ServiceTests.Wait(service,service.StartProfilerAttachment(plan.Id,grant.Id,"load").Id)).State);
                }
                var prepared=await CompilationControlTests.Prepare(service,process.Id,methodName); var tools=new LabTools(service);
                var targetId=prepared.Target.Id; var inspect=service.IssueGrant(targetId,Permission.InspectLiveCode,240,8);
                var execute=service.IssueGrant(targetId,Permission.ExecuteRegisteredScenario,240,12);
                var snapshots=new List<string>();
                async Task<(string Id,NativeSnapshotReply Reply)> Capture(long epoch)
                {
                    var operation=await ServiceTests.Wait(service,(await service.InspectLiveAsync(targetId,prepared.Candidate.Method.Id,epoch,inspect.Id,null,CancellationToken.None)).Id);
                    Assert.True(operation.State==OperationState.Completed,Wire.Serialize(operation));
                    var row=service.Catalog.Get<JsonElement>(operation.ResultId!); var manifest=service.Catalog.Get<Artifact>(row.GetProperty("manifest_artifact_id").GetString()!);
                    snapshots.Add(manifest.Id); Assert.True(service.Catalog.IsPinned(manifest.Id));
                    return(operation.ResultId!,Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(manifest))));
                }
                async Task<CilCandidateRecord> Candidate(byte[] body,string acquisition,string baseline,IReadOnlyList<string> components)
                {
                    var artifact=await service.Artifacts.WriteJsonAsync("reapplication-input",new CilInput(Wire.Version,body,"Equivalent integer-expression candidate."),"qualification");
                    service.Catalog.Put(artifact.Id,"artifact",artifact); return await service.RegisterCilAsync(artifact.Id,acquisition,baseline,components);
                }
                async Task<(Operation Operation,MutationRoot Root,MutationOutcome Outcome,JsonElement Native)> Apply(CilCandidateRecord candidate,Grant grant,long epoch,string key)
                {
                    var accepted=await tools.Patch(new ApplyCil(targetId,epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),candidate.Id,grant.Id,execute.Id,scenario,key));
                    Assert.True(accepted.Status=="ok",Wire.Serialize(accepted)); var operation=await ServiceTests.Wait(service,accepted.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
                    Assert.True(operation.State==OperationState.Completed,Wire.Serialize(operation));
                    var outcome=service.Catalog.Get<MutationOutcome>(operation.ResultId!); var root=service.Catalog.Get<MutationRoot>(outcome.Id[..^8]);
                    Assert.True(outcome.Measurable&&!outcome.Tainted&&!outcome.Restored); Assert.False(outcome.PromotionEligible);
                    var target=service.Target(targetId).Identity; var native=await new ProfilerClient().SendAsync(target,
                        new ProfilerRequest(Wire.Version,"mutation_status",targetId,target.ProcessId,target.StartTimeUtcTicks,outcome.Epoch),CancellationToken.None);
                    Assert.Equal(root.NativeTransactionId,native.GetProperty("transaction_id").GetString()); Assert.Equal(candidate.BodyHash,native.GetProperty("current_body_hash").GetString());
                    Assert.True(native.GetProperty("compilation_observed").GetBoolean()); Assert.False(native.GetProperty("pending").GetBoolean());
                    Assert.Equal(candidate.Inliners!.Count,native.GetProperty("affected_callers").GetArrayLength());
                    Assert.All(native.GetProperty("affected_callers").EnumerateArray(),caller=>Assert.True(caller.GetProperty("compilation_observed").GetBoolean()));
                    return(operation,root,outcome,native);
                }
                async Task<MutationRoot> Restore(MutationRoot source,MutationOutcome applied,Grant grant,string key)
                {
                    var accepted=await tools.Patch(new RestoreCil(source.Id,applied.Epoch.ToString(System.Globalization.CultureInfo.InvariantCulture),grant.Id,execute.Id,key));
                    Assert.True(accepted.Status=="ok",Wire.Serialize(accepted)); var operation=await ServiceTests.Wait(service,accepted.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
                    Assert.True(operation.State==OperationState.Completed,Wire.Serialize(operation)); var result=service.Catalog.Get<MutationOutcome>(operation.ResultId!);
                    Assert.True(result.Restored&&!result.Tainted&&!result.Measurable); Assert.False(result.PromotionEligible);
                    var root=service.Catalog.Get<MutationRoot>(result.Id[..^8]); Assert.Equal(source.Id,root.RestoresTransactionId); Assert.Equal(source.NativeTransactionId,root.NativeTransactionId);
                    var journal=service.Catalog.RecordsByPrefix("mutation_event",root.Id+"-event-").Select(row=>Wire.Deserialize<MutationEvent>(row.Json));
                    var restored=Assert.Single(journal,item=>item.Phase=="restored");
                    Assert.Equal(prepared.Candidate.OriginalIlHash,restored.Evidence!.Value.GetProperty("current_body_hash").GetString());
                    Assert.Equal("restored_original_version",restored.Evidence.Value.GetProperty("mode").GetString());
                    Assert.True(restored.Evidence.Value.GetProperty("restore_api_succeeded").GetBoolean());
                    Assert.All(restored.Evidence.Value.GetProperty("affected_callers").EnumerateArray(),caller=>Assert.True(caller.GetProperty("restore_api_succeeded").GetBoolean()));
                    return root;
                }
                bool inline=methodName=="InlineLeaf";
                byte[] bodyA=inline?[0x1e,0x02,0x1f,40,0x17,0x58,0x58,0x2a]:[0x16,0x1f,40,0x18,0x58,0x2a];
                byte[] bodyB=inline?[0x1e,0x02,0x1f,39,0x18,0x58,0x58,0x2a]:[0x16,0x1f,39,0x19,0x58,0x2a];
                var candidateA=await Candidate(bodyA,prepared.InspectionId,prepared.Candidate.OriginalIlHash,[]);
                Assert.Equal(inline?1:0,candidateA.Inliners!.Count); var grantA=service.IssueMutationGrant(targetId,candidateA.Id,240,8);
                var first=await Apply(candidateA,grantA,0,"candidate-a"); var aCode=await Capture(first.Outcome.Epoch);
                Assert.Contains(aCode.Reply.Snapshots,snapshot=>snapshot.RejitId!="0"&&snapshot.CurrentIlHash==candidateA.BodyHash);
                var candidateB=await Candidate(bodyB,aCode.Id,candidateA.BodyHash,[candidateA.Id]);
                Assert.Equal(candidateA.OriginalIlHash,candidateB.OriginalIlHash); Assert.NotEqual(candidateA.BodyHash,candidateB.BodyHash);
                var grantB=service.IssueMutationGrant(targetId,candidateB.Id,240,8);
                var second=await Apply(candidateB,grantB,first.Outcome.Epoch,"candidate-b"); var bCode=await Capture(second.Outcome.Epoch);
                Assert.Contains(bCode.Reply.Snapshots,snapshot=>snapshot.RejitId!="0"&&snapshot.CurrentIlHash==candidateB.BodyHash);
                Assert.NotEqual(first.Native.GetProperty("rejit_id").GetString(),second.Native.GetProperty("rejit_id").GetString());
                var originalRestore=await Restore(second.Root,second.Outcome,grantB,"restore-original"); originalRestoreRootId=originalRestore.Id;
                long restoredEpoch=service.Target(targetId).Identity.Epoch;
                Assert.Equal(second.Outcome.Epoch+1,restoredEpoch);
                var restoredCode=await Capture(restoredEpoch);
                Assert.Equal(candidateA.OriginalIlHash,restoredCode.Reply.OriginalBodyHash);
                var reapplied=await Apply(candidateA,grantA,restoredEpoch,"reapply-earlier-a"); reapplicationRootId=reapplied.Root.Id;
                Assert.NotEqual(first.Root.Id,reapplied.Root.Id); Assert.NotEqual(first.Root.NativeTransactionId,reapplied.Root.NativeTransactionId);
                Assert.Null(reapplied.Root.RestoresTransactionId); Assert.Equal(candidateA.Id,reapplied.Root.CandidateId);
                Assert.NotEqual(first.Native.GetProperty("rejit_id").GetString(),reapplied.Native.GetProperty("rejit_id").GetString());
                var replay=await service.ApplyCilAsync(targetId,restoredEpoch,candidateA.Id,grantA.Id,execute.Id,scenario,"reapply-earlier-a"); Assert.Equal(reapplied.Operation.Id,replay.Id);
                var reapplication=service.Catalog.RecordsByPrefix("mutation_event",reapplied.Root.Id+"-event-").Select(row=>Wire.Deserialize<MutationEvent>(row.Json)).ToArray();
                Assert.Contains(reapplication,item=>item.Phase=="request_submitted"); Assert.Contains(reapplication,item=>item.Phase=="compilation_observed");
                Assert.DoesNotContain(reapplication,item=>item.Phase=="restoring");
                var reappliedCode=await Capture(reapplied.Outcome.Epoch);
                Assert.Contains(reappliedCode.Reply.Snapshots,snapshot=>snapshot.RejitId==reapplied.Native.GetProperty("rejit_id").GetString()&&snapshot.CurrentIlHash==candidateA.BodyHash);
                await Restore(reapplied.Root,reapplied.Outcome,grantA,"restore-final-original"); artifactIds=snapshots.ToArray();
            }
            process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0,process.ExitCode);
            await using var reopened=new LabService(workspace.Root);
            Assert.NotNull(reopened.Catalog.Get<MutationRoot>(originalRestoreRootId).RestoresTransactionId);
            Assert.Null(reopened.Catalog.Get<MutationRoot>(reapplicationRootId).RestoresTransactionId);
            foreach(string artifactId in artifactIds) Assert.True(File.Exists(await reopened.Artifacts.VerifiedPathAsync(reopened.Catalog.Get<Artifact>(artifactId))));
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
