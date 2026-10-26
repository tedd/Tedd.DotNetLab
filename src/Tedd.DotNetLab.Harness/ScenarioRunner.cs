using System.Collections.Concurrent;
using System.Diagnostics;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Harness;

public sealed class ScenarioRunner(ScenarioRegistry registry, TargetIdentity target, string workspace,
    IScenarioDispatcher? applicationDispatcher = null)
{
    private readonly SemaphoreSlim lease = new(1,1);
    private readonly ConcurrentDictionary<string,int> grantUses = new();
    private volatile bool tainted;
    private bool ownedProcess;
    private int ownedExecutionCount;
    internal void EnableOwnedProcess()=>ownedProcess=true;
    public bool Tainted => tainted;
    public async Task<object> ControlAsync(Func<object> action,CancellationToken cancellation)
    {
        if(tainted) throw new LabException("target_tainted","Harness quiescence is unknown.");
        if(!await lease.WaitAsync(0,cancellation)) throw new LabException("lease_conflict","Registered execution is active.");
        try { using var nativeLease=NativeCoordination.Acquire(true); return action(); }
        finally { lease.Release(); }
    }
    public async Task<ValidationResult> SetupAsync(RunRequest request,CancellationToken cancellation)
    {
        Authorize(request);
        if(!await lease.WaitAsync(0,cancellation)) throw new LabException("lease_conflict","Target harness is executing.");
        IDisposable? nativeLease=null; Task<ValidationResult>? work=null;
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(request.Deadline>DateTimeOffset.UtcNow?request.Deadline-DateTimeOffset.UtcNow:TimeSpan.Zero);
        try
        {
            nativeLease=NativeCoordination.Acquire(true);
            Authorize(request);
            var scenario=registry.Get(request.ScenarioId);
            if(scenario is not ISynchronousScenario sync||sync.Describe().Affinity!=ThreadAffinity.Any||request.WarmupBatches!=0||
                !sync.Describe().Variants.Contains(request.Variant))
                throw new LabException("capability_unsupported","Mutation setup currently requires a synchronous Any-thread registered scenario.");
            if(request.ExpectedMethodScope is not null&&!(sync.Describe().MutationScopes?.Contains(request.ExpectedMethodScope)??false)||
                request.ExpectedMethodScope is null&&NativeCoordination.Present)
                throw new LabException("scenario_scope_mismatch","Setup oracle does not declare coverage of the selected mutation method.");
            if(request.ExpectedCallerScopes?.Any(scope=>!(sync.Describe().MutationScopes?.Contains(scope)??false))==true)
                throw new LabException("scenario_scope_mismatch","Setup oracle does not declare every affected caller scope.");
            grantUses.AddOrUpdate(request.Grant.Id,1,(_,old)=>old>=request.Grant.MaximumOperations?
                throw new LabException("permission_denied","Target grant budget exhausted."):old+1);
            work=Task.Run(()=>
            {
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    sync.Prepare(new(request.Variant,deadline.Token)); sync.Reset(new(sync.Describe().ResetLevel));
                    long completed=sync.ExecuteBatch(request.OperationCount); var oracle=sync.Validate();
                    return oracle with { Correct=oracle.Correct&&completed==request.OperationCount };
                }
                finally { sync.Cleanup(); }
            },CancellationToken.None);
            try
            {
                var result=await work.WaitAsync(deadline.Token);
                if(!result.Quiescent) tainted=true;
                NativeCoordination.ReportSetup(request.ExpectedEpoch,result.Correct&&result.Quiescent,request.ExpectedMethodScope);
                return result;
            }
            catch(OperationCanceledException)
            {
                try { await work.WaitAsync(TimeSpan.FromSeconds(2)); }
                catch(TimeoutException) { tainted=true; throw new LabException("target_tainted","Setup did not acknowledge cancellation."); }
                throw new LabException("scenario_failed","Mutation setup was cancelled.");
            }
            catch(Exception error) when(error is not OutOfMemoryException and not LabException)
            { throw new LabException("scenario_failed","Mutation setup or cleanup failed."); }
        }
        finally
        {
            if(work is {IsCompleted:false})
            {
                tainted=true;
                _=work.ContinueWith(done=>{ _=done.Exception; nativeLease?.Dispose(); lease.Release(); },CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
            else { nativeLease?.Dispose(); lease.Release(); }
        }
    }
    public async Task<Measurement> ExecuteAsync(RunRequest request,CancellationToken cancellation)
    {
        Authorize(request);
        if (!await lease.WaitAsync(0,cancellation)) throw new LabException("lease_conflict","Target harness is executing.");
        Task<Measurement>? work = null;
        IDisposable? nativeLease=null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(request.Deadline-DateTimeOffset.UtcNow);
        try
        {
            nativeLease=NativeCoordination.Acquire();
            Authorize(request);
            var scenario = registry.Get(request.ScenarioId);
            var descriptor = ScenarioRegistry.Describe(scenario);
            if (!descriptor.Variants.Contains(request.Variant)) throw new LabException("invalid_plan","Variant is not registered.");
            if (descriptor.ResetLevel == ResetLevel.FreshProcess&&!ownedProcess)
                throw new LabException("capability_unsupported","Fresh-process lifecycle requires an external process manager.");
            if(ownedProcess&&Interlocked.Exchange(ref ownedExecutionCount,1)!=0)
                throw new LabException("process_budget_exhausted","An owned fresh process supports exactly one measurement execution.");
            if (descriptor.Affinity == ThreadAffinity.ApplicationThread && applicationDispatcher is null)
                throw new LabException("capability_unsupported","Application-owned dispatcher is required.");
            if (scenario is IAsynchronousScenario && descriptor.Affinity == ThreadAffinity.ApplicationThread)
                throw new LabException("capability_unsupported","Async application-thread dispatch is not qualified.");
            grantUses.AddOrUpdate(request.Grant.Id,1,(_,old) =>
                old >= request.Grant.MaximumOperations ? throw new LabException("permission_denied","Target grant budget exhausted.") : old+1);
            if (scenario is ISynchronousScenario sync)
            {
                // Dispatch outside the timed synchronous fast path.
                work = descriptor.Affinity == ThreadAffinity.ApplicationThread
                    ? applicationDispatcher!.DispatchAsync(() => RunSync(sync,request,deadline.Token),deadline.Token)
                    : Task.Run(() => RunSync(sync,request,deadline.Token),CancellationToken.None);
            }
            else work = RunAsync((IAsynchronousScenario)scenario,request,deadline.Token);
            try
            {
                var result = await work.WaitAsync(deadline.Token);
                if (!result.Quiescent) { tainted = true; throw new LabException("target_tainted","Scenario reports unjoined application work."); }
                if (!result.Correct) throw new LabException("scenario_failed",result.Failure ?? "Correctness failed.");
                return result;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    await work.WaitAsync(TimeSpan.FromSeconds(2));
                    throw new LabException("scenario_failed","Execution deadline/cancellation ended the run.");
                }
                catch (TimeoutException)
                {
                    tainted = true;
                    throw new LabException("target_tainted","Work did not acknowledge cancellation; restart required.");
                }
            }
        }
        finally
        {
            if (work is { IsCompleted: false })
            {
                tainted = true;
                // Never release the target-side lease while application work still runs.
                _ = work.ContinueWith(completed => { _ = completed.Exception; nativeLease?.Dispose(); lease.Release(); },CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
            else { nativeLease?.Dispose(); lease.Release(); }
        }
    }
    private void Authorize(RunRequest request)
    {
        if (tainted) throw new LabException("target_tainted","Harness quiescence is unknown.");
        if (request.Target.Id != target.Id || request.Target.ProcessId != target.ProcessId ||
            request.Target.StartTimeUtcTicks != target.StartTimeUtcTicks)
            throw new LabException("target_identity_changed","Harness target differs.");
        if (request.Target.Epoch!=request.ExpectedEpoch||request.ExpectedEpoch != NativeCoordination.Epoch(target.Epoch)) throw new LabException("stale_epoch","Harness epoch differs.");
        var grant = request.Grant;
        if (grant.TargetId != target.Id || grant.StartTimeUtcTicks != target.StartTimeUtcTicks ||
            grant.Workspace != workspace || grant.Permission != Permission.ExecuteRegisteredScenario ||
            grant.ExpiresAt <= DateTimeOffset.UtcNow || grant.MaximumOperations <= 0 || grant.ContentHash is not null||grant.MethodScope is not null)
            throw new LabException("permission_denied","Target rejected the execution grant.");
        if (request.OperationCount is < 1 or > 100000000 || request.WarmupBatches is < 0 or > 1000 ||
            request.Deadline <= DateTimeOffset.UtcNow || request.Deadline > DateTimeOffset.UtcNow.AddMinutes(10))
            throw new LabException("invalid_plan","Harness budget/deadline is invalid.");
        if(request.RetainIndividualLatencies&&request.OperationCount>10000)
            throw new LabException("resource_limit","Individual-latency retention is bounded to 10000 operations.");
    }
    private static Measurement RunSync(ISynchronousScenario scenario,RunRequest request,CancellationToken token)
    {
        var descriptor = scenario.Describe();
        string? failure = null;
        Measurement? result = null;
        try
        {
            scenario.Prepare(new(request.Variant,token));
            scenario.Reset(new(descriptor.ResetLevel));
            bool jitSettle = Environment.GetEnvironmentVariable("DOTNETLAB_JIT_SETTLE_MS") == "1";
            for (int i=0;i<request.WarmupBatches;i++)
            {
                token.ThrowIfCancellationRequested();
                scenario.ExecuteBatch(request.OperationCount);
                if (jitSettle && i is 0 or 1 && i + 1 < request.WarmupBatches) Thread.Sleep(50);
            }
            if (request.ResetAfterWarmup) scenario.Reset(new(descriptor.ResetLevel));
            token.ThrowIfCancellationRequested();
            long[]? individual=request.RetainIndividualLatencies?new long[request.OperationCount]:null;
            using var telemetrySession=new BatchTelemetrySession(captureThreadCounters:true);
            HarnessMarkers.Log.Clock(request.RunId,request.BlockId,request.ExecutionId,"clock_before");
            int startThread = Environment.CurrentManagedThreadId;
            telemetrySession.CapturePreBatchGcSnapshot();
            long processBefore = GC.GetTotalAllocatedBytes(true);
            long? threadBefore = request.MeasureThreadAllocation ? GC.GetAllocatedBytesForCurrentThread() : null;
            telemetrySession.BeginThreadWindow();
            long start = Stopwatch.GetTimestamp();
            long completed;
            if(individual is null) completed=scenario.ExecuteBatch(request.OperationCount);
            else
            {
                completed=0;
                for(int i=0;i<individual.Length;i++)
                {
                    token.ThrowIfCancellationRequested(); long beforeOperation=Stopwatch.GetTimestamp();
                    long work=scenario.ExecuteBatch(1); long afterOperation=Stopwatch.GetTimestamp();
                    if(work!=1) throw new LabException("scenario_failed","Individual timing requires exactly one completed operation per interval.");
                    individual[i]=afterOperation-beforeOperation; completed++;
                }
            }
            long end = Stopwatch.GetTimestamp();
            telemetrySession.EndThreadWindow();
            long? threadBytes = threadBefore.HasValue ? GC.GetAllocatedBytesForCurrentThread()-threadBefore.Value : null;
            long processBytes = GC.GetTotalAllocatedBytes(true)-processBefore;
            int endThread = Environment.CurrentManagedThreadId;
            var telemetry = telemetrySession.BuildSnapshot(startThread == endThread);
            HarnessMarkers.Log.Clock(request.RunId,request.BlockId,request.ExecutionId,"clock_after");
            HarnessMarkers.Log.Measurement(request.RunId,request.BlockId,request.ExecutionId,start,end);
            var validation = scenario.Validate();
            bool correct = validation.Correct && validation.Quiescent && completed == request.OperationCount;
            result = new(request.ExecutionId,request.RunId,request.BlockId,request.Variant,start,end,
                Stopwatch.Frequency,completed,processBytes,threadBytes,startThread,endThread,correct,
                correct ? null : validation.Reason ?? "Completed-work or quiescence contract failed.",
                descriptor.InputHash,descriptor.Version,validation.Quiescent,
                individual is null?null:new("harness_sync_individual_stopwatch/v1","individual_operation_latency",Stopwatch.Frequency,individual),
                telemetry);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { failure = e.Message; }
        finally
        {
            try { scenario.Cleanup(); }
            catch (Exception e) when (e is not OutOfMemoryException) { failure = (failure is null ? "" : failure+"; ")+"cleanup_failed: "+e.Message; }
        }
        if (failure is not null) throw new LabException("scenario_failed",failure);
        return result ?? throw new LabException("scenario_failed","No measurement completed.");
    }
    private static async Task<Measurement> RunAsync(IAsynchronousScenario scenario,RunRequest request,CancellationToken token)
    {
        if (request.MeasureThreadAllocation) throw new LabException("invalid_plan","Async work cannot use current-thread allocation.");
        var descriptor = scenario.Describe();
        string? failure = null;
        Measurement? result = null;
        try
        {
            await scenario.PrepareAsync(new(request.Variant,token));
            await scenario.ResetAsync(new(descriptor.ResetLevel));
            for (int i=0;i<request.WarmupBatches;i++) { token.ThrowIfCancellationRequested(); await scenario.ExecuteBatchAsync(request.OperationCount,token); }
            if (request.ResetAfterWarmup) await scenario.ResetAsync(new(descriptor.ResetLevel));
            token.ThrowIfCancellationRequested();
            long[]? individual=request.RetainIndividualLatencies?new long[request.OperationCount]:null;
            using var telemetrySession=new BatchTelemetrySession(captureThreadCounters:false);
            HarnessMarkers.Log.Clock(request.RunId,request.BlockId,request.ExecutionId,"clock_before");
            int startThread = Environment.CurrentManagedThreadId;
            telemetrySession.CapturePreBatchGcSnapshot();
            long before = GC.GetTotalAllocatedBytes(true);
            long start = Stopwatch.GetTimestamp();
            long completed;
            if(individual is null) completed=await scenario.ExecuteBatchAsync(request.OperationCount,token);
            else
            {
                completed=0;
                for(int i=0;i<individual.Length;i++)
                {
                    token.ThrowIfCancellationRequested(); long beforeOperation=Stopwatch.GetTimestamp();
                    long work=await scenario.ExecuteBatchAsync(1,token); long afterOperation=Stopwatch.GetTimestamp();
                    if(work!=1) throw new LabException("scenario_failed","Individual timing requires exactly one joined operation per interval.");
                    individual[i]=afterOperation-beforeOperation; completed++;
                }
            }
            long end = Stopwatch.GetTimestamp();
            long bytes = GC.GetTotalAllocatedBytes(true)-before;
            int endThread = Environment.CurrentManagedThreadId;
            var telemetry = telemetrySession.BuildSnapshot(sameThread:false);
            HarnessMarkers.Log.Clock(request.RunId,request.BlockId,request.ExecutionId,"clock_after");
            HarnessMarkers.Log.Measurement(request.RunId,request.BlockId,request.ExecutionId,start,end);
            var validation = await scenario.ValidateAsync();
            bool correct = validation.Correct && validation.Quiescent && completed == request.OperationCount;
            result = new(request.ExecutionId,request.RunId,request.BlockId,request.Variant,start,end,
                Stopwatch.Frequency,completed,bytes,null,startThread,endThread,correct,
                correct ? null : validation.Reason ?? "Completed-work or quiescence contract failed.",
                descriptor.InputHash,descriptor.Version,validation.Quiescent,
                individual is null?null:new("harness_async_individual_stopwatch/v1","individual_operation_latency",Stopwatch.Frequency,individual),
                telemetry);
        }
        catch (Exception e) when (e is not OutOfMemoryException) { failure = e.Message; }
        finally
        {
            try { await scenario.CleanupAsync(); }
            catch (Exception e) when (e is not OutOfMemoryException) { failure = (failure is null ? "" : failure+"; ")+"cleanup_failed: "+e.Message; }
        }
        if (failure is not null) throw new LabException("scenario_failed",failure);
        return result ?? throw new LabException("scenario_failed","No measurement completed.");
    }
}
