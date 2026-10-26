using System.Runtime.CompilerServices;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

// CPU-only factors: A enables checked arithmetic; B replaces the loop with the equivalent formula.
// Every arm computes the same sum, and 11 applies both factors. Dispatch is outside the timer.
sealed class FactorialScenario : ISynchronousScenario
{
    private Func<int,long> sum=Loop;
    private long actual,expected;
    private CancellationToken cancellation;
    public ScenarioDescriptor Describe()=>new("factorial-sum","1",Wire.Hash(new { input=1024 }),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One integer sum per operation.",["00","10","01","11"]);
    public void Prepare(ScenarioContext context)
    {
        cancellation=context.Cancellation;
        sum=context.Variant switch { "00"=>Loop,"10"=>CheckedLoop,"01"=>Formula,"11"=>CheckedFormula,_=>throw new InvalidOperationException() };
    }
    public void Reset(ResetContext context) { actual=0; expected=0; }
    public long ExecuteBatch(int count)
    {
        actual=0; expected=0;
        for(int i=0;i<count;i++) { cancellation.ThrowIfCancellationRequested(); int n=1024+(i&7); actual+=sum(n); expected+=(long)n*(n+1)/2; }
        return count;
    }
    public ValidationResult Validate()=>new(actual==expected);
    public void Cleanup() { }
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Loop(int n) { long value=0; for(int i=1;i<=n;i++) value+=i; return value; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static long CheckedLoop(int n) { long value=0; for(int i=1;i<=n;i++) value=checked(value+i); return value; }
    [MethodImpl(MethodImplOptions.NoInlining)] private static long Formula(int n)=>(long)n*(n+1)/2;
    [MethodImpl(MethodImplOptions.NoInlining)] private static long CheckedFormula(int n)=>checked((long)n*checked(n+1))/2;
}
