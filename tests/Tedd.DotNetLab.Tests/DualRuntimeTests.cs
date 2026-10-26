using System.Text.Json;
using Tedd.DotNetLab.Analysis.Native;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class DualRuntimeTests
{
    [Theory,
        InlineData("windows","6.0.36",true),InlineData("windows","8.0.33",true),InlineData("windows","9.0.22",true),
        InlineData("windows","10.0.12",true),InlineData("windows","10.0.14",true),
        InlineData("windows","11.0.0-rc.1.26425.128",true),InlineData("windows","11.0.0-rc.2.26504.105",true),InlineData("windows","11.0.0",true),
        InlineData("windows","12.0.0-preview.1",true),InlineData("windows","12.0.0-dev",false),InlineData("windows","5.0.17",false),
        InlineData("linux","8.0.33",true),InlineData("linux","10.0.12",true),InlineData("linux","10.0.14",true),
        InlineData("linux","11.0.0-rc.2.26504.105",true),InlineData("linux","5.0.17",false),InlineData("macos","10.0.12",false),
        Case("UT-DUAL-RUNTIME","FR-001,FR-004","M7","Unit")]
    public void AdmissionAcceptsAnyCoreClr6PlusRuntimeOnSupportedPlatforms(string os,string runtime,bool expected)
        =>Assert.Equal(expected,RuntimeSupport.Supports(runtime,os));

    [Theory,InlineData(RuntimeSupport.Net10,false),InlineData(RuntimeSupport.Net10,true),
        InlineData(RuntimeSupport.Net10Patch,false),InlineData(RuntimeSupport.Net10Patch,true),
        InlineData(RuntimeSupport.Net11,false),InlineData(RuntimeSupport.Net11,true),
        Case("IT-DUAL-RUNTIME","FR-001,FR-039,FR-040,FR-052,FR-053,FR-074","M7","Integration")]
    public async Task DifferentControllerAndTargetRuntimesPreserveInspectionMutationAndRestoration(string requestedRuntime,bool attach)
    {
        if(!OperatingSystem.IsWindows()) return;
        string runtime;
        try { RuntimeSupport.ResolveRuntimeDirectory(requestedRuntime,"LatestPatch",out runtime); }
        catch(LabException) { return; }
        using var workspace=new TestWorkspace();
        using var process=attach?await NativeAttachTests.Start(workspace.Root,runtimeVersion:runtime):
            await ProfilerTests.StartNativeTarget(workspace.Root,runtimeVersion:runtime);
        try
        {
            await using var service=new LabService(workspace.Root,approvedProfilerPath:ProfilerTests.ProfilerPath,
                profilerAttachWorkerAssemblyPath:Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll"));
            var target=service.Discover().Single(item=>item.Identity.ProcessId==process.Id).Identity;
            var harness=await service.ScenariosAsync(target.Id,CancellationToken.None);
            Assert.Equal(runtime,harness.Target.RuntimeVersion);
            if(attach)
            {
                var plan=service.PrepareProfilerAttachment(target.Id,0);
                Assert.Equal(runtime,plan.Target.RuntimeVersion);
                var grant=service.IssueProfilerAttachmentGrant(plan.Id,90);
                var operation=await ServiceTests.Wait(service,service.StartProfilerAttachment(plan.Id,grant.Id,"dual-attach").Id);
                Assert.True(operation.State==OperationState.Completed,Wire.Serialize(operation));
            }
            var profiler=await service.ProfilerAsync(target.Id,CancellationToken.None);
            Assert.Equal(runtime,profiler.Runtime);
            Assert.Equal(CapabilityState.Supported,service.Target(target.Id).Capabilities["rejit"].State);
            var image=await service.ImportAsync(TestWorkspace.Workload(runtime),"application/vnd.microsoft.portable-executable");
            var method=Assert.Single((await service.MethodsAsync(image.Id,"NativeFixture.Value",100,null)).Rows);
            var inspect=service.IssueGrant(target.Id,Permission.InspectLiveCode,90,3);
            var inspection=await ServiceTests.Wait(service,(await service.InspectLiveAsync(target.Id,method.Id,0,inspect.Id,null,CancellationToken.None)).Id);
            Assert.True(inspection.State==OperationState.Completed,Wire.Serialize(inspection));
            var row=service.Catalog.Get<JsonElement>(inspection.ResultId!);
            var manifest=service.Catalog.Get<Artifact>(row.GetProperty("manifest_artifact_id").GetString()!);
            Assert.Equal(RuntimeSupport.Producer(runtime),manifest.Producer);
            var snapshot=Wire.Deserialize<NativeSnapshotReply>(await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(manifest)));
            Assert.All(snapshot.Snapshots,item=>Assert.Equal(runtime,item.RuntimeVersion));
            string candidatePath=Path.Combine(workspace.Root,"candidate.json");
            await File.WriteAllTextAsync(candidatePath,Wire.Serialize(new CilInput(Wire.Version,[0x16,0x1f,40,0x18,0x58,0x2a],"equivalent integer expression")));
            var input=await service.ImportAsync(candidatePath,"application/json");
            var candidate=await service.RegisterCilAsync(input.Id,inspection.ResultId!,snapshot.OriginalBodyHash,[]);
            var mutation=service.IssueMutationGrant(target.Id,candidate.Id,90,10);
            var execute=service.IssueGrant(target.Id,Permission.ExecuteRegisteredScenario,90,10);
            var applied=await ServiceTests.Wait(service,(await service.ApplyCilAsync(target.Id,0,candidate.Id,mutation.Id,execute.Id,"native-value","dual-apply")).Id);
            Assert.True(applied.State==OperationState.Completed,Wire.Serialize(applied));
            var outcome=service.Catalog.Get<MutationOutcome>(applied.ResultId!);
            Assert.True(outcome.Measurable); Assert.False(outcome.Tainted); Assert.False(outcome.PromotionEligible);
            var root=Assert.Single(service.Catalog.List("mutation").Rows.Select(record=>Wire.Deserialize<MutationRoot>(record.Json)),item=>item.OperationId==applied.Id);
            var restored=await ServiceTests.Wait(service,(await service.RestoreCilAsync(root.Id,outcome.Epoch,mutation.Id,execute.Id,"dual-restore")).Id);
            Assert.True(restored.State==OperationState.Completed,Wire.Serialize(restored));
            var restoration=service.Catalog.Get<MutationOutcome>(restored.ResultId!);
            Assert.True(restoration.Restored); Assert.False(restoration.Tainted);
            process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(0,process.ExitCode);
        }
        finally { if(!process.HasExited) process.Kill(true); await process.WaitForExitAsync(); }
    }
}
