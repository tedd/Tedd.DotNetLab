using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

public sealed class NativeValueScenario : ISynchronousScenario
{
    private long sum,count;
    private static readonly string ValueScope=MethodBinding.Scope(typeof(NativeFixture).GetMethod(nameof(NativeFixture.Value))!);
    public ScenarioDescriptor Describe()=>new("native-value","1",Wire.Hash("value-42"),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One verified Int32 leaf invocation",["baseline","candidate"],[ValueScope],new Dictionary<string,int>{{ValueScope,1}});
    public void Prepare(ScenarioContext context) { if(Tedd.DotNetLab.Probes.ProbeRuntime.IsActive) Tedd.DotNetLab.Probes.ProbeRuntime.WarmCurrentThread(); }
    public void Reset(ResetContext context) { sum=count=0; }
    public long ExecuteBatch(int operations)
    { for(int i=0;i<operations;i++) { sum+=NativeFixture.Value(); count++; } return operations; }
    public ValidationResult Validate()=>new(sum==42*count);
    public void Cleanup() { }
}
