using System.Threading.Channels;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Harness;

/// <summary>The application pumps this queue on the thread that constructs it.</summary>
public sealed class ApplicationThreadDispatcher : IApplicationMutationBoundary,IDisposable
{
    private interface IWork { bool TryStart(); void Execute(); void Stop(); }
    private sealed class Work<T> : IWork
    {
        private readonly Func<T> action;
        private readonly CancellationToken cancellation;
        private readonly CancellationTokenRegistration registration;
        private readonly TaskCompletionSource<T> completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int state; // queued, executing, cancelled/stopped, completed
        public Task<T> Task=>completion.Task;
        public Work(Func<T> action,CancellationToken cancellation)
        {
            this.action=action; this.cancellation=cancellation;
            registration=cancellation.Register(()=>
            {
                if(Interlocked.CompareExchange(ref state,2,0)==0) completion.TrySetCanceled(cancellation);
            });
        }
        public bool TryStart()=>Interlocked.CompareExchange(ref state,1,0)==0;
        public void Execute()
        {
            try { cancellation.ThrowIfCancellationRequested(); completion.TrySetResult(action()); }
            catch(OperationCanceledException error) when(error.CancellationToken==cancellation) { completion.TrySetCanceled(cancellation); }
            catch(Exception error) when(error is not OutOfMemoryException) { completion.TrySetException(error); }
            finally { Volatile.Write(ref state,3); registration.Dispose(); }
        }
        public void Stop()
        {
            if(Interlocked.CompareExchange(ref state,2,0)==0)
                completion.TrySetException(new LabException("dispatcher_stopped","Application dispatcher stopped before callback execution."));
            registration.Dispose();
        }
    }
    private readonly Channel<IWork> queue;
    private readonly CancellationTokenSource stopping=new();
    private readonly object lifecycle=new();
    private int disposed,pumping;
    private BoundaryLease? boundary;
    private sealed class BoundaryLease : IDisposable
    {
        private readonly ApplicationThreadDispatcher owner;
        private CancellationTokenRegistration registration;
        public BoundaryLease(ApplicationThreadDispatcher owner) { this.owner=owner; }
        public void Register(CancellationToken cancellation)=>registration=cancellation.Register(Dispose);
        public void Dispose() { owner.ReleaseBoundary(this); registration.Unregister(); }
    }
    public async Task<IDisposable> HoldAsync(CancellationToken cancellation)
    {
        var held=await DispatchAsync(()=>
        {
            lock(lifecycle)
            {
                if(boundary is not null) throw new LabException("lease_conflict","Application mutation boundary is already held.");
                var result=new BoundaryLease(this); boundary=result; return result;
            }
        },cancellation);
        held.Register(cancellation); return held;
    }
    private void ReleaseBoundary(BoundaryLease held)
    {
        lock(lifecycle)
        {
            if(boundary!=held) return;
            boundary=null;
            if(disposed!=0) return;
            var wake=new Work<int>(()=>0,CancellationToken.None);
            if(!queue.Writer.TryWrite(wake)) { wake.Stop(); _=wake.Task.Exception; }
        }
    }
    /// <summary>Call between frames after joining application workers. A mutation
    /// hold keeps this boundary open while servicing validation callbacks.</summary>
    public int PumpPending(int maximumCallbacks=1,CancellationToken cancellation=default)
    {
        if(maximumCallbacks is <1 or >1024) throw new ArgumentOutOfRangeException(nameof(maximumCallbacks));
        if(Environment.CurrentManagedThreadId!=OwningThreadId) throw new LabException("dispatcher_thread_mismatch","Only the owning application thread can pump callbacks.");
        lock(lifecycle)
        {
            if(disposed!=0) throw new LabException("dispatcher_stopped","Application dispatcher is stopped.");
            if(pumping!=0) throw new LabException("lease_conflict","Application dispatcher pump is already active.");
            pumping=1;
        }
        int completed=0;
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,stopping.Token);
        try
        {
            while(true)
            {
                IWork? work;
                bool held; lock(lifecycle) held=boundary is not null;
                if(stop.IsCancellationRequested||!held&&completed>=maximumCallbacks) break;
                if(held)
                {
                    try { work=queue.Reader.ReadAsync(stop.Token).AsTask().GetAwaiter().GetResult(); }
                    catch(OperationCanceledException) when(stop.IsCancellationRequested) { break; }
                    catch(ChannelClosedException) { break; }
                }
                else if(!queue.Reader.TryRead(out work)) break;
                bool started; lock(lifecycle) started=disposed==0&&!stop.IsCancellationRequested&&work.TryStart();
                if(started) { work.Execute(); completed++; } else work.Stop();
            }
            return completed;
        }
        finally { lock(lifecycle) { pumping=0; if(disposed!=0) stopping.Dispose(); } }
    }
    public int OwningThreadId { get; }=Environment.CurrentManagedThreadId;
    public ApplicationThreadDispatcher(int maximumQueuedCallbacks=64)
    {
        if(maximumQueuedCallbacks is <1 or >1024) throw new ArgumentOutOfRangeException(nameof(maximumQueuedCallbacks));
        queue=Channel.CreateBounded<IWork>(new BoundedChannelOptions(maximumQueuedCallbacks) { SingleReader=false,FullMode=BoundedChannelFullMode.Wait });
    }
    public Task<T> DispatchAsync<T>(Func<T> action,CancellationToken cancellation)
    {
        ArgumentNullException.ThrowIfNull(action); cancellation.ThrowIfCancellationRequested();
        lock(lifecycle)
        {
            if(disposed!=0) throw new LabException("dispatcher_stopped","Application dispatcher is stopped.");
            var work=new Work<T>(action,cancellation);
            if(!queue.Writer.TryWrite(work))
            {
                work.Stop();
                // Observe the rejected internal task; the caller receives the admission error.
                _=work.Task.Exception;
                throw new LabException("resource_limit","Application dispatcher queue cannot admit another callback.");
            }
            return work.Task;
        }
    }
    public void Pump(CancellationToken cancellation)
    {
        if(Environment.CurrentManagedThreadId!=OwningThreadId) throw new LabException("dispatcher_thread_mismatch","Only the application owning thread can pump callbacks.");
        CancellationTokenSource stop;
        lock(lifecycle)
        {
            if(disposed!=0) throw new LabException("dispatcher_stopped","Application dispatcher is stopped.");
            if(pumping!=0) throw new LabException("lease_conflict","Application dispatcher pump is already active.");
            stop=CancellationTokenSource.CreateLinkedTokenSource(cancellation,stopping.Token); pumping=1;
        }
        try
        {
            while(true)
            {
                IWork work;
                try { work=queue.Reader.ReadAsync(stop.Token).AsTask().GetAwaiter().GetResult(); }
                catch(OperationCanceledException) when(stop.IsCancellationRequested) { return; }
                catch(ChannelClosedException) { return; }
                bool started;
                lock(lifecycle) { started=disposed==0&&!stop.IsCancellationRequested&&work.TryStart(); }
                // Callback admission and shutdown share one transition; running work is joined.
                if(started) work.Execute(); else work.Stop();
            }
        }
        finally
        {
            stop.Dispose();
            lock(lifecycle) { pumping=0; StopQueued(); stopping.Dispose(); }
        }
    }
    public void Dispose()
    {
        lock(lifecycle)
        {
            if(disposed!=0) return;
            StopQueued();
            if(pumping==0) stopping.Dispose();
        }
    }
    private void StopQueued()
    {
        if(disposed!=0) return;
        disposed=1;
        queue.Writer.TryComplete(); stopping.Cancel();
        while(queue.Reader.TryRead(out var work)) work.Stop();
    }
}
