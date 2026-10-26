using System.Threading.Channels;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Harness;

/// <summary>The application pumps this queue on the thread that constructs it.</summary>
public sealed class ApplicationThreadDispatcher : IScenarioDispatcher,IDisposable
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
