using System.Runtime.CompilerServices;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

if(args.Length!=2||args[0]!="--harness") return 2;
var registry=new ScenarioRegistry();
registry.Register(new SumScenario("normal-sum"));
registry.Register(new SumScenario("normal-wrong"));
registry.Register(new SumScenario("normal-slow"));
await new HarnessHost(registry,args[1]).RunOwnedAsync();
return 0;

// Startup is CPU-only. No configuration, network, or database write paths exist in this fixture.
sealed class SumScenario(string id) : ISynchronousScenario
{
    private CancellationToken cancellation;
    private long actual,expected;
    public ScenarioDescriptor Describe()=>new(id,"1",Wire.Hash(new { input=1024 }),ResetLevel.FreshProcess,
        ThreadAffinity.Any,false,"One complete integer sum per operation.",["baseline","candidate"]);
    public void Prepare(ScenarioContext context)=>cancellation=context.Cancellation;
    public void Reset(ResetContext context) { actual=0; expected=0; }
    public long ExecuteBatch(int operationCount)
    {
        if(id=="normal-slow")
        {
            while(!cancellation.WaitHandle.WaitOne(10)) { }
            cancellation.ThrowIfCancellationRequested();
        }
        actual=0; expected=0;
        for(int i=0;i<operationCount;i++)
        {
            cancellation.ThrowIfCancellationRequested();
            int n=1024+(i&7);
            actual+=Sum(n); expected+=(long)n*(n+1)/2;
        }
        return operationCount;
    }
    public ValidationResult Validate()=>new(actual==expected&&id!="normal-wrong");
    public void Cleanup() { }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long Sum(int n)
    {
#if NORMAL_CANDIDATE
        return (long)n*(n+1)/2;
#else
        long sum=0;
        for(int i=1;i<=n;i++) sum+=i;
        return sum;
#endif
    }
}
