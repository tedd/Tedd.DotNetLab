using System.Runtime.Versioning;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Transport.Local;

[SupportedOSPlatform("windows")]
sealed class NativeActiveFrameScenario : ISynchronousScenario
{
    private const int Iterations=200_000_000;
    private int result;
    private static readonly string Scope=MethodBinding.Scope(typeof(NativeFixture).GetMethod(nameof(NativeFixture.ActiveLoop))!);
    public ScenarioDescriptor Describe()=>new("native-active-frame","1",Wire.Hash(new { iterations=Iterations }),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One bounded active-loop call; quiescence qualification stimulus.",["baseline","candidate"],[Scope],new Dictionary<string,int>{{Scope,1}});
    public void Prepare(ScenarioContext context) { }
    public void Reset(ResetContext context)=>result=0;
    public long ExecuteBatch(int operations)
    {
        if(operations!=1) throw new LabException("resource_limit","Active-frame stimulus requires one operation.");
        Frames.WriteAsync(Console.OpenStandardOutput(),new { phase="registered-active-frame",thread_id=ActiveFrameFixture.GetCurrentThreadId() },CancellationToken.None).GetAwaiter().GetResult();
        result=NativeFixture.ActiveLoop(Iterations); return 1;
    }
    public ValidationResult Validate()=>new(result==unchecked((int)(16L*Iterations*(Iterations-1)+496L*Iterations)));
    public void Cleanup() { }
}

sealed class NativeActiveZeroScenario : ISynchronousScenario
{
    private int sum;
    private static readonly string Scope=MethodBinding.Scope(typeof(NativeFixture).GetMethod(nameof(NativeFixture.ActiveLoop))!);
    public ScenarioDescriptor Describe()=>new("native-active-zero","1",Wire.Hash(new { iterations=0 }),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One zero-iteration call; untimed original-body setup oracle.",["baseline","candidate"],[Scope],new Dictionary<string,int>{{Scope,1}});
    public void Prepare(ScenarioContext context) { }
    public void Reset(ResetContext context)=>sum=0;
    public long ExecuteBatch(int operations) { for(int i=0;i<operations;i++) sum+=NativeFixture.ActiveLoop(0); return operations; }
    public ValidationResult Validate()=>new(sum==0);
    public void Cleanup() { }
}
