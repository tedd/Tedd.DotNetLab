using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Tedd.DotNetLab.Probes;

public record ProbeRow(int MethodId,int ThreadId,long Entered,long NormallyCompleted,long ExceptionallyCompleted,
    long InFlight,long Overflow,long Dropped,long Saturated,long? NormalElapsedTicks,long? ExceptionalElapsedTicks);
public record ProbeSnapshot(string Version,bool Timing,long Frequency,string Scope,bool Complete,long DroppedThreads,long DroppedRecords,int FirstUseThreads,
    IReadOnlyList<ProbeRow> Rows);

public static class ProbeRuntime
{
    public const string Version="bounded-thread-slots/1";
    private readonly record struct Frame(int Method,long Tick);
    private sealed class Slot(int methods,int depth)
    {
        public int ThreadId,Depth,OverflowDepth,Busy;
        public bool Warmed;
        public readonly Frame[] Frames=new Frame[depth];
        public readonly long[] Entered=new long[methods],Normal=new long[methods],Exceptional=new long[methods],InFlight=new long[methods],
            Overflow=new long[methods],Dropped=new long[methods],Saturated=new long[methods],NormalTicks=new long[methods],ExceptionalTicks=new long[methods];
    }
    private sealed class Session(int methods,int threads,int depth,bool timing)
    {
        public int State=1,NextSlot;
        public long DroppedThreads,DroppedRecords;
        public readonly bool Timing=timing;
        public readonly int Methods=methods;
        public readonly Slot[] Slots=Enumerable.Range(0,threads).Select(_=>new Slot(methods,depth)).ToArray();
    }
    private static readonly object control=new();
    private static Session? active;
    public static bool IsActive=>Volatile.Read(ref active) is not null;
    [ThreadStatic] private static Session? localSession;
    [ThreadStatic] private static Slot? localSlot;
    [ThreadStatic] private static int ignoredNesting;
    public static void Begin(int methods,bool timing,int threads=32,int maximumNesting=64)
    {
        if(methods is <1 or >256||threads is <1 or >64||maximumNesting is <1 or >128)
            throw new ArgumentOutOfRangeException(nameof(methods),"Probe slot budgets exceed the supported limits.");
        lock(control)
        {
            if(active is not null) throw new InvalidOperationException("A probe session is already active.");
            _=Stopwatch.GetTimestamp();
            Volatile.Write(ref active,new Session(methods,threads,maximumNesting,timing));
        }
    }
    private static Slot? Register(Session session,bool warmed=false)
    {
        if(ReferenceEquals(localSession,session)) return localSlot;
        localSession=session; localSlot=null; ignoredNesting=0;
        lock(control)
        {
            if(Volatile.Read(ref session.State)!=1) return null;
            int index=session.NextSlot++;
            if(index>=session.Slots.Length) { session.DroppedThreads++; return null; }
            var slot=session.Slots[index]; slot.ThreadId=Environment.CurrentManagedThreadId; slot.Warmed=warmed;
            return localSlot=slot;
        }
    }
    public static void WarmCurrentThread()
    {
        var session=Volatile.Read(ref active);
        if(session is null||Volatile.Read(ref session.State)!=1) throw new InvalidOperationException("No active probe session.");
        var slot=Register(session,true)??throw new InvalidOperationException("Thread slot budget exhausted.");
        slot.Warmed=true;
    }
    private static void Add(long[] values,long[] saturation,int method,long value=1)
    {
        if(value<0||values[method]>long.MaxValue-value) { values[method]=long.MaxValue; saturation[method]=1; }
        else values[method]+=value;
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static void Enter(int method)
    {
        var session=Volatile.Read(ref active);
        if(session is null||Volatile.Read(ref session.State)!=1)
        {
            if(localSlot is {Depth:>0} or {OverflowDepth:>0}) ignoredNesting++;
            return;
        }
        var slot=Register(session);
        if(slot is not null) Volatile.Write(ref slot.Busy,1);
        if(Volatile.Read(ref session.State)!=1)
        { if(slot is not null) Volatile.Write(ref slot.Busy,0); if(localSlot is {Depth:>0} or {OverflowDepth:>0}) ignoredNesting++; return; }
        if(slot is null||method<0||method>=session.Methods)
        {
            Interlocked.Increment(ref session.DroppedRecords);
            if(slot is {Depth:>0} or {OverflowDepth:>0}) ignoredNesting++;
            if(slot is not null) Volatile.Write(ref slot.Busy,0); return;
        }
        Add(slot.Entered,slot.Saturated,method); slot.InFlight[method]++;
        if(slot.Depth==slot.Frames.Length||slot.OverflowDepth>0)
        { Add(slot.Overflow,slot.Saturated,method); slot.OverflowDepth++; Volatile.Write(ref slot.Busy,0); return; }
        slot.Frames[slot.Depth++]=new(method,session.Timing?Stopwatch.GetTimestamp():0);
        Volatile.Write(ref slot.Busy,0);
    }
    [MethodImpl(MethodImplOptions.NoInlining)] public static void Exit(int method)=>Finish(method,false);
    [MethodImpl(MethodImplOptions.NoInlining)] public static void ExitExceptional(int method)=>Finish(method,true);
    private static void Finish(int method,bool exceptional)
    {
        if(ignoredNesting>0) { ignoredNesting--; return; }
        var slot=localSlot; var session=localSession;
        if(slot is null||session is null||method<0||method>=session.Methods) return;
        Volatile.Write(ref slot.Busy,1);
        if(slot.OverflowDepth>0)
        {
            slot.OverflowDepth--;
            if(slot.InFlight[method]>0) slot.InFlight[method]--;
            Add(slot.Dropped,slot.Saturated,method); Volatile.Write(ref slot.Busy,0); return;
        }
        if(slot.Depth==0) { Volatile.Write(ref slot.Busy,0); return; }
        var frame=slot.Frames[slot.Depth-1];
        if(frame.Method!=method) { Add(slot.Dropped,slot.Saturated,method); Volatile.Write(ref slot.Busy,0); return; }
        slot.Depth--; slot.InFlight[method]--;
        Add(exceptional?slot.Exceptional:slot.Normal,slot.Saturated,method);
        if(session.Timing) Add(exceptional?slot.ExceptionalTicks:slot.NormalTicks,slot.Saturated,method,Stopwatch.GetTimestamp()-frame.Tick);
        Volatile.Write(ref slot.Busy,0);
    }
    public static ProbeSnapshot Stop()
    {
        lock(control)
        {
            var session=active??throw new InvalidOperationException("No active probe session.");
            Interlocked.Exchange(ref session.State,2);
            if(session.Slots.Any(s=>Volatile.Read(ref s.Busy)!=0||Volatile.Read(ref s.Depth)!=0||Volatile.Read(ref s.OverflowDepth)!=0))
            { Volatile.Write(ref session.State,1); throw new InvalidOperationException("Probe frames remain in flight; quiesce before flushing."); }
            Volatile.Write(ref active,null); Volatile.Write(ref session.State,0);
            var rows=new List<ProbeRow>();
            foreach(var slot in session.Slots.Where(s=>s.ThreadId!=0))
                for(int method=0;method<session.Methods;method++)
                    if(slot.Entered[method]!=0||slot.Dropped[method]!=0)
                        rows.Add(new(method,slot.ThreadId,slot.Entered[method],slot.Normal[method],slot.Exceptional[method],
                            slot.InFlight[method],slot.Overflow[method],slot.Dropped[method],slot.Saturated[method],
                            session.Timing?slot.NormalTicks[method]:null,session.Timing?slot.ExceptionalTicks[method]:null));
            bool complete=session.DroppedThreads==0&&session.DroppedRecords==0&&rows.All(r=>r.InFlight==0&&r.Overflow==0&&r.Dropped==0&&r.Saturated==0);
            return new(Version,session.Timing,Stopwatch.Frequency,"synchronous-invocations/inclusive-wall-time",complete,session.DroppedThreads,
                session.DroppedRecords,session.Slots.Count(s=>s.ThreadId!=0&&!s.Warmed),rows);
        }
    }
}
