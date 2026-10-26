using System.Collections.Concurrent;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Artifacts;
using Tedd.DotNetLab.Collector.EventPipe;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Storage;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService : IAsyncDisposable
{
    public Catalog Catalog { get; }
    public ArtifactStore Artifacts { get; }
    public ResourceLimits Limits { get; }
    public WorkspaceContext Workspace { get; }
    private readonly ITargetRegistry targets;
    private readonly HarnessClient harness=new();
    private readonly object gate=new();
    private readonly ConcurrentDictionary<string,Job> jobs=new();
    public bool HasActiveOperations=>!jobs.IsEmpty||activityScheduler.State.UncertainMeasurements>0;
    private readonly Action<MutationEvent>? mutationJournalCommitted;
    private readonly Action<string>? normalBuildFileStaged;
    private readonly bool collectControllerResources;
    private readonly ITraceParserClient traceParser;
    private readonly ParserMeasurementGate activityScheduler;
    private readonly string approvedProfilerPath,profilerAttachWorkerAssemblyPath;
    private sealed class Job
    {
        public CancellationTokenSource Cancellation { get; }=new();
        public Task Work { get; set; }=Task.CompletedTask; public TaskCompletionSource Stop { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public LabService(string workspace,ResourceLimits? limits=null,ITargetRegistry? targetRegistry=null,Action<MutationEvent>? mutationJournalCommitted=null,bool collectControllerResources=true,ITraceParserClient? traceParser=null,ParserMeasurementGate? activityScheduler=null,Action<string>? normalBuildFileStaged=null,string? approvedProfilerPath=null,string? profilerAttachWorkerAssemblyPath=null)
    {
        if(limits is not null) Policies.Validate(limits);
        targets=targetRegistry??new Targets();
        this.mutationJournalCommitted=mutationJournalCommitted;
        this.normalBuildFileStaged=normalBuildFileStaged;
        this.approvedProfilerPath=Path.GetFullPath(approvedProfilerPath??Path.Combine(AppContext.BaseDirectory,"Tedd.DotNetLab.Profiler.dll"));
        this.profilerAttachWorkerAssemblyPath=Path.GetFullPath(profilerAttachWorkerAssemblyPath??Path.Combine(AppContext.BaseDirectory,"Tedd.DotNetLab.Host.dll"));
        this.collectControllerResources=collectControllerResources;
        this.traceParser=traceParser??new TraceWorkerClient();
        Catalog=new(workspace,backupDiskReserveBytes:limits?.DiskReserveBytes??new ResourceLimits().DiskReserveBytes);
        try
        {
            Workspace=Catalog.ConfigureWorkspace(limits); Limits=Workspace.Policy.Limits;
            this.activityScheduler=activityScheduler??new ParserMeasurementGate(maximumQueuedActivities:Limits.MaximumQueuedActivities);
            if(this.activityScheduler.MaximumQueuedActivities!=Limits.MaximumQueuedActivities) throw new LabException("invalid_plan","Injected activity scheduler differs from the persisted workspace queue policy.");
            Artifacts=new(workspace,id=>Catalog.Retention(id).DeletionState is not null);
            Catalog.Recover(); RecoverArtifactDeletions();
        }
        catch { Catalog.Dispose(); throw; }
    }
    private void Upsert<T>(string id,string kind,T value)
    {
        NotebookRecord? previous=null;
        try { previous=Catalog.Get(id); }
        catch(LabException e) when(e.Code=="record_not_found") { }
        if(previous?.Hash!=Wire.Hash(value)) Catalog.Put(id,kind,value,previous?.Revision);
    }
    public IReadOnlyList<TargetInfo> Discover()
    {
        var result=new List<TargetInfo>();
        foreach(var target in targets.Discover())
        {
            TargetInfo current=target;
            try
            {
                var saved=Catalog.Get<TargetInfo>(target.Identity.Id);
                if(saved.Identity.StartTimeUtcTicks==target.Identity.StartTimeUtcTicks) current=saved;
            }
            catch(LabException e) when(e.Code=="record_not_found") { }
            Upsert(current.Identity.Id,"target",current); result.Add(current);
        }
        return result;
    }
    public TargetInfo Target(string id)
    {
        if(Catalog.Get(id).Kind!="target") throw new LabException("invalid_request","Expected a target ID.");
        return Catalog.Get<TargetInfo>(id);
    }
    public async Task<HarnessHandshake> ScenariosAsync(string targetId,CancellationToken cancellation)
    {
        var target=Target(targetId);
        targets.Verify(target.Identity,target.Identity.Epoch);
        var handshake=await harness.HandshakeAsync(target.Identity,cancellation);
        if(handshake.Target.StartTimeUtcTicks!=target.Identity.StartTimeUtcTicks||handshake.Target.ProcessId!=target.Identity.ProcessId)
            throw new LabException("target_identity_changed","Harness handshake mismatch.");
        var capabilities=new Dictionary<string,Capability>(target.Capabilities)
        {
            ["harness"]=new(handshake.Tainted?CapabilityState.Blocked:CapabilityState.Supported,
                handshake.Tainted?"Target is tainted.":"Registered scenario handshake verified.")
        };
        Upsert(targetId,"target",target with { Identity=handshake.Target,Capabilities=capabilities });
        return handshake;
    }
    // This entry point is for the local CLI. It is not an MCP tool.
    public Grant IssueGrant(string targetId,Permission permission,int seconds,int maximumOperations)
    {
        if(permission is not (Permission.Observe or Permission.ExecuteRegisteredScenario or Permission.InspectLiveCode))
            throw new LabException("capability_unsupported","Live inspection and mutation grants require a certified profiler.");
        if(seconds is <1 or >3600||maximumOperations is <1 or >100000)
            throw new LabException("invalid_request","Grant budget is invalid.");
        var target=Target(targetId).Identity;
        if(permission==Permission.InspectLiveCode&&Target(targetId).Capabilities["native_inspection"].State!=CapabilityState.Supported)
            throw new LabException("capability_unsupported","Verify the profiler qualification before issuing an inspection grant.");
        targets.Verify(target,target.Epoch);
        var grant=new Grant(Wire.Id("grant"),Catalog.Root,target.Id,target.StartTimeUtcTicks,permission,
            DateTimeOffset.UtcNow.AddSeconds(seconds),maximumOperations);
        Catalog.Put(grant.Id,"grant",grant);
        return grant;
    }
    private Grant Authorize(string grantId,TargetIdentity target,Permission permission)
    {
        if(Catalog.Get(grantId).Kind!="grant") throw new LabException("permission_denied","Expected an issued grant ID.");
        var grant=Catalog.Get<Grant>(grantId);
        Policies.Authorize(grant,Catalog.Root,target,permission,DateTimeOffset.UtcNow);
        targets.Verify(target,target.Epoch);
        return grant;
    }
    public Hypothesis Hypothesis(string mechanism,string prediction,IReadOnlyList<string> evidence,
        string? id=null,long? expectedRevision=null,string state="proposed")
    {
        if(string.IsNullOrWhiteSpace(mechanism)||string.IsNullOrWhiteSpace(prediction)||
            mechanism.Length>8192||prediction.Length>8192||!new[]{"proposed","planned","testing","supported","not_supported","inconclusive","superseded","abandoned"}.Contains(state))
            throw new LabException("invalid_request","Invalid hypothesis.");
        foreach(string item in evidence) Catalog.Get(item);
        var result=new Hypothesis(id??Wire.Id("hyp"),mechanism,prediction,state,evidence);
        Catalog.Put(result.Id,"hypothesis",result,expectedRevision);
        return result;
    }
    public Candidate Candidate(string name,string buildIdentity,IReadOnlyList<string> components)
    {
        if(string.IsNullOrWhiteSpace(name)||name.Length>256||string.IsNullOrWhiteSpace(buildIdentity)||buildIdentity.Length>512||components.Count>100)
            throw new LabException("invalid_request","Invalid candidate identity.");
        foreach(string component in components)
            if(Catalog.Get(component).Kind!="candidate") throw new LabException("invalid_request","Unknown candidate component.");
        var result=new Candidate(Wire.Id("candidate"),name,buildIdentity,components);
        Catalog.Put(result.Id,"candidate",result);
        return result;
    }
    public ExperimentPlan Plan(ExperimentPlan plan,string? baselineId=null,string? baselineAliasId=null,string? compilationControlId=null)
    {
        plan=FreezeDiagnosticWindows(plan);
        plan=FreezeCompilationControl(plan,compilationControlId);
        Policies.Validate(plan);
        plan=FreezeBaseline(plan,baselineId,baselineAliasId);
        if(plan.Factorial is { } factorial)
        {
            Record<Candidate>(factorial.ComponentAId,"candidate"); Record<Candidate>(factorial.ComponentBId,"candidate");
            var combined=Record<Candidate>(factorial.CombinationId,"candidate");
            if(combined.Components.Count!=2||!combined.Components.Contains(factorial.ComponentAId)||!combined.Components.Contains(factorial.ComponentBId))
                throw new LabException("invalid_plan","The registered joint candidate must compose exactly the two declared components.");
        }
        if(plan.FreshNormalBuild)
            throw new LabException("invalid_plan","Fresh normal builds require a registered confirmation plan and owned process launches.");
        if(plan.Analysis.FamilySize<plan.Variants.Count-1)
            throw new LabException("invalid_plan","Comparison family does not cover all candidate arms.");
        plan=plan with { Id=Wire.Id("plan") };
        Catalog.Put(plan.Id,"plan",plan);
        return plan;
    }
    public WorkItem WorkItem(string question,int priority,IReadOnlyList<string> dependencies,IReadOnlyList<string> evidence,
        string? id=null,long? expectedRevision=null,string state="todo")
    {
        if(string.IsNullOrWhiteSpace(question)||question.Length>8192||priority is <0 or >100||
            dependencies.Count>100||!new[]{"todo","active","done","blocked","abandoned"}.Contains(state))
            throw new LabException("invalid_request","Invalid work item.");
        string identity=id??Wire.Id("work");
        var visited=new HashSet<string>();
        void Visit(string node,int depth)
        {
            if(node==identity) throw new LabException("dependency_cycle","Work-item dependencies form a cycle.");
            if(depth>64||visited.Count>1000) throw new LabException("resource_limit","Dependency graph exceeds policy.");
            if(!visited.Add(node)) return;
            if(Catalog.Get(node).Kind!="work_item") throw new LabException("invalid_request","Dependency is not a work item.");
            foreach(string child in Catalog.Get<WorkItem>(node).Dependencies) Visit(child,depth+1);
        }
        foreach(string dependency in dependencies) Visit(dependency,0);
        foreach(string item in evidence) Catalog.Get(item);
        var result=new WorkItem(identity,question,priority,state,dependencies,evidence);
        Catalog.Put(result.Id,"work_item",result,expectedRevision);
        return result;
    }
    public ManualDecision Decision(string comparisonId,string action,string rationale)
    {
        if(Catalog.Get(comparisonId).Kind!="comparison"||action.Length>256||rationale.Length>8192)
            throw new LabException("invalid_request","Invalid decision.");
        var decision=new ManualDecision(Wire.Id("decision"),comparisonId,action,rationale);
        Catalog.Put(decision.Id,"decision",decision);
        return decision;
    }
    public Operation StartCapture(string targetId,long epoch,string profile,int seconds,string grantId,string key)
    {
        var target=Target(targetId).Identity;
        targets.Verify(target,epoch);
        var grant=AuthorizeTargetOrScratchAsync(grantId,target,Permission.Observe,null,CancellationToken.None).GetAwaiter().GetResult();
        var resolved=Profiles.Resolve(profile);
        if(resolved.Providers.Count==0||seconds<1||seconds>Limits.MaximumDurationSeconds)
            throw new LabException("invalid_plan","Invalid capture profile or duration.");
        string hash=Wire.Hash(new { targetId,epoch,profile_hash=resolved.Hash,seconds,grantId });
        return Start("capture",target,key,hash,async(op,token)=>
        {
            Capture capture;
            using(var permit=await activityScheduler.EnterMeasurementAsync(token))
            {
                await ReauthorizeTargetOrScratchAsync(grant,target,Permission.Observe,null,token);
                Catalog.UseGrant(grant);
                capture=await new EventPipeCollector(Catalog,Artifacts,targets,new EventPipeSessionFactory(),Limits)
                    .CaptureAsync(op.Id,target,profile,seconds,token,stopRequested:jobs[op.Id].Stop.Task);
            }
            if(capture.ArtifactId is not null) await AnalyzeSafelyAsync(capture.Id,token);
            Catalog.Transition(op.Id,capture.State==CaptureState.Sealed?OperationState.Completed:OperationState.Failed,capture.Id,capture.Reason);
            return true;
        });
    }
    public async Task<Operation> StartExecutionAsync(string targetId,long epoch,string planId,string grantId,
        string key,CancellationToken cancellation,string? observationGrantId=null)
    {
        var initial=Target(targetId).Identity;
        targets.Verify(initial,epoch);
        var grant=await AuthorizeTargetOrScratchAsync(grantId,initial,Permission.ExecuteRegisteredScenario,null,cancellation);
        if(Catalog.Get(planId).Kind!="plan") throw new LabException("invalid_request","Expected frozen plan ID.");
        var plan=Catalog.Get<ExperimentPlan>(planId); Policies.Validate(plan);
        if(plan.Baseline is not null) await VerifyBaselineAsync(plan.Baseline,cancellation);
        bool hasCapture=Profiles.Resolve(plan.Profile).Providers.Count>0;
        Grant? observationGrant=!hasCapture?null:await AuthorizeTargetOrScratchAsync(observationGrantId??throw new LabException("permission_denied","Diagnostic execution requires a separate observation grant."),initial,Permission.Observe,null,cancellation);
        var handshake=await ScenariosAsync(targetId,cancellation);
        if(handshake.Target.Epoch!=epoch) throw new LabException("stale_epoch","Scenario epoch differs from the frozen execution request.");
        if(handshake.Tainted) throw new LabException("target_tainted","Harness is tainted.");
        if(plan.Profile=="quiet-validation"&&handshake.ProbeHelper?.Active==true)
            throw new LabException("observer_mismatch","Quiet validation requires inactive selected probes.");
        if(plan.Profile=="targeted-probes"&&(handshake.ProbeHelper?.Active!=true||string.IsNullOrEmpty(handshake.ProbeHelper.ActivePlanHash)))
            throw new LabException("profile_requires_probes","Targeted-probes execution requires a granted and verified installed probe plan.");
        Tedd.DotNetLab.Transport.Local.ProfilerHandshake? qualifiedNative=null;
        if(OperatingSystem.IsLinux()&&handshake.Runtime?.NativeProfilerPresent==true)
        {
            var native=await ProfilerAsync(targetId,cancellation);
            if(Target(targetId).Capabilities["timing_probes"].State==CapabilityState.Supported) qualifiedNative=native;
        }
        ExecutionQualification.Validate(handshake,plan.Profile,plan.CompilationControl is not null,qualifiedNative,plan.TargetRuntimeVersion);
        var descriptor=handshake.Scenarios.SingleOrDefault(s=>s.Id==plan.ScenarioId)
            ?? throw new LabException("scenario_not_found","Scenario is not registered.");
        if(descriptor.Version!=plan.ScenarioVersion||descriptor.InputHash!=plan.InputHash||plan.Variants.Any(v=>!descriptor.Variants.Contains(v)))
            throw new LabException("invalid_plan","Frozen scenario/input/variants differ from the target.");
        var control=plan.CompilationControl is null?null:await VerifyCompilationControlAsync(plan.CompilationControl,cancellation);
        var compilation=await CompilationRegimeAsync(handshake.Target,handshake.Runtime,plan,descriptor,control,cancellation);
        string observer=compilation is null?Wire.Serialize(new { external="other_observers_unknown",probe=handshake.ProbeHelper }):
            Wire.Serialize(new { external="other_observers_unknown",probe=handshake.ProbeHelper,compilation });
        VerifyBaselineTarget(plan,handshake,descriptor,observer,compilation);
        if(plan.Profile=="targeted-probes"&&!(descriptor.MutationScopes?.Contains(handshake.ProbeHelper!.MethodScope!)??false))
            throw new LabException("scenario_scope_mismatch","Targeted-probes scenario does not declare the selected method scope.");
        string hash=Wire.Hash(new { targetId,epoch,plan_id=planId,plan_hash=Wire.Hash(plan),grantId,observationGrantId });
        return Start("execution",handshake.Target,key,hash,async(op,token)=>
        {
            var execution=new ExecutionRecord(Wire.Id("exec"),plan.Id,Wire.Hash(plan),handshake.Target,"running",[],DateTimeOffset.UtcNow);
            long revision=Catalog.Put(execution.Id,"execution",execution).Revision;
            var runIds=new List<string>();
            var random=new DeterministicRandom(plan.Seed);
            var end=DateTimeOffset.UtcNow.AddSeconds(plan.MaximumDurationSeconds);
            using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(plan.MaximumDurationSeconds));
            Task<Capture>? captureTask=null;
            var captureStop=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            string? captureId=null;
            using var executionPermit=await activityScheduler.EnterMeasurementAsync(deadline.Token);
            try
            {
                if(hasCapture)
                {
                    await ReauthorizeTargetOrScratchAsync(observationGrant!,handshake.Target,Permission.Observe,handshake,deadline.Token); Catalog.UseGrant(observationGrant!);
                    var collecting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    captureTask=new EventPipeCollector(Catalog,Artifacts,targets,new EventPipeSessionFactory(),Limits)
                        .CaptureAsync(op.Id,handshake.Target,plan.Profile,plan.MaximumDurationSeconds,deadline.Token,
                            stopRequested:captureStop.Task,collecting:collecting);
                    await collecting.Task.WaitAsync(TimeSpan.FromSeconds(10),deadline.Token);
                }
                for(int block=0;block<plan.Blocks;block++)
                {
                    string blockId=execution.Id+"-block-"+block;
                    var order=random.Shuffle(plan.Variants);
                    for(int arm=0;arm<order.Length;arm++)
                    {
                        deadline.Token.ThrowIfCancellationRequested();
                        targets.Verify(handshake.Target,epoch);
                        var currentHandshake=await harness.HandshakeAsync(handshake.Target,deadline.Token);
                        if(currentHandshake.Target!=handshake.Target||currentHandshake.Tainted||
                            Wire.Hash(currentHandshake.ProbeHelper)!=Wire.Hash(handshake.ProbeHelper)||Wire.Hash(currentHandshake.Runtime)!=Wire.Hash(handshake.Runtime))
                            throw new LabException("run_identity_changed","Build, runtime, observer, or target identity changed during the frozen execution.");
                        var beforeCompilation=await CompilationRegimeAsync(handshake.Target,currentHandshake.Runtime,plan,descriptor,control,deadline.Token);
                        if(Wire.Hash(beforeCompilation)!=Wire.Hash(compilation)) throw new LabException("run_identity_changed","Compilation regime changed before measurement.");
                        VerifyBaselineTarget(plan,currentHandshake,descriptor,observer,compilation);
                        await ReauthorizeTargetOrScratchAsync(grant,handshake.Target,Permission.ExecuteRegisteredScenario,currentHandshake,deadline.Token);
                        Catalog.UseGrant(grant);
                        string runId=Wire.Id("run");
                        var targetGrant=BindTargetGrant(grant,handshake.Target);
                        var request=new RunRequest(execution.Id,runId,blockId,plan.ScenarioId,order[arm],
                            plan.OperationsPerRun,plan.WarmupBatches,true,!descriptor.IsAsync,epoch,handshake.Target,targetGrant,end);
                        var measurement=await ExecuteMeasuredAsync(request,deadline.Token,executionPermit);
                        Policies.Validate(measurement);
                        if(!measurement.Correct||!measurement.Quiescent||measurement.ExecutionId!=execution.Id||
                            measurement.RunId!=runId||measurement.BlockId!=blockId||measurement.Variant!=order[arm]||
                            measurement.CompletedOperations!=plan.OperationsPerRun||measurement.InputHash!=plan.InputHash||
                            measurement.ScenarioVersion!=plan.ScenarioVersion)
                            throw new LabException("scenario_failed","Measurement identity, correctness, or completed work differs from the frozen request.");
                        var after=await harness.HandshakeAsync(handshake.Target,deadline.Token);
                        if(after.Target!=handshake.Target||after.Tainted||Wire.Hash(after.Runtime)!=Wire.Hash(currentHandshake.Runtime)||
                            Wire.Hash(after.ProbeHelper)!=Wire.Hash(currentHandshake.ProbeHelper))
                            throw new LabException("run_identity_changed","Execution changed its retained runtime or observer identity.");
                        var afterCompilation=await CompilationRegimeAsync(handshake.Target,after.Runtime,plan,descriptor,control,deadline.Token);
                        if(Wire.Hash(afterCompilation)!=Wire.Hash(compilation)) throw new LabException("run_identity_changed","Compilation regime changed during measurement.");
                        var run=new RunRecord(runId,execution.Id,plan.Id,Wire.Hash(plan),block,arm,handshake.Target,
                            descriptor,Profiles.Resolve(plan.Profile).Hash,observer,measurement,handshake.Runtime,compilation);
                        Catalog.Put(runId,"run",run);
                        var artifact=await Artifacts.WriteJsonAsync(op.Id,measurement,"harness/1");
                        Upsert(artifact.Id,"artifact",artifact);
                        Catalog.Put(runId+"-measurement","measurement_artifact",new { run_id=runId,artifact_id=artifact.Id });
                        runIds.Add(runId);
                        execution=execution with { RunIds=runIds.ToArray() };
                        revision=Catalog.Put(execution.Id,"execution",execution,revision).Revision;
                    }
                }
                captureStop.TrySetResult();
                AnalysisIndex? executionAnalysis=null;
                if(captureTask is not null)
                {
                    var capture=await captureTask;
                    captureId=capture.Id;
                    executionPermit.Dispose();
                    executionAnalysis=await AnalyzeSafelyAsync(captureId,token);
                }
                execution=execution with { State="completed",RunIds=runIds.ToArray(),EndedAt=DateTimeOffset.UtcNow,CaptureId=captureId,AnalysisId=executionAnalysis?.Id };
                Catalog.Put(execution.Id,"execution",execution,revision);
                Catalog.Transition(op.Id,OperationState.Completed,execution.Id);
                return true;
            }
            catch(Exception e) when(e is not OutOfMemoryException)
            {
                captureStop.TrySetResult();
                if(captureTask is not null)
                {
                    var capture=await captureTask;
                    captureId=capture.Id;
                }
                string code=e is LabException error?error.Code:e is OperationCanceledException?"target_tainted":"execution_failed";
                Catalog.Put(execution.Id,"execution",execution with { State="failed",RunIds=runIds.ToArray(),
                    EndedAt=DateTimeOffset.UtcNow,Failure=code,CaptureId=captureId },revision);
                bool quiescent=e is LabException failure && failure.Code is "scenario_failed" or "permission_denied" or "invalid_measurement";
                Catalog.Transition(op.Id,quiescent?OperationState.Failed:OperationState.Tainted,execution.Id,code);
                return quiescent;
            }
        });
    }
    private Operation Start(string kind,TargetIdentity target,string key,string hash,Func<Operation,CancellationToken,Task<bool>> action,
        string? recoverySourceOperationId=null)
    {
        lock(gate)
        {
            var operation=Catalog.Accept("local",key,hash,kind,target.Id,Workspace.Policy.Id);
            if(operation.State!=OperationState.Accepted||jobs.ContainsKey(operation.Id)) return operation;
            if(jobs.Count>=Limits.MaximumConcurrentOperations)
            {
                Catalog.Transition(operation.Id,OperationState.Failed,error:"resource_limit");
                throw new LabException("resource_limit","Workspace concurrent-operation cap reached.");
            }
            try
            {
                if(recoverySourceOperationId is null) Catalog.AcquireLease(target.Id,operation.Id);
                else Catalog.AcquireMutationRecoveryLease(target.Id,operation.Id,recoverySourceOperationId);
            }
            catch(LabException e) { Catalog.Transition(operation.Id,OperationState.Failed,error:e.Code); throw; }
            var job=new Job();
            jobs[operation.Id]=job;
            Catalog.Transition(operation.Id,OperationState.Running);
            job.Work=Task.Run(async()=>
            {
                bool quiescent=kind is not ("execution" or "mutation" or "mutation_recovery" or "individual_latencies" or "profiler_attach");
                try { RecordControllerResources(operation.Id,"before",true); quiescent=await action(operation,job.Cancellation.Token); }
                catch(Exception e) when(e is not OutOfMemoryException)
                {
                    string code=e is LabException error?error.Code:e is OperationCanceledException?"cancelled":"operation_failed";
                    if(kind=="stack_analysis")
                        Catalog.Put(operation.Id+"-failure","stack_analysis_failure",new { code,error_type=e.GetType().Name,
                            message=e.Message.Length>4096?e.Message[..4096]:e.Message });
                    if(!Lifecycle.Terminal(Catalog.GetOperation(operation.Id).State))
                        Catalog.Transition(operation.Id,kind is "execution" or "mutation" or "mutation_recovery" or "individual_latencies" or "profiler_attach"?OperationState.Tainted:OperationState.Failed,error:code);
                }
                finally
                {
                    RecordControllerResources(operation.Id,"after",quiescent);
                    Catalog.ReleaseLease(target.Id,operation.Id,quiescent);
                    jobs.TryRemove(operation.Id,out _);
                    job.Cancellation.Dispose();
                }
            });
            return Catalog.GetOperation(operation.Id);
        }
    }
    public Operation Status(string operationId) => Catalog.GetOperation(operationId);
    public Operation Cancel(string operationId)
    {
        lock(gate)
        {
            var operation=Status(operationId);
            if(Lifecycle.Terminal(operation.State)) return operation;
            if(!jobs.TryGetValue(operationId,out var job)) throw new LabException("operation_interrupted","Operation has no active worker.");
            Catalog.Transition(operationId,OperationState.CancellationRequested);
            job.Cancellation.Cancel();
            return Status(operationId);
        }
    }
    public Operation StopCapture(string operationId)
    {
        lock(gate)
        {
            var operation=Status(operationId);
            if(operation.Kind!="capture") throw new LabException("invalid_request","Expected a capture operation.");
            if(Lifecycle.Terminal(operation.State)) return operation;
            if(!jobs.TryGetValue(operationId,out var job)) throw new LabException("operation_interrupted","Operation has no active worker.");
            job.Stop.TrySetResult();
            return Status(operationId);
        }
    }
    private async Task<AnalysisIndex?> AnalyzeSafelyAsync(string captureId,CancellationToken cancellation=default)
    {
        try { return await AnalyzeAsync(captureId,cancellation); }
        catch(Exception e) when(e is not OutOfMemoryException)
        {
            Catalog.Put(Wire.Id("analysis-failure"),"analysis_failure",new { capture_id=captureId,
                code=e is LabException error?error.Code:"analysis_failed" });
            return null;
        }
    }
    public async Task<AnalysisIndex> AnalyzeAsync(string captureId,CancellationToken cancellation=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(TimeSpan.FromSeconds(Math.Min(Limits.MaximumParserDurationSeconds,Limits.MaximumDurationSeconds)));
        using var permit=await activityScheduler.EnterParserAsync(deadline.Token);
        var capture=Catalog.Get<Capture>(captureId);
        if(capture.ArtifactId is null) throw new LabException("capture_incomplete","Capture has no sealed bytes.");
        var artifact=Catalog.Get<Artifact>(capture.ArtifactId);
        var summary=await ParseTraceAsync(capture,artifact,Wire.Id("trace-parse"),deadline.Token);
        var derived=await Artifacts.WriteJsonAsync(summary.Id,summary,TraceAnalyzer.Version,cancellation:deadline.Token);
        Upsert(derived.Id,"artifact",derived);
        var index=new AnalysisIndex(summary.Id,capture.Id,derived.Id,summary.ParserVersion,summary.Quality);
        Catalog.Put(index.Id,"analysis",index);
        Catalog.Put(index.Id+"-capture","capture_analysis",new { capture_id=captureId,analysis_id=index.Id });
        return index;
    }
    public async Task<TraceSummary> AnalysisAsync(string analysisId)
    {
        var index=Catalog.Get<AnalysisIndex>(analysisId);
        var artifact=Catalog.Get<Artifact>(index.ArtifactId);
        if(artifact.Length>16777216) throw new LabException("resource_limit","Analysis payload exceeds memory policy.");
        return Wire.Deserialize<TraceSummary>(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact)));
    }
    public async Task<MetricResult> MetricAsync(string analysisId,string metric)
    {
        var summary=await AnalysisAsync(analysisId);
        var capture=Catalog.Get<Capture>(summary.CaptureId);
        try { await Artifacts.VerifyAsync(Catalog.Get<Artifact>(summary.ArtifactId).Hash,Catalog.Get<Artifact>(summary.ArtifactId).Length); }
        catch(LabException e) when(e.Code=="artifact_expired")
        { return new(metric,Availability.ArtifactExpired,null,"unknown","capture_process","Raw evidence expired."); }
        return new TraceAnalyzer().Metric(summary,capture,metric);
    }
    private IReadOnlyList<Comparison> CompareElapsed(string executionId)
    {
        var execution=Catalog.Get<ExecutionRecord>(executionId);
        var plan=Catalog.Get<ExperimentPlan>(execution.PlanId);
        if(execution.State!="completed") throw new LabException("not_comparable","Execution did not complete its frozen sample budget.");
        if(execution.PlanHash!=Wire.Hash(plan)||execution.RunIds.Count!=plan.Blocks*plan.Variants.Count)
            throw new LabException("not_comparable","Execution/plan integrity or sample budget differs.");
        var runs=execution.RunIds.Select(Catalog.Get<RunRecord>).ToArray();
        var result=new List<Comparison>();
        foreach(string candidate in plan.Variants.Skip(1))
        {
            var pairs=new List<CostPair>();
            var blockers=new List<string>();
            for(int block=0;block<plan.Blocks;block++)
            {
                var b=runs.Single(r=>r.BlockOrdinal==block&&r.Measurement.Variant==plan.Variants[0]);
                var c=runs.Single(r=>r.BlockOrdinal==block&&r.Measurement.Variant==candidate);
                if(b.PlanHash!=execution.PlanHash||c.PlanHash!=execution.PlanHash||b.ProfileHash!=c.ProfileHash||
                    b.Target.RuntimeVersion!=c.Target.RuntimeVersion||b.ObserverState!=c.ObserverState||Wire.Hash(b.Compilation)!=Wire.Hash(c.Compilation)||
                    !b.Measurement.Correct||!c.Measurement.Correct||!b.Measurement.Quiescent||!c.Measurement.Quiescent)
                    blockers.Add("run_fingerprint_or_correctness_mismatch");
                double Value(RunRecord r)=>plan.PrimaryMetric=="scenario.elapsed_ns"?r.Measurement.ElapsedNanoseconds:r.Measurement.NanosecondsPerOperation;
                pairs.Add(new(b.Measurement.BlockId,b.Measurement.BlockId,Value(b),Value(c),b.Id,c.Id));
            }
            string comparisonId="cmp-"+Wire.Hash(new { execution_id=executionId,candidate,algorithm=StatisticalEngine.Version,plan=execution.PlanHash,
                inputs=runs.Select(Wire.Hash).ToArray() });
            try { result.Add(Catalog.Get<Comparison>(comparisonId)); continue; }
            catch(LabException e) when(e.Code=="record_not_found") { }
            // A same-process plan cannot itself establish fresh-process promotion.
            var comparison=StatisticalEngine.Paired(pairs,plan.Analysis,plan.IndependentBlocks,false,blockers)
                with { Id=comparisonId };
            Catalog.Put(comparisonId,"comparison",comparison);
            foreach(string runId in comparison.RunIds)
            {
                var reference=Catalog.Get<JsonElement>(runId+"-measurement");
                Catalog.Pin(reference.GetProperty("artifact_id").GetString()!,"comparison_input");
            }
            result.Add(comparison);
        }
        return result;
    }
    public async Task<string> ArtifactSliceAsync(string artifactId,long offset,int count)
    {
        if(offset<0||count is <1 or >32768) throw new LabException("resource_limit","Artifact read budget is invalid.");
        var artifact=Catalog.Get<Artifact>(artifactId);
        if(offset>artifact.Length) throw new LabException("invalid_cursor","Offset exceeds immutable artifact length.");
        await using var file=new FileStream(await Artifacts.VerifiedPathAsync(artifact),FileMode.Open,FileAccess.Read,FileShare.Read);
        file.Position=offset;
        byte[] bytes=new byte[(int)Math.Min(count,artifact.Length-offset)];
        await file.ReadExactlyAsync(bytes);
        return Wire.Serialize(new { artifact_id=artifactId,offset,encoding="base64",data=Convert.ToBase64String(bytes),
            truncated=offset+bytes.Length<artifact.Length,next_offset=offset+bytes.Length<artifact.Length?(long?)(offset+bytes.Length):null });
    }
    public async ValueTask DisposeAsync()
    {
        Job[] active=jobs.Values.ToArray();
        foreach(var job in active) { try { job.Cancellation.Cancel(); } catch(ObjectDisposedException) { } }
        activityScheduler.Close();
        await Task.WhenAll(active.Select(j=>j.Work)).WaitAsync(TimeSpan.FromSeconds(45));
        await traceParser.DisposeAsync();
        Catalog.Dispose();
    }
}
