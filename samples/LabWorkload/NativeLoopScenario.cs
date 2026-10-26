using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Probes;

public sealed class NativeLoopScenario : ISynchronousScenario
{
    private long sum,count;
    private static readonly string Scope=MethodBinding.Scope(typeof(NativeFixture).GetMethod(nameof(NativeFixture.Loop))!);
    public ScenarioDescriptor Describe()=>new("native-loop","1",Wire.Hash(new { iterations=5 }),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One sum of integers zero through four",["baseline","candidate"],[Scope],new Dictionary<string,int>{{Scope,1}});
    public void Prepare(ScenarioContext context) { if(ProbeRuntime.IsActive) ProbeRuntime.WarmCurrentThread(); }
    public void Reset(ResetContext context) { sum=count=0; }
    public long ExecuteBatch(int operations)
    { for(int i=0;i<operations;i++) { sum+=NativeFixture.Loop(5); count++; } return operations; }
    public ValidationResult Validate()=>new(sum==count*10);
    public void Cleanup() { }
}
