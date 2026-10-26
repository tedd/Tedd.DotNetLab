using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Transport.Local;
using Xunit;
using Xunit.Abstractions;

namespace Tedd.DotNetLab.Tests;

public sealed class MutationSessionBudgetTests(ITestOutputHelper output)
{
    [Theory,InlineData(false),InlineData(true),Case("UT-TXN-019","FR-016,FR-053","M7","Integration")]
    public async Task LivePrivateCommitGrowthBlocksSubmissionAndRequiresNewProcess(bool attach)
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace();
        using var process=attach?await NativeAttachTests.Start(workspace.Root,budgetGrowthStimulus:true):await ProfilerTests.StartNativeTarget(workspace.Root,budgetGrowthStimulus:true);
        try
        {
            string host=Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll");
            await using var service=new LabService(workspace.Root,approvedProfilerPath:ProfilerTests.ProfilerPath,profilerAttachWorkerAssemblyPath:host);
            var discovered=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
            if(attach)
            {
                var plan=service.PrepareProfilerAttachment(discovered.Id,0); var grant=service.IssueProfilerAttachmentGrant(plan.Id,120);
                Assert.Equal(OperationState.Completed,(await ServiceTests.Wait(service,service.StartProfilerAttachment(plan.Id,grant.Id,"load").Id)).State);
            }
            var prepared=await CompilationControlTests.Prepare(service,process.Id);
            var mutate=service.IssueMutationGrant(prepared.Target.Id,prepared.Candidate.Id,120,8);
            await service.PreflightCilAsync(prepared.Target.Id,0,prepared.Candidate.Id,mutate.Id);
            var before=await Status(service,prepared.Target.Id,0); Assert.False(before.GetProperty("resource_budget").GetProperty("restart_required").GetBoolean());
            var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,120,2);
            var target=service.Target(prepared.Target.Id).Identity;
            var result=await new HarnessClient().ExecuteAsync(new Tedd.DotNetLab.Contracts.RunRequest("memory-stimulus","memory-stimulus","memory-stimulus",
                "native-budget-growth","baseline",1,0,true,false,0,target,execute,DateTimeOffset.UtcNow.AddSeconds(30)),CancellationToken.None);
            Assert.True(result.Correct&&result.Quiescent);
            Assert.Equal("resource_limit",(await Assert.ThrowsAsync<LabException>(()=>service.PreflightCilAsync(target.Id,0,prepared.Candidate.Id,mutate.Id))).Code);
            var denied=await ServiceTests.Wait(service,(await service.ApplyCilAsync(target.Id,0,prepared.Candidate.Id,mutate.Id,execute.Id,"native-value","over-memory-budget")).Id);
            Assert.Equal(OperationState.Failed,denied.State); Assert.Equal("resource_limit",denied.ErrorCode);
            var rejection=Assert.Single(service.Catalog.RecordsByPrefix("mutation_event",denied.ResultId![..^8]+"-event-")
                .Select(row=>Wire.Deserialize<MutationEvent>(row.Json)),item=>item.Phase=="resource_budget_rejected");
            Assert.Equal("private_commit_growth",rejection.Evidence!.Value.GetProperty("resource_budget").GetProperty("restart_reason").GetString());
            var status=await Status(service,target.Id,0); var budget=status.GetProperty("resource_budget");
            Assert.Equal("private_commit_growth",budget.GetProperty("restart_reason").GetString()); Assert.True(budget.GetProperty("restart_required").GetBoolean());
            Assert.True(Wire.Deserialize<long>(budget.GetProperty("private_commit_growth_bytes").GetRawText())>
                Wire.Deserialize<long>(budget.GetProperty("maximum_private_commit_growth_bytes").GetRawText()));
            Assert.Equal(0,status.GetProperty("mutation_count").GetInt32()); Assert.Equal("0",status.GetProperty("epoch").GetString());
            Assert.False(status.GetProperty("pending").GetBoolean()); Assert.False(status.GetProperty("tainted").GetBoolean());
            output.WriteLine(Wire.Serialize(new { target,attach,result,budget }));
            await process.StandardInput.WriteLineAsync("exit"); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0,process.ExitCode);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Fact,Case("UT-TXN-019,IT-REJIT-005","FR-016,FR-052,FR-053,FR-072","M7","Integration")]
    public async Task HundredsOfVerifiedMutationsEnforceSessionBudgetAndRestartWithFreshIdentities()
    {
        if(!OperatingSystem.IsWindows()) return;
        var sessions=new List<object>(); var identities=new HashSet<string>(); int applications=0,restorations=0;
        for(int session=0;session<8;session++)
        {
            using var workspace=new TestWorkspace(); bool attach=session%2==1;
            using var process=attach?await NativeAttachTests.Start(workspace.Root):await ProfilerTests.StartNativeTarget(workspace.Root);
            try
            {
                string host=Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll");
                await using var service=new LabService(workspace.Root,approvedProfilerPath:ProfilerTests.ProfilerPath,profilerAttachWorkerAssemblyPath:host);
                var discovered=service.Discover().Single(t=>t.Identity.ProcessId==process.Id).Identity;
                Assert.True(identities.Add(discovered.Id));
                if(attach)
                {
                    var plan=service.PrepareProfilerAttachment(discovered.Id,0); var grant=service.IssueProfilerAttachmentGrant(plan.Id,120);
                    var loaded=await ServiceTests.Wait(service,service.StartProfilerAttachment(plan.Id,grant.Id,"load").Id);
                    Assert.True(loaded.State==OperationState.Completed,Wire.Serialize(loaded));
                }
                var prepared=await CompilationControlTests.Prepare(service,process.Id);
                var candidate=await CompilationAdmissionTests.Candidate(service,prepared.InspectionId,prepared.Candidate.OriginalIlHash);
                var mutation=service.IssueMutationGrant(prepared.Target.Id,candidate.Id,300,80);
                var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,300,80);
                var versions=new HashSet<string>(); long previousBytes=0,previousGrowth=0;
                JsonElement lastBudget=default;
                for(int cycle=0;cycle<32;cycle++)
                {
                    var applied=await ServiceTests.Wait(service,(await service.ApplyCilAsync(prepared.Target.Id,cycle*2,candidate.Id,mutation.Id,execute.Id,
                        "native-value","apply-"+cycle,setupOperations:10)).Id);
                    Assert.True(applied.State==OperationState.Completed,Wire.Serialize(applied));
                    var activated=service.Catalog.Get<MutationOutcome>(applied.ResultId!); Assert.True(activated.Measurable&&!activated.Tainted); applications++;
                    var status=await Status(service,prepared.Target.Id,activated.Epoch);
                    Assert.True(versions.Add(status.GetProperty("rejit_id").GetString()!));
                    Assert.Equal(cycle+1,status.GetProperty("mutation_count").GetInt32());
                    var budget=status.GetProperty("resource_budget");
                    Assert.Equal(32,budget.GetProperty("maximum_candidates").GetInt32()); Assert.True(budget.GetProperty("private_commit_available").GetBoolean());
                    long bytes=Wire.Deserialize<long>(budget.GetProperty("native_extent_bytes").GetRawText()),growth=Wire.Deserialize<long>(budget.GetProperty("private_commit_growth_bytes").GetRawText());
                    Assert.True(bytes>previousBytes); Assert.True(growth>=previousGrowth);
                    Assert.True(bytes<=Wire.Deserialize<long>(budget.GetProperty("maximum_native_extent_bytes").GetRawText()));
                    Assert.True(growth<=Wire.Deserialize<long>(budget.GetProperty("maximum_private_commit_growth_bytes").GetRawText()));
                    previousBytes=bytes; previousGrowth=growth;
                    Assert.Equal(cycle==31,budget.GetProperty("restart_required").GetBoolean());
                    var restored=await ServiceTests.Wait(service,(await service.RestoreCilAsync(activated.Id[..^8],activated.Epoch,mutation.Id,execute.Id,"restore-"+cycle)).Id);
                    Assert.True(restored.State==OperationState.Completed,Wire.Serialize(restored));
                    var original=service.Catalog.Get<MutationOutcome>(restored.ResultId!); Assert.True(original.Restored&&!original.Tainted); restorations++;
                    status=await Status(service,prepared.Target.Id,original.Epoch); lastBudget=status.GetProperty("resource_budget").Clone();
                    Assert.False(status.GetProperty("pending").GetBoolean()); Assert.False(status.GetProperty("tainted").GetBoolean());
                    Assert.Equal(candidate.OriginalIlHash,status.GetProperty("current_body_hash").GetString());
                }
                Assert.Equal("candidate_count",lastBudget.GetProperty("restart_reason").GetString());
                var denied=await ServiceTests.Wait(service,(await service.ApplyCilAsync(prepared.Target.Id,64,candidate.Id,mutation.Id,execute.Id,"native-value","over-budget")).Id);
                Assert.Equal(OperationState.Failed,denied.State); Assert.Equal("resource_limit",denied.ErrorCode);
                var rejection=Assert.Single(service.Catalog.RecordsByPrefix("mutation_event",denied.ResultId![..^8]+"-event-")
                    .Select(row=>Wire.Deserialize<MutationEvent>(row.Json)),item=>item.Phase=="resource_budget_rejected");
                Assert.Equal("candidate_count",rejection.Evidence!.Value.GetProperty("resource_budget").GetProperty("restart_reason").GetString());
                var final=await Status(service,prepared.Target.Id,64);
                Assert.Equal(32,final.GetProperty("mutation_count").GetInt32()); Assert.Equal("64",final.GetProperty("epoch").GetString());
                Assert.False(final.GetProperty("pending").GetBoolean()); Assert.False(final.GetProperty("tainted").GetBoolean());
                Assert.Equal("permission_denied",(await Assert.ThrowsAsync<LabException>(()=>service.PreflightCilAsync(prepared.Target.Id,64,candidate.Id,execute.Id))).Code);
                Assert.Equal("resource_limit",(await Assert.ThrowsAsync<LabException>(()=>service.PreflightCilAsync(prepared.Target.Id,64,candidate.Id,mutation.Id))).Code);
                sessions.Add(new { target=prepared.Target,attach,applications=32,restorations=32,resource_budget=final.GetProperty("resource_budget").Clone() });
                await process.StandardInput.WriteLineAsync("exit"); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(0,process.ExitCode);
            }
            finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        }
        Assert.Equal(256,applications); Assert.Equal(256,restorations); Assert.Equal(8,identities.Count);
        output.WriteLine(Wire.Serialize(new { schema_version="windows-mutation-session-qualification/v1",applications,restorations,sessions,
            memory_accounting="sampled private commit at native control boundaries; other target allocations included",
            code_accounting="cumulative observed regions for approved ReJIT methods and callers; runtime bookkeeping excluded" }));
    }
    private static Task<JsonElement> Status(LabService service,string targetId,long epoch)
    {
        var target=service.Target(targetId).Identity;
        return new ProfilerClient().SendAsync(target,new ProfilerRequest(Wire.Version,"mutation_status",target.Id,target.ProcessId,target.StartTimeUtcTicks,epoch),CancellationToken.None);
    }
}
