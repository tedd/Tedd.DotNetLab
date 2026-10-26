using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Transport.Local;
using Microsoft.Diagnostics.NETCore.Client;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class NativeAttachTests
{
    internal static async Task<Process> Start(string workspace,bool disposable=true,bool budgetGrowthStimulus=false,bool activeFrameStimulus=false,string? runtimeVersion=null)
    {
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        if(runtimeVersion is not null) { start.FileName=RuntimeSupport.StartInfo(runtimeVersion).FileName; RuntimeSupport.AddRuntimeArguments(start,runtimeVersion); }
        start.ArgumentList.Add(TestWorkspace.Workload(runtimeVersion));
        start.ArgumentList.Add("--harness"); start.ArgumentList.Add(workspace);
        start.Environment["CORECLR_ENABLE_PROFILING"]="0"; start.Environment["DOTNET_ENABLE_PROFILING"]="0";
        start.Environment["DOTNETLAB_WORKSPACE"]=workspace; start.Environment["DOTNETLAB_DISPOSABLE"]=disposable?"1":"0";
        start.Environment["DOTNETLAB_EXIT_ON_STDIN"]="1";
        start.Environment["DOTNETLAB_BUDGET_GROWTH_STIMULUS"]=budgetGrowthStimulus?"1":"0";
        start.Environment["DOTNETLAB_ACTIVE_FRAME_STIMULUS"]=activeFrameStimulus?"1":"0";
        var target=Process.Start(TestWorkspace.PinRuntime(start))!;
        try
        {
            if(await target.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)) is null)
                throw new InvalidOperationException(await target.StandardError.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(5)));
            await CaptureTests.WaitForDiagnosticPublicationAsync(target); return target;
        }
        catch { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); target.Dispose(); throw; }
    }
    private static byte[] Payload(TargetIdentity target,string workspace)=>Encoding.UTF8.GetBytes(Wire.Serialize(new {
        schema_version=Wire.Version,target_id=target.Id,process_id=target.ProcessId,start_time_utc_ticks=target.StartTimeUtcTicks,workspace }));
    private static void Attach(TargetIdentity target,byte[] data)=>new DiagnosticsClient(target.ProcessId).AttachProfiler(
        TimeSpan.FromSeconds(10),Guid.Parse(ProfilerTests.ProfilerId),ProfilerTests.ProfilerPath,data);

    [Theory,InlineData(false,false),InlineData(true,false),InlineData(true,true),
        Case("IT-REJIT-001","FR-001,FR-003,FR-040,FR-052,FR-053,FR-054,FR-070,FR-072","M6","Integration")]
    public async Task StartupAndAttachLeafTransactionsReportObservedCompilationAndVerifiedRestoration(bool attach,bool incorrect)
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=attach?await Start(workspace.Root):await ProfilerTests.StartNativeTarget(workspace.Root);
        string outcomeId;
        try
        {
            await using(var service=new LabService(workspace.Root))
            {
                var discovered=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                if(attach)
                {
                    var before=await service.ScenariosAsync(discovered.Id,CancellationToken.None); Assert.False(before.Runtime!.NativeProfilerPresent);
                    Attach(before.Target,Payload(before.Target,workspace.Root));
                }
                var handshake=await service.ProfilerAsync(discovered.Id,CancellationToken.None);
                Assert.Equal(!attach,handshake.Startup); Assert.True(handshake.AttachmentComplete); Assert.Equal((uint)0x40024,handshake.EventMaskLow);
                Assert.Equal(attach?"verified_noinline_only":"experimental",handshake.InlinerHandling);
                Assert.Equal(CapabilityState.Supported,service.Target(discovered.Id).Capabilities["rejit"].State);
                var prepared=await CompilationControlTests.Prepare(service,target.Id);
                CilCandidateRecord candidate;
                if(incorrect)
                {
                    var artifact=await service.Artifacts.WriteJsonAsync("bad-attach",new CilInput(Wire.Version,[0x0e,0x1f,43,0x2a],"Wrong constant fixture."),"fixture");
                    service.Catalog.Put(artifact.Id,"artifact",artifact); candidate=await service.RegisterCilAsync(artifact.Id,prepared.InspectionId,prepared.Candidate.OriginalIlHash,[]);
                }
                else candidate=await CompilationAdmissionTests.Candidate(service,prepared.InspectionId,prepared.Candidate.OriginalIlHash);
                var mutate=service.IssueMutationGrant(prepared.Target.Id,candidate.Id,120,10);
                var execute=service.IssueGrant(prepared.Target.Id,Permission.ExecuteRegisteredScenario,120,10);
                await service.PreflightCilAsync(prepared.Target.Id,0,candidate.Id,mutate.Id);
                var applied=await ServiceTests.Wait(service,(await service.ApplyCilAsync(prepared.Target.Id,0,candidate.Id,mutate.Id,execute.Id,"native-value","attach-apply")).Id);
                var outcome=service.Catalog.Get<MutationOutcome>(applied.ResultId!);
                Assert.Equal(incorrect?OperationState.Failed:OperationState.Completed,applied.State);
                Assert.Equal(!incorrect,outcome.Measurable); Assert.Equal(incorrect,outcome.Restored); Assert.False(outcome.Tainted||outcome.PromotionEligible);
                var root=service.Catalog.List("mutation").Rows.Select(r=>Wire.Deserialize<MutationRoot>(r.Json)).Single(r=>r.OperationId==applied.Id);
                if(!incorrect)
                {
                    var status=await new ProfilerClient().SendAsync(prepared.Target,new ProfilerRequest(Wire.Version,"mutation_status",prepared.Target.Id,
                        target.Id,prepared.Target.StartTimeUtcTicks,outcome.Epoch),CancellationToken.None);
                    Assert.True(status.GetProperty("compilation_observed").GetBoolean()); Assert.True(status.GetProperty("correctness_passed").GetBoolean());
                    Assert.Equal("excluded_by_verified_noinline_method",status.GetProperty("inliner_coverage").GetString());
                    var restored=await ServiceTests.Wait(service,(await service.RestoreCilAsync(root.Id,outcome.Epoch,mutate.Id,execute.Id,"attach-restore")).Id);
                    Assert.Equal(OperationState.Completed,restored.State); outcome=service.Catalog.Get<MutationOutcome>(restored.ResultId!);
                }
                Assert.True(outcome.Restored); Assert.False(outcome.Tainted); outcomeId=outcome.Id;
                var final=await new ProfilerClient().SendAsync(prepared.Target,new ProfilerRequest(Wire.Version,"mutation_status",prepared.Target.Id,
                    target.Id,prepared.Target.StartTimeUtcTicks,outcome.Epoch),CancellationToken.None);
                Assert.True(final.GetProperty("restore_api_succeeded").GetBoolean()); Assert.False(final.GetProperty("pending").GetBoolean());
            }
            target.StandardInput.Close(); await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.Equal(0,target.ExitCode);
            await using var reopened=new LabService(workspace.Root); Assert.True(reopened.Catalog.Get<MutationOutcome>(outcomeId).Restored);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }

    [Fact,Case("IT-ATTACH-NOINLINE-BOUNDARY","FR-005,FR-040,FR-052,FR-070","M6","Integration")]
    public async Task AttachDoesNotInventPreAttachCallerHistoryOrAdmitAnInlineableMutation()
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=await Start(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var id=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            Attach(id,Payload(id,workspace.Root)); var handshake=await service.ProfilerAsync(id.Id,CancellationToken.None); var client=new ProfilerClient();
            Assert.Equal("verified_noinline_only",handshake.InlinerHandling); Assert.Equal("unsupported",handshake.Probes);
            string file=Path.Combine(TestWorkspace.TaskRoot,"bin","LabWorkload","Release","net10.0","LabWorkload.dll");
            string imageHash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(await File.ReadAllBytesAsync(file)));
            var method=Assert.Single(Tedd.DotNetLab.Analysis.Native.PeInspector.Methods(file,imageHash,"NativeFixture.InlineLeaf").Rows);
            object selector=new { module_mvid=method.ModuleMvid,method_token=method.Token,module_hash=method.ModuleHash,signature=method.SignatureHex };
            var inspect=service.IssueGrant(id.Id,Permission.InspectLiveCode,60,2);
            var snapshot=await client.SendAsync(id,new { schema_version=Wire.Version,action="snapshot",target_id=id.Id,process_id=id.ProcessId,
                start_time_utc_ticks=id.StartTimeUtcTicks,expected_target_epoch=0L,grant=inspect,selector },CancellationToken.None);
            Assert.Equal("not_collected_pre_attach_history",snapshot.GetProperty("inliner_coverage").GetString()); Assert.Empty(snapshot.GetProperty("inliners").EnumerateArray());
            byte[] body=[0x0e,0x1f,42,0x2a]; string hash=Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body));
            var grant=new Grant("inline-boundary",workspace.Root,id.Id,id.StartTimeUtcTicks,Permission.ReplaceIl,DateTimeOffset.UtcNow.AddSeconds(60),1,
                hash,method.ModuleMvid+"/"+method.Token+"/"+method.ModuleHash);
            var rejected=await Assert.ThrowsAsync<LabException>(()=>client.SendAsync(id,new { schema_version=Wire.Version,action="preflight",target_id=id.Id,
                process_id=id.ProcessId,start_time_utc_ticks=id.StartTimeUtcTicks,expected_target_epoch=0L,grant,selector,body,transaction_id="inline-boundary",base_il_hash=hash },CancellationToken.None));
            Assert.Equal("inliner_coverage_unknown",rejected.Code);
            var status=await client.SendAsync(id,new ProfilerRequest(Wire.Version,"mutation_status",id.Id,id.ProcessId,id.StartTimeUtcTicks,0),CancellationToken.None);
            Assert.Equal(0,status.GetProperty("mutation_count").GetInt32()); Assert.False(status.GetProperty("pending").GetBoolean()); Assert.Equal("0",status.GetProperty("epoch").GetString());
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
    [Fact,Case("IT-ATTACH-DISPOSABLE-BOUNDARY","FR-005,FR-070","M6","Integration")]
    public async Task NondisposableAttachExposesInspectionAndRejectsMutation()
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=await Start(workspace.Root,false);
        try
        {
            await using var service=new LabService(workspace.Root); var id=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            Attach(id,Payload(id,workspace.Root)); var handshake=await service.ProfilerAsync(id.Id,CancellationToken.None);
            Assert.Equal(CapabilityState.Supported,service.Target(id.Id).Capabilities["native_inspection"].State);
            Assert.Equal("unsupported",handshake.Rejit); Assert.Equal("unsupported",handshake.Restore);
            var rejected=await Assert.ThrowsAsync<LabException>(()=>new ProfilerClient().SendAsync(id,new ProfilerRequest(Wire.Version,"apply",id.Id,id.ProcessId,id.StartTimeUtcTicks,0),CancellationToken.None));
            Assert.Equal("capability_unsupported",rejected.Code); Assert.Equal(0,(await new ProfilerClient().HandshakeAsync(id,CancellationToken.None)).Epoch);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }

    [Theory,InlineData("pid"),InlineData("start"),InlineData("schema"),InlineData("workspace"),InlineData("relative"),InlineData("fields"),InlineData("malformed"),InlineData("oversize"),
        Case("IT-ATTACH-METADATA-REJECTION","FR-002,FR-016,FR-070,FR-074","M7","Integration")]
    public async Task InvalidAttachMetadataCannotExposeAQualifiedEndpoint(string defect)
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=await Start(workspace.Root);
        try
        {
            await using var service=new LabService(workspace.Root); var id=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var fields=JsonSerializer.Deserialize<Dictionary<string,JsonElement>>(Payload(id,workspace.Root))!;
            void Set<T>(string key,T value)=>fields[key]=JsonSerializer.SerializeToElement(value,Wire.Json);
            switch(defect)
            {
                case "pid": Set("process_id",id.ProcessId+1); break;
                case "start": Set("start_time_utc_ticks",id.StartTimeUtcTicks-1); break;
                case "schema": Set("schema_version","unsupported"); break;
                case "workspace": Set("workspace",workspace.Root+"-other"); break;
                case "relative": Set("workspace","relative"); break;
                case "fields": Set("unexpected",true); break;
            }
            byte[] payload=defect=="malformed"?Encoding.UTF8.GetBytes("{"):defect=="oversize"?new byte[16385]:Encoding.UTF8.GetBytes(Wire.Serialize(fields));
            Assert.ThrowsAny<DiagnosticsClientException>(()=>Attach(id,payload));
            var endpoint=await Assert.ThrowsAsync<LabException>(()=>new ProfilerClient().HandshakeAsync(id,CancellationToken.None)); Assert.Equal("capability_unsupported",endpoint.Code);
            Assert.Equal(CapabilityState.Unsupported,service.Target(id.Id).Capabilities["rejit"].State);
        }
        finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
    }
}
