using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Probes;

public sealed class NativeInlinerScenario : ISynchronousScenario
{
    private long sum,count;
    private static readonly string Leaf=MethodBinding.Scope(typeof(NativeFixture).GetMethod(nameof(NativeFixture.InlineLeaf))!);
    private static readonly string Caller=MethodBinding.Scope(typeof(NativeFixture).GetMethod(nameof(NativeFixture.InlineCaller))!);
    public ScenarioDescriptor Describe()=>new("native-inliner","1",Wire.Hash("caller invokes leaf with one"),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One declared caller/leaf evaluation",["baseline","candidate"],[Leaf,Caller],new Dictionary<string,int>{{Leaf,1}});
    public void Prepare(ScenarioContext context) { if(ProbeRuntime.IsActive) ProbeRuntime.WarmCurrentThread(); }
    public void Reset(ResetContext context) { sum=count=0; }
    public long ExecuteBatch(int operations)
    { for(int i=0;i<operations;i++) { sum+=NativeFixture.InlineCaller(1); count++; } return operations; }
    public ValidationResult Validate()=>new(sum==count*43);
    public void Cleanup() { }
}
