using System.Diagnostics;
using System.Runtime.InteropServices;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Application;

public record ProfilerAttachmentPlan(string Id,TargetIdentity Target,string BinaryPath,string BinaryHash,long BinaryLength,string RuntimeHash,
    string ProfilerId,uint EventMaskLow,int MaximumDurationSeconds=30)
{
    public string ContentHash=>Wire.Hash(new { Id,Target,BinaryPath,BinaryHash,BinaryLength,RuntimeHash,ProfilerId,EventMaskLow,MaximumDurationSeconds });
}
public record ProfilerAttachmentOutcome(string Id,string OperationId,string PlanId,string PlanHash,TargetIdentity Target,string State,
    ProfilerHandshake? Handshake,string? Failure,DateTimeOffset ObservedAt);

public sealed partial class LabService
{
    public ProfilerAttachmentPlan PrepareProfilerAttachment(string targetId,long epoch)
    {
        ProfilerAttachmentIdentity.RequireHost(); var target=Target(targetId).Identity; targets.Verify(target,epoch);
        if(epoch!=0) throw new LabException("stale_epoch","Initial profiler attachment requires epoch zero.");
        string runtime=ProfilerAttachmentIdentity.VerifyRuntime(target,true,out string runtimeVersion);
        using var binary=ProfilerAttachmentIdentity.OpenBinary(approvedProfilerPath);
        var plan=new ProfilerAttachmentPlan(Wire.Id("attach-plan"),target with { RuntimeVersion=runtimeVersion,Architecture="x64" },
            approvedProfilerPath,ProfilerAttachmentIdentity.Hash(binary),binary.Length,runtime,ProfilerAttachmentIdentity.ProfilerId,0x40024);
        Catalog.Put(plan.Id,"profiler_attachment_plan",plan); return plan;
    }
    // Local CLI only; the grant authorizes the exact target session, plan, and approved native bytes.
    public Grant IssueProfilerAttachmentGrant(string planId,int seconds,int maximumOperations=1)
    {
        var plan=Record<ProfilerAttachmentPlan>(planId,"profiler_attachment_plan");
        if(seconds is <1 or >3600||maximumOperations!=1) throw new LabException("invalid_request","Profiler loading grants permit exactly one activation.");
        VerifyAttachmentPlan(plan);
        var grant=new Grant(Wire.Id("grant"),Catalog.Root,plan.Target.Id,plan.Target.StartTimeUtcTicks,Permission.AttachProfiler,
            DateTimeOffset.UtcNow.AddSeconds(seconds),1,plan.ContentHash);
        Catalog.Put(grant.Id,"grant",grant); return grant;
    }
    private void VerifyAttachmentPlan(ProfilerAttachmentPlan plan)
    {
        ProfilerAttachmentIdentity.RequireHost(); targets.Verify(plan.Target,plan.Target.Epoch);
        if(!plan.BinaryPath.Equals(approvedProfilerPath,StringComparison.OrdinalIgnoreCase)||plan.ProfilerId!=ProfilerAttachmentIdentity.ProfilerId||plan.EventMaskLow!=0x40024||
            plan.MaximumDurationSeconds!=30||plan.Target.Epoch!=0||!RuntimeSupport.Supports(plan.Target.RuntimeVersion)||plan.Target.Architecture!="x64")
            throw new LabException("invalid_plan","Attachment plan differs from the trusted profiler configuration.");
        using var binary=ProfilerAttachmentIdentity.OpenBinary(plan.BinaryPath);
        if(binary.Length!=plan.BinaryLength||ProfilerAttachmentIdentity.Hash(binary)!=plan.BinaryHash)
            throw new LabException("artifact_corrupt","Approved profiler bytes changed after attachment preparation.");
    }
    public Operation StartProfilerAttachment(string planId,string grantId,string key)
    {
        var plan=Record<ProfilerAttachmentPlan>(planId,"profiler_attachment_plan"); var grant=Record<Grant>(grantId,"grant");
        Policies.Authorize(grant,Catalog.Root,plan.Target,Permission.AttachProfiler,DateTimeOffset.UtcNow,plan.ContentHash);
        VerifyAttachmentPlan(plan);
        return Start("profiler_attach",plan.Target,key,Wire.Hash(new { planId,plan.ContentHash,grantId }),async(operation,cancellation)=>
        {
            bool submitted=false;
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds)); deadline.Token.ThrowIfCancellationRequested();
                Policies.Authorize(grant,Catalog.Root,plan.Target,Permission.AttachProfiler,DateTimeOffset.UtcNow,plan.ContentHash);
                VerifyAttachmentPlan(plan);
                if(ProfilerAttachmentIdentity.VerifyRuntime(plan.Target,true)!=plan.RuntimeHash) throw new LabException("target_identity_changed","Target runtime changed.");
                if(plan.MaximumDurationSeconds>Limits.MaximumDurationSeconds) throw new LabException("resource_limit","Attachment exceeds workspace duration policy.");
                if(!File.Exists(profilerAttachWorkerAssemblyPath)) throw new LabException("capability_unsupported","Packaged attachment worker is unavailable.");
                Catalog.UseGrant(grant);
                using var binary=ProfilerAttachmentIdentity.OpenBinary(plan.BinaryPath);
                if(ProfilerAttachmentIdentity.Hash(binary)!=plan.BinaryHash) throw new LabException("artifact_corrupt","Profiler changed before activation.");
                deadline.Token.ThrowIfCancellationRequested();
                Catalog.Put(operation.Id+"-intent","profiler_attachment_intent",new { operation_id=operation.Id,plan_id=plan.Id,plan_hash=plan.ContentHash,target=plan.Target });
                submitted=true;
                await InvokeAttachmentWorkerAsync(plan,deadline.Token);
                var handshake=await VerifyProfilerActivationAsync(plan,deadline.Token);
                var result=new ProfilerAttachmentOutcome(Wire.Id("attachment"),operation.Id,plan.Id,plan.ContentHash,plan.Target,"attached",handshake,null,DateTimeOffset.UtcNow);
                await RetainAttachmentOutcomeAsync(result); Catalog.Transition(operation.Id,OperationState.Completed,result.Id); return true;
            }
            catch(Exception error) when(error is not OutOfMemoryException)
            {
                string code=error is LabException lab?lab.Code:error is OperationCanceledException?"cancelled":"profiler_attach_failed";
                var result=new ProfilerAttachmentOutcome(Wire.Id("attachment"),operation.Id,plan.Id,plan.ContentHash,plan.Target,submitted?"unverified":"not_submitted",null,code,DateTimeOffset.UtcNow);
                await RetainAttachmentOutcomeAsync(result);
                Catalog.Transition(operation.Id,submitted?OperationState.Tainted:OperationState.Failed,result.Id,submitted?"profiler_attach_unverified":code);
                return !submitted;
            }
        });
    }
    private async Task RetainAttachmentOutcomeAsync(ProfilerAttachmentOutcome outcome)
    {
        var artifact=await Artifacts.WriteJsonAsync(outcome.Id,outcome,"windows-profiler-attachment/v1");
        Catalog.Put(artifact.Id,"artifact",artifact); Catalog.Pin(artifact.Id,"profiler_attachment");
        Catalog.Put(outcome.Id,"profiler_attachment",outcome);
        Catalog.Put(outcome.Id+"-manifest","profiler_attachment_manifest",new { artifact_id=artifact.Id });
    }
    private async Task<ProfilerHandshake> VerifyProfilerActivationAsync(ProfilerAttachmentPlan plan,CancellationToken cancellation)
    {
        VerifyAttachmentPlan(plan);
        if(ProfilerAttachmentIdentity.VerifyRuntime(plan.Target,false)!=plan.RuntimeHash) throw new LabException("target_identity_changed","Attached runtime changed.");
        ProfilerAttachmentIdentity.VerifyLoadedBinary(plan.Target,plan.BinaryPath);
        var handshake=await ProfilerAsync(plan.Target.Id,cancellation);
        if(!VerifiedWindowsAttach(handshake)||handshake.Workspace!=Catalog.Root||handshake.Epoch!=0)
            throw new LabException("profiler_attach_unverified","Attachment does not match the frozen activation identity and flags.");
        return handshake;
    }
    private async Task InvokeAttachmentWorkerAsync(ProfilerAttachmentPlan plan,CancellationToken cancellation)
    {
        string host=Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..","dotnet.exe"));
        if(!File.Exists(profilerAttachWorkerAssemblyPath)) throw new LabException("capability_unsupported","Packaged attachment worker is unavailable.");
        var start=new ProcessStartInfo(host) { UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=Catalog.Root };
        RuntimeSupport.AddRuntimeArguments(start); start.ArgumentList.Add(profilerAttachWorkerAssemblyPath); start.ArgumentList.Add("--profiler-attach-worker"); start.Environment.Clear();
        foreach(string name in new[]{"SystemRoot","WINDIR"}) if(Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name]=value;
        start.Environment["TEMP"]=Catalog.Root; start.Environment["TMP"]=Catalog.Root; start.Environment["DOTNET_EnableDiagnostics"]="0";
        using var worker=new Process { StartInfo=start }; ParserJob? job=null; Task? stderr=null; bool started=false;
        try
        {
            if(!worker.Start()) throw new LabException("profiler_attach_failed","Attachment worker did not start.");
            started=true;
            if(OperatingSystem.IsWindows()) job=new ParserJob(worker,268435456);
            stderr=DrainAttachmentErrorAsync(worker.StandardError);
            await Frames.WriteAsync(worker.StandardInput.BaseStream,new ProfilerAttachmentInvocation(Wire.Version,plan.Target,Catalog.Root,
                plan.BinaryPath,plan.BinaryHash,plan.BinaryLength,plan.RuntimeHash),cancellation); worker.StandardInput.Close();
            var reply=await Frames.ReadAsync<ProfilerAttachmentWorkerReply>(worker.StandardOutput.BaseStream,cancellation);
            await worker.WaitForExitAsync(cancellation);
            if(reply.SchemaVersion!=Wire.Version||reply.Status!="ok"||worker.ExitCode!=0) throw new LabException(reply.Code??"profiler_attach_failed","Attachment worker did not verify activation submission.");
        }
        finally
        {
            try
            {
                if(started)
                {
                    if(!worker.HasExited) worker.Kill(true);
                    await worker.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    if(stderr is not null) await stderr.WaitAsync(TimeSpan.FromSeconds(2));
                }
            }
            finally { if(OperatingSystem.IsWindows()) job?.Dispose(); }
        }
    }
    private static async Task DrainAttachmentErrorAsync(StreamReader error)
    {
        char[] buffer=new char[4096];
        while(await error.ReadAsync(buffer)>0) { } // Drain without retaining unbounded diagnostics.
    }
    public async Task<ProfilerAttachmentReconciliation> ReconcileProfilerAttachmentAsync(string operationId,string grantId,CancellationToken cancellation=default)
    {
        var operation=Catalog.GetOperation(operationId);
        if(operation.Kind!="profiler_attach"||operation.State is not (OperationState.Tainted or OperationState.Interrupted))
            throw new LabException("invalid_request","Only an uncertain profiler attachment can be reconciled.");
        var intent=Record<System.Text.Json.JsonElement>(operationId+"-intent","profiler_attachment_intent");
        var plan=Record<ProfilerAttachmentPlan>(intent.GetProperty("plan_id").GetString()!,"profiler_attachment_plan");
        var grant=Record<Grant>(grantId,"grant"); Policies.Authorize(grant,Catalog.Root,plan.Target,Permission.AttachProfiler,DateTimeOffset.UtcNow,plan.ContentHash);
        try
        {
            var committed=Record<System.Text.Json.JsonElement>(operationId+"-attachment-reconciled","profiler_attachment_reconciliation_commit");
            return Record<ProfilerAttachmentReconciliation>(committed.GetProperty("proof_id").GetString()!,"profiler_attachment_reconciliation");
        }
        catch(LabException error) when(error.Code=="record_not_found") { }
        string state;
        if(ProfilerAttachmentIdentity.OriginalTargetGone(plan.Target)) state="target_gone";
        else
        {
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation); deadline.CancelAfter(TimeSpan.FromSeconds(30));
            await VerifyProfilerActivationAsync(plan,deadline.Token); state="attached";
        }
        var proof=new ProfilerAttachmentReconciliation(Wire.Id("attach-reconciled"),operationId,plan.Id,plan.ContentHash,plan.Target,state,DateTimeOffset.UtcNow);
        Catalog.Put(proof.Id,"profiler_attachment_reconciliation",proof); Catalog.CompleteProfilerAttachmentReconciliation(operationId,proof.Id);
        var commit=Record<System.Text.Json.JsonElement>(operationId+"-attachment-reconciled","profiler_attachment_reconciliation_commit");
        return Record<ProfilerAttachmentReconciliation>(commit.GetProperty("proof_id").GetString()!,"profiler_attachment_reconciliation");
    }
}
