using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Probes;

public sealed class NativeRecursiveScenario : ISynchronousScenario
{
    private long sum,count;
    private static readonly string Scope=MethodBinding.Scope(typeof(NativeFixture).GetMethod(nameof(NativeFixture.Recursive))!);
    public ScenarioDescriptor Describe()=>new("native-recursive","1",Wire.Hash(new { depth=5 }),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One depth-five recursive evaluation",["baseline","candidate"],[Scope],new Dictionary<string,int>{{Scope,6}});
    public void Prepare(ScenarioContext context) { if(ProbeRuntime.IsActive) ProbeRuntime.WarmCurrentThread(); }
    public void Reset(ResetContext context) { sum=count=0; }
    public long ExecuteBatch(int operations)
    { for(int i=0;i<operations;i++) { sum+=NativeFixture.Recursive(5); count++; } return operations; }
    public ValidationResult Validate()=>new(sum==count*6);
    public void Cleanup() { }
}
