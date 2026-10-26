using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Storage;
using Tedd.DotNetLab.Transport.Local;
using Microsoft.Diagnostics.NETCore.Client;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ProfilerAttachmentTests
{
    private static string Host=>Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll");
    private static LabService Service(string root,string? binary=null,string? worker=null)=>new(root,
        approvedProfilerPath:binary??ProfilerTests.ProfilerPath,profilerAttachWorkerAssemblyPath:worker??Host);
    private static async Task Stop(Process target)
    { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); }

    [Fact,Case("IT-ATTACH-001","FR-001,FR-003,FR-040,FR-070,FR-072","M4","Integration")]
    public async Task FrozenMcpPlanHumanCliGrantAndBoundedWorkerLoadApprovedProfilerOnce()
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=await NativeAttachTests.Start(workspace.Root);
        string packagedBinary=Path.Combine(Path.GetDirectoryName(Host)!,"Tedd.DotNetLab.Profiler.dll");
        try
        {
            ProfilerAttachmentPlan plan;
            await using(var service=Service(workspace.Root,packagedBinary))
            {
                var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                var reply=await new LabTools(service).Experiment(new PrepareProfilerAttachment(identity.Id,"0"));
                Assert.Equal("ok",reply.Status); plan=reply.Data!.Value.Deserialize<ProfilerAttachmentPlan>(Wire.Json)!;
                Assert.Equal(packagedBinary,plan.BinaryPath); Assert.Equal(64,plan.BinaryHash.Length);
            }
            var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
            foreach(string argument in new[]{Host,"grant-profiler-attach",workspace.Root,plan.Id,"120","1"}) start.ArgumentList.Add(argument);
            using var cli=Process.Start(TestWorkspace.PinRuntime(start))!; var output=cli.StandardOutput.ReadToEndAsync(); var errors=cli.StandardError.ReadToEndAsync();
            await cli.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.True(cli.ExitCode==0,await errors);
            var grant=Wire.Deserialize<Grant>(await output); Assert.Equal(Permission.AttachProfiler,grant.Permission); Assert.Equal(plan.ContentHash,grant.ContentHash);
            string outcomeId;
            await using(var service=Service(workspace.Root,packagedBinary))
            {
                var tools=new LabTools(service);
                var accepted=await tools.Run(new StartProfilerAttachment(plan.Id,grant.Id,"approved-attach"));
                Assert.Equal("ok",accepted.Status); var operation=accepted.Data!.Value.Deserialize<Operation>(Wire.Json)!;
                var finished=await ServiceTests.Wait(service,operation.Id); Assert.True(finished.State==OperationState.Completed,finished.ErrorCode);
                var outcome=service.Catalog.Get<ProfilerAttachmentOutcome>(finished.ResultId!); outcomeId=outcome.Id;
                Assert.Equal("attached",outcome.State); Assert.True(outcome.Handshake!.AttachmentComplete); Assert.False(outcome.Handshake.Startup);
                Assert.Equal((uint)0x40024,outcome.Handshake.EventMaskLow);
                Assert.Equal(operation.Id,service.StartProfilerAttachment(plan.Id,grant.Id,"approved-attach").Id);
                Assert.Equal("profiler_conflict",Assert.Throws<LabException>(()=>service.PrepareProfilerAttachment(plan.Target.Id,0)).Code);
                Assert.Equal(CapabilityState.Supported,service.Target(plan.Target.Id).Capabilities["native_inspection"].State);
                var method=await CompilationControlTests.Prepare(service,target.Id); Assert.NotEmpty(method.InspectionId);
                var successor=service.Catalog.Accept("local","post-attach","post-attach","native_inspection",plan.Target.Id);
                service.Catalog.AcquireLease(plan.Target.Id,successor.Id); service.Catalog.Transition(successor.Id,OperationState.Running); service.Catalog.Transition(successor.Id,OperationState.Completed);
                var manifest=service.Catalog.Get<JsonElement>(outcome.Id+"-manifest");
                var artifact=service.Catalog.Get<Artifact>(manifest.GetProperty("artifact_id").GetString()!); Assert.True(artifact.Complete); Assert.True(service.Catalog.IsPinned(artifact.Id));
            }
            await Stop(target);
            await using var reopened=Service(workspace.Root,packagedBinary);
            Assert.Equal("attached",reopened.Catalog.Get<ProfilerAttachmentOutcome>(outcomeId).State);
        }
        finally { await Stop(target); }
    }

    [Theory,InlineData(Permission.Observe),InlineData(Permission.ExecuteRegisteredScenario),
        Case("UT-ATTACH-001","FR-003,FR-070","M4","Integration")]
    public async Task OrdinaryGrantCannotSubmitProfilerLoad(Permission permission)
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=await NativeAttachTests.Start(workspace.Root);
        try
        {
            await using var service=Service(workspace.Root); var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var plan=service.PrepareProfilerAttachment(identity.Id,0); var grant=service.IssueGrant(identity.Id,permission,120,1);
            Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartProfilerAttachment(plan.Id,grant.Id,"denied")).Code);
            Assert.Empty(service.Catalog.List("profiler_attachment_intent").Rows); ProfilerAttachmentIdentity.VerifyRuntime(identity,true);
            Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>service.IssueGrant(identity.Id,Permission.AttachProfiler,120,1)).Code);
        }
        finally { await Stop(target); }
    }

    [Fact,Case("UT-ATTACH-002","FR-003,FR-070","M4","Integration")]
    public async Task ExpiredDifferentPlanAndChangedBinaryAreRejectedBeforeIntent()
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=await NativeAttachTests.Start(workspace.Root);
        string binary=Path.Combine(workspace.Root,"Tedd.DotNetLab.Profiler.dll"); File.Copy(ProfilerTests.ProfilerPath,binary);
        try
        {
            await using var service=Service(workspace.Root,binary); var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var plan=service.PrepareProfilerAttachment(identity.Id,0); var grant=service.IssueProfilerAttachmentGrant(plan.Id,120);
            var second=service.PrepareProfilerAttachment(identity.Id,0);
            Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartProfilerAttachment(second.Id,grant.Id,"wrong-plan")).Code);
            var expired=grant with { Id="expired",ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1) }; service.Catalog.Put(expired.Id,"grant",expired);
            Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>service.StartProfilerAttachment(plan.Id,expired.Id,"expired")).Code);
            Assert.Equal("invalid_request",Assert.Throws<LabException>(()=>service.IssueProfilerAttachmentGrant(plan.Id,120,2)).Code);
            using(var stream=new FileStream(binary,FileMode.Append)) stream.WriteByte(0);
            Assert.Equal("artifact_corrupt",Assert.Throws<LabException>(()=>service.StartProfilerAttachment(plan.Id,grant.Id,"changed-binary")).Code);
            Assert.Empty(service.Catalog.List("profiler_attachment_intent").Rows); ProfilerAttachmentIdentity.VerifyRuntime(identity,true);
        }
        finally { await Stop(target); }
    }

    [Fact,Case("UT-ATTACH-003","FR-003,FR-070,FR-072","M4","Integration")]
    public async Task MissingWorkerFailsBeforeSubmissionAndLeavesTargetAvailable()
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=await NativeAttachTests.Start(workspace.Root);
        try
        {
            await using var service=Service(workspace.Root,worker:Path.Combine(workspace.Root,"missing.dll"));
            var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity; var plan=service.PrepareProfilerAttachment(identity.Id,0);
            var grant=service.IssueProfilerAttachmentGrant(plan.Id,120); var operation=await ServiceTests.Wait(service,service.StartProfilerAttachment(plan.Id,grant.Id,"missing").Id);
            Assert.Equal(OperationState.Failed,operation.State); Assert.Equal("capability_unsupported",operation.ErrorCode);
            Assert.Equal("not_submitted",service.Catalog.Get<ProfilerAttachmentOutcome>(operation.ResultId!).State);
            Assert.Empty(service.Catalog.List("profiler_attachment_intent").Rows); ProfilerAttachmentIdentity.VerifyRuntime(identity,true);
            var next=service.Catalog.Accept("local","next","next","capture",identity.Id); service.Catalog.AcquireLease(identity.Id,next.Id); service.Catalog.Transition(next.Id,OperationState.Running); service.Catalog.Transition(next.Id,OperationState.Completed);
        }
        finally { await Stop(target); }
    }

    [Theory,InlineData(false),InlineData(true),Case("IT-ATTACH-002","FR-003,FR-070,FR-072","M4","Integration")]
    public async Task RestartReconcilesExactUnacknowledgedActivationOrOriginalProcessExitWithoutReload(bool exit)
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=await NativeAttachTests.Start(workspace.Root);
        try
        {
            ProfilerAttachmentPlan plan; Grant grant; Operation operation;
            await using(var service=Service(workspace.Root))
            {
                var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity; plan=service.PrepareProfilerAttachment(identity.Id,0);
                grant=service.IssueProfilerAttachmentGrant(plan.Id,120); service.Catalog.UseGrant(grant);
                operation=service.Catalog.Accept("local","lost-ack","lost-ack","profiler_attach",identity.Id);
                service.Catalog.AcquireLease(identity.Id,operation.Id); service.Catalog.Transition(operation.Id,OperationState.Running);
                service.Catalog.Put(operation.Id+"-intent","profiler_attachment_intent",new { operation_id=operation.Id,plan_id=plan.Id,plan_hash=plan.ContentHash,target=plan.Target });
                byte[] metadata=Encoding.UTF8.GetBytes(Wire.Serialize(new { schema_version=Wire.Version,target_id=identity.Id,
                    process_id=identity.ProcessId,start_time_utc_ticks=identity.StartTimeUtcTicks,workspace=workspace.Root }));
                new DiagnosticsClient(identity.ProcessId).AttachProfiler(TimeSpan.FromSeconds(10),Guid.Parse(ProfilerAttachmentIdentity.ProfilerId),plan.BinaryPath,metadata);
                // Leave the durable operation running: restart must treat the missing acknowledgement as uncertain.
            }
            if(exit) await Stop(target);
            await using var reopened=Service(workspace.Root); Assert.Equal(OperationState.Interrupted,reopened.Status(operation.Id).State);
            var blocked=reopened.Catalog.Accept("local","blocked","blocked","capture",plan.Target.Id);
            Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>reopened.Catalog.AcquireLease(plan.Target.Id,blocked.Id)).Code);
            var proof=await reopened.ReconcileProfilerAttachmentAsync(operation.Id,grant.Id); Assert.Equal(exit?"target_gone":"attached",proof.State);
            Assert.Equal(proof,await reopened.ReconcileProfilerAttachmentAsync(operation.Id,grant.Id));
            Assert.Equal("permission_denied",Assert.Throws<LabException>(()=>reopened.Catalog.UseGrant(grant)).Code);
            var next=reopened.Catalog.Accept("local","successor","successor","execution",plan.Target.Id);
            reopened.Catalog.AcquireLease(plan.Target.Id,next.Id); reopened.Catalog.Transition(next.Id,OperationState.Running);
            reopened.Catalog.Transition(next.Id,OperationState.Tainted);
            Assert.Equal(proof,await reopened.ReconcileProfilerAttachmentAsync(operation.Id,grant.Id));
            var later=reopened.Catalog.Accept("local","later","later","capture",plan.Target.Id);
            Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>reopened.Catalog.AcquireLease(plan.Target.Id,later.Id)).Code);
        }
        finally { await Stop(target); }
    }

    [Fact,Case("CT-ATTACH-001","FR-003,FR-060,FR-070","M4","Contract")]
    public async Task AgentCannotChooseLibraryPathOrIssueLoadingGrant()
    {
        using var workspace=new TestWorkspace(); await using var service=Service(workspace.Root); var tools=new LabTools(service);
        var response=await tools.InvokeAsync("lab.experiment","{\"action\":\"prepare_profiler_attachment\",\"target_id\":\"target\",\"expected_target_epoch\":\"0\",\"binary_path\":\"arbitrary.dll\"}");
        Assert.Equal("invalid_request",response.Code);
        response=await tools.InvokeAsync("lab.run","{\"action\":\"attach_profiler\",\"plan_id\":\"plan\",\"grant_id\":\"grant\",\"idempotency_key\":\"key\",\"binary_path\":\"arbitrary.dll\"}");
        Assert.Equal("invalid_request",response.Code);
        response=await tools.InvokeAsync("lab.experiment","{\"action\":\"issue_profiler_attachment_grant\",\"plan_id\":\"plan\"}"); Assert.Equal("invalid_request",response.Code);
    }

    [Theory,InlineData("lost_ack",true),InlineData("stall",true),InlineData("stall",false),
        Case("IT-ATTACH-003","FR-003,FR-063,FR-070,FR-072","M4","Integration")]
    public async Task CancellationAndDeadlineJoinWorkerAndPreserveUncertainActivation(string mode,bool cancel)
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); using var target=await NativeAttachTests.Start(workspace.Root);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.Root,"attachment-fault-mode"),mode);
            string worker=Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.ControllerFault","Release","net10.0","Tedd.DotNetLab.ControllerFault.dll");
            await using var service=Service(workspace.Root,worker:worker); var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
            var plan=service.PrepareProfilerAttachment(identity.Id,0); var grant=service.IssueProfilerAttachmentGrant(plan.Id,120);
            var operation=service.StartProfilerAttachment(plan.Id,grant.Id,"uncertain"); string ready=Path.Combine(workspace.Root,"attachment-fault-ready");
            using var readiness=new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while(!File.Exists(ready)) await Task.Delay(20,readiness.Token);
            int workerPid=Wire.Deserialize<JsonElement>(await File.ReadAllTextAsync(ready)).GetProperty("process_id").GetInt32();
            if(cancel) service.Cancel(operation.Id);
            var finished=await ServiceTests.Wait(service,operation.Id); Assert.Equal(OperationState.Tainted,finished.State);
            Assert.Equal("profiler_attach_unverified",finished.ErrorCode); Assert.Equal("unverified",service.Catalog.Get<ProfilerAttachmentOutcome>(finished.ResultId!).State);
            Assert.Equal(operation.Id,service.StartProfilerAttachment(plan.Id,grant.Id,"uncertain").Id);
            try { using var process=Process.GetProcessById(workerPid); Assert.True(process.HasExited); } catch(ArgumentException) { }
            var blocked=service.Catalog.Accept("local","blocked-after-cancel","blocked-after-cancel","capture",identity.Id);
            Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>service.Catalog.AcquireLease(identity.Id,blocked.Id)).Code);
            if(mode=="lost_ack")
            {
                var reconciled=await service.ReconcileProfilerAttachmentAsync(operation.Id,grant.Id); Assert.Equal("attached",reconciled.State);
                Assert.True((await service.ProfilerAsync(identity.Id,CancellationToken.None)).AttachmentComplete);
            }
            else
            {
                await Stop(target); Assert.Equal("target_gone",(await service.ReconcileProfilerAttachmentAsync(operation.Id,grant.Id)).State);
            }
        }
        finally { await Stop(target); }
    }

    [Theory,InlineData("source"),InlineData("target"),InlineData("plan"),InlineData("hash"),InlineData("state"),
        Case("UT-ATTACH-004","FR-070,FR-072","M4","Integration")]
    public void ReconciliationProofCannotClearUnrelatedSourceOrTarget(string mismatch)
    {
        using var workspace=new TestWorkspace(); using var catalog=new Catalog(workspace.Root);
        var target=new TargetIdentity("target",1,123,"runtime","10.0.12","x64");
        var operation=catalog.Accept("local","source","hash","profiler_attach",target.Id); catalog.AcquireLease(target.Id,operation.Id);
        catalog.Transition(operation.Id,OperationState.Running); catalog.Transition(operation.Id,OperationState.Tainted);
        catalog.Put(operation.Id+"-intent","profiler_attachment_intent",new { operation_id=operation.Id,plan_id="plan",plan_hash="plan-hash",target });
        var proof=new ProfilerAttachmentReconciliation("proof",operation.Id,"plan","plan-hash",target,"attached",DateTimeOffset.UtcNow);
        proof=mismatch switch { "source"=>proof with { SourceOperationId="other" },"target"=>proof with { Target=target with { StartTimeUtcTicks=124 } },
            "plan"=>proof with { PlanId="other" },"hash"=>proof with { PlanHash="other" },_=>proof with { State="not_attached" } };
        catalog.Put(proof.Id,"profiler_attachment_reconciliation",proof);
        Assert.Equal("recovery_unverified",Assert.Throws<LabException>(()=>catalog.CompleteProfilerAttachmentReconciliation(operation.Id,proof.Id)).Code);
        var next=catalog.Accept("local","next","next","capture",target.Id); Assert.Equal("target_tainted",Assert.Throws<LabException>(()=>catalog.AcquireLease(target.Id,next.Id)).Code);
    }

    [Fact,Case("CT-ATTACH-002","FR-070,FR-072","M4","Contract")]
    public async Task RejectedWorkerInvocationIsFramedAndDoesNotCreateCatalog()
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace();
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=workspace.Root };
        start.ArgumentList.Add(Host); start.ArgumentList.Add("--profiler-attach-worker");
        using var worker=Process.Start(TestWorkspace.PinRuntime(start))!; var errors=worker.StandardError.ReadToEndAsync();
        await Frames.WriteAsync(worker.StandardInput.BaseStream,new ProfilerAttachmentInvocation("invalid",new("target",1,1,"runtime","10.0.12","x64"),
            workspace.Root,ProfilerTests.ProfilerPath,"hash",1,"runtime"),CancellationToken.None); worker.StandardInput.Close();
        var reply=await Frames.ReadAsync<ProfilerAttachmentWorkerReply>(worker.StandardOutput.BaseStream,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10));
        await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); await errors;
        Assert.Equal("error",reply.Status); Assert.Equal("invalid_request",reply.Code); Assert.NotEqual(0,worker.ExitCode);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }
}
