using System.Diagnostics;
using System.Runtime.CompilerServices;
using Tedd.DotNetLab.Contracts;

if(args.Length==2&&args[0]=="--active-frame-fixture")
{
    if(!OperatingSystem.IsWindows()) throw new LabException("capability_unsupported","Active-frame fixture requires Windows x64.");
    await ActiveFrameFixture.RunAsync(args[1]); return;
}
if (args.Length == 2 && args[0] is "--harness" or "--application-thread-harness")
{
    GC.KeepAlive(NativeFixture.Value());
    GC.KeepAlive(NativeFixture.Recursive(5));
    GC.KeepAlive(NativeFixture.Loop(5));
    GC.KeepAlive(NativeFixture.Divide(2));
    GC.KeepAlive(NativeFixture.InlineLeaf(1));
    GC.KeepAlive(NativeFixture.InlineCaller(1));
    GC.KeepAlive(ManagedBodyFixture.Shared.Sum([1,2,3]));
    GC.KeepAlive(ManagedBodyFixture.Add(System.Runtime.Intrinsics.Vector256.Create(2f)));
    int managedValue=2; ManagedBodyFixture.Increment(ref managedValue);
    GC.KeepAlive(ManagedBodyFixture.Transform(new System.Numerics.Vector2(2,3)));
    GC.KeepAlive(ManagedBodyFixture.Incorrect());
    GC.KeepAlive(ManagedBodyFixture.IntegerCall(-3));
    GC.KeepAlive(ManagedBodyFixture.SpanSum([1,2,3]));
    var registry = new Tedd.DotNetLab.Harness.ScenarioRegistry();
    registry.Register(new CpuScenario());
    registry.Register(new FactorialScenario());
    registry.Register(new GcWindowScenario());
    registry.Register(new AsyncScenario());
    registry.Register(new NativeValueScenario());
    registry.Register(new NativeRecursiveScenario());
    registry.Register(new NativeLoopScenario());
    registry.Register(new NativeExceptionScenario());
    registry.Register(new NativeInlinerScenario());
    if(Environment.GetEnvironmentVariable("DOTNETLAB_BUDGET_GROWTH_STIMULUS")=="1") registry.Register(new NativeBudgetGrowthScenario());
    if(OperatingSystem.IsWindows()&&Environment.GetEnvironmentVariable("DOTNETLAB_ACTIVE_FRAME_STIMULUS")=="1")
    {
        GC.KeepAlive(NativeFixture.ActiveLoop(0));
        registry.Register(new NativeActiveFrameScenario()); registry.Register(new NativeActiveZeroScenario());
    }
    using var dispatcher=args[0]=="--application-thread-harness"?new Tedd.DotNetLab.Harness.ApplicationThreadDispatcher():null;
    if(dispatcher is not null) registry.Register(new ApplicationThreadScenario(dispatcher.OwningThreadId));
    foreach(string method in new[]{nameof(ManagedBodyFixture.Sum),nameof(ManagedBodyFixture.Add),nameof(ManagedBodyFixture.Increment),nameof(ManagedBodyFixture.Transform),nameof(ManagedBodyFixture.Incorrect),nameof(ManagedBodyFixture.IntegerCall),nameof(ManagedBodyFixture.SpanSum)})
        registry.Register(new ManagedReplacementScenario(method,dispatcher?.OwningThreadId));
    using var cancellation = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
    var host = new Tedd.DotNetLab.Harness.HarnessHost(registry,args[1],dispatcher,probes:new Tedd.DotNetLab.Probes.HarnessProbeSupport(args[1]));
    var listener = host.ListenAsync(cancellation.Token);
    Task? inspectionStress=null;
    if(Environment.GetEnvironmentVariable("DOTNETLAB_INSPECTION_STRESS")=="1")
        inspectionStress=Task.Run(()=>
        {
            for(int iteration=0;iteration<512&&!cancellation.IsCancellationRequested;iteration++)
            {
                var generated=new System.Reflection.Emit.DynamicMethod("inspection-stress-"+iteration,typeof(int),Type.EmptyTypes);
                var body=generated.GetILGenerator(); body.Emit(System.Reflection.Emit.OpCodes.Ldc_I4,iteration); body.Emit(System.Reflection.Emit.OpCodes.Ret);
                GC.KeepAlive(generated.CreateDelegate<Func<int>>()());
                GC.Collect(0,GCCollectionMode.Forced,true); Thread.Sleep(2);
            }
        });
    Console.WriteLine(Wire.Serialize(new { pid = Environment.ProcessId, runtime = Environment.Version.ToString(), target=host.Target,application_thread_id=dispatcher?.OwningThreadId }));
    Console.Out.Flush();
    if (Environment.GetEnvironmentVariable("DOTNETLAB_EXIT_ON_STDIN") == "1") _ = Task.Run(async () => { await Console.In.ReadLineAsync(); cancellation.Cancel(); });
    if(dispatcher is not null)
    {
        _=listener.ContinueWith(_=>cancellation.Cancel(),CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
        dispatcher.Pump(cancellation.Token);
    }
    await listener;
    if(inspectionStress is not null) await inspectionStress;
    return;
}
Console.WriteLine(Wire.Serialize(new { pid = Environment.ProcessId, runtime = Environment.Version.ToString() }));
Console.Out.Flush();
var clock = Stopwatch.StartNew();
long sum = 0;
while (clock.Elapsed < TimeSpan.FromMinutes(10))
{
    sum ^= Stimulus.Work();
    // Explicit diagnostic stimulus, outside any scenario measurement.
    GC.Collect(0, GCCollectionMode.Forced, true);
    Thread.Sleep(20);
}
GC.KeepAlive(sum);
static class Stimulus
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static long Work()
    {
        long result = 0;
        for (int i = 0; i < 512; i++)
        {
            var array = new byte[4096];
            array[i & 1023] = (byte)i;
            result += array[i & 1023];
        }
        return result;
    }
}

public static class NativeFixture
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Value() => 42;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static T Generic<T>(T value) => value;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int NeverInvoked() => 13;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Recursive(int depth) => depth<=0?1:Recursive(depth-1)+1;
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Loop(int count)
    {
        int sum=0;
        for(int index=0;index<count;index++) sum+=index;
        return sum;
    }
    [MethodImpl(MethodImplOptions.NoInlining|MethodImplOptions.AggressiveOptimization)]
    public static int ActiveLoop(int count)
    {
        int sum=0;
        for(int index=0;index<count;index++) for(int inner=0;inner<32;inner++) sum=unchecked(sum+index+inner);
        return sum;
    }
    // Existing trusted references permit selected instrumentation without adding arbitrary metadata.
    public static void HelperReferences(int id)
    { Tedd.DotNetLab.Probes.ProbeRuntime.Enter(id); Tedd.DotNetLab.Probes.ProbeRuntime.Exit(id); Tedd.DotNetLab.Probes.ProbeRuntime.ExitExceptional(id); }
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static int Divide(int denominator)=>42/denominator;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int InlineLeaf(int value)=>value+41;
    [MethodImpl(MethodImplOptions.NoInlining|MethodImplOptions.AggressiveOptimization)]
    public static int InlineCaller(int value)=>InlineLeaf(value)+1;
    // Explicit metadata integration supplies an existing all-Int32 signature with return-local capacity.
    public static int ProbeLocalReferences(int count)
    {
        int sum=0;
        for(int index=0;index<count;index++)
        { int value=count+index; if(value>13) sum-=value; else sum+=value; }
        return sum;
    }
}
