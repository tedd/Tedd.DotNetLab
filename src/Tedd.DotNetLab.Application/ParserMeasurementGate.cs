using System.Diagnostics;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record ParserMeasurementState(int ActiveMeasurements,bool ParserActive,int Queued,int UncertainMeasurements,bool Closed);
public sealed class ParserMeasurementGate(Func<TargetIdentity,bool>? targetGone=null,int maximumQueuedActivities=64)
{
    public int MaximumQueuedActivities { get; }=maximumQueuedActivities is >=1 and <=1024?maximumQueuedActivities:throw new ArgumentOutOfRangeException(nameof(maximumQueuedActivities));
    private readonly object sync=new(); private readonly LinkedList<Waiter> queue=new();
    private readonly Dictionary<Lease,TargetIdentity> uncertain=new();
    private int readers; private bool writer,closed;
    private readonly CancellationTokenSource closing=new();
    private sealed class Waiter(bool parser)
    {
        public bool Parser { get; }=parser; public LinkedListNode<Waiter>? Node;
        public TaskCompletionSource<Lease> Completion { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    public sealed class Lease : IDisposable
    {
        private readonly ParserMeasurementGate owner; private readonly bool parser; private bool released,retained;
        internal MachineMutex? Machine;
        internal Lease(ParserMeasurementGate owner,bool parser) { this.owner=owner; this.parser=parser; }
        public void RetainForUncertainTarget(TargetIdentity target)
        {
            lock(owner.sync)
            {
                if(parser||released) throw new InvalidOperationException("Only an active measurement lease can retain uncertain quiescence.");
                retained=true; owner.uncertain[this]=target;
            }
            _=Task.Run(async()=>
            {
                while(true)
                {
                    lock(owner.sync) { if(released) return; }
                    await Task.Delay(100); owner.ReapExitedTargets();
                }
            });
        }
        public void Dispose()
        {
            lock(owner.sync)
            {
                if(released||retained) return; Release(); owner.Pump();
            }
        }
        internal void Release()
        {
            if(released) return; released=true;
            Machine?.Dispose();
            if(parser) owner.writer=false; else owner.readers--;
        }
    }
    public ParserMeasurementState State { get { lock(sync) return new(readers,writer,queue.Count,uncertain.Count,closed); } }
    public Task<Lease> EnterMeasurementAsync(CancellationToken cancellation=default)=>EnterAsync(false,cancellation);
    public Task<Lease> EnterParserAsync(CancellationToken cancellation=default)=>EnterAsync(true,cancellation);
    private async Task<Lease> EnterAsync(bool parser,CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested(); var waiter=new Waiter(parser);
        lock(sync)
        {
            if(closed) throw new LabException("controller_stopping","Activity scheduler is closed.");
            if(queue.Count>=MaximumQueuedActivities) throw new LabException("resource_limit","Parser/measurement activity queue is full.");
            waiter.Node=queue.AddLast(waiter); Pump();
        }
        using var registration=cancellation.Register(()=>
        {
            lock(sync)
            {
                if(waiter.Node is null) return;
                queue.Remove(waiter.Node); waiter.Node=null; waiter.Completion.TrySetCanceled(cancellation); Pump();
            }
        });
        while(!waiter.Completion.Task.IsCompleted)
        {
            await Task.WhenAny(waiter.Completion.Task,Task.Delay(100,cancellation));
            ReapExitedTargets();
        }
        var lease=await waiter.Completion.Task;
        if(cancellation.IsCancellationRequested) { lease.Dispose(); cancellation.ThrowIfCancellationRequested(); }
        try
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(cancellation,closing.Token);
            lease.Machine=await MachineMutex.AcquireAsync(MachineMutex.MeasurementName,linked.Token);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }
    private void Pump()
    {
        if(closed||writer||readers>0) return;
        while(queue.First is { } node)
        {
            if(node.Value.Parser&&readers>0) return;
            queue.RemoveFirst(); node.Value.Node=null;
            if(node.Value.Parser) writer=true; else readers++;
            node.Value.Completion.SetResult(new Lease(this,node.Value.Parser));
            return;
        }
    }
    private void ReapExitedTargets()
    {
        KeyValuePair<Lease,TargetIdentity>[] snapshot; lock(sync) snapshot=uncertain.ToArray();
        foreach(var pair in snapshot)
        {
            if(!(targetGone??HasExited)(pair.Value)) continue;
            lock(sync) { if(uncertain.Remove(pair.Key)) { pair.Key.Release(); Pump(); } }
        }
    }
    private static bool HasExited(TargetIdentity target)
    {
        try { using var process=Process.GetProcessById(target.ProcessId); return process.HasExited||ProcessIdentity.StartTimeUtcTicks(process)!=target.StartTimeUtcTicks; }
        catch(ArgumentException) { return true; }
        catch(LabException error) { return error.Code=="target_gone"; }
        catch(Exception error) when(error is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }
    public void AcknowledgeQuiescence(TargetIdentity target)
    {
        lock(sync)
        {
            foreach(var pair in uncertain.Where(pair=>pair.Value.Id==target.Id&&pair.Value.ProcessId==target.ProcessId&&pair.Value.StartTimeUtcTicks==target.StartTimeUtcTicks).ToArray())
            { uncertain.Remove(pair.Key); pair.Key.Release(); }
            Pump();
        }
    }
    public void Close()
    {
        lock(sync)
        {
            closed=true;
            closing.Cancel();
            while(queue.First is { } node)
            {
                queue.RemoveFirst(); node.Value.Node=null;
                node.Value.Completion.TrySetException(new LabException("controller_stopping","Activity scheduler is closed."));
            }
        }
    }
}
