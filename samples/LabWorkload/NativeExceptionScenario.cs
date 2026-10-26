using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Probes;

public sealed class NativeExceptionScenario : ISynchronousScenario
{
    private long sum,count,normal,exceptional;
    private static readonly string Scope=MethodBinding.Scope(typeof(NativeFixture).GetMethod(nameof(NativeFixture.Divide))!);
    public ScenarioDescriptor Describe()=>new("native-divide","1",Wire.Hash("alternating denominator two and zero"),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One division or expected DivideByZeroException",["baseline","candidate"],[Scope],new Dictionary<string,int>{{Scope,1}});
    public void Prepare(ScenarioContext context) { if(ProbeRuntime.IsActive) ProbeRuntime.WarmCurrentThread(); }
    public void Reset(ResetContext context) { sum=count=normal=exceptional=0; }
    public long ExecuteBatch(int operations)
    {
        for(int i=0;i<operations;i++)
        {
            try { sum+=NativeFixture.Divide(i%2==0?2:0); normal++; }
            catch(DivideByZeroException) { exceptional++; }
            count++;
        }
        return operations;
    }
    public ValidationResult Validate()=>new(sum==normal*21&&normal+exceptional==count&&exceptional==count/2);
    public void Cleanup() { }
}
