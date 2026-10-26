using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

// Opt-in resource stimulus for the disposable native-budget qualification fixture.
sealed class NativeBudgetGrowthScenario : ISynchronousScenario
{
    private const int Bytes=192*1024*1024;
    private byte[]? retained;
    private CancellationToken cancellation;
    public ScenarioDescriptor Describe()=>new("native-budget-growth","1",Wire.Hash(new { bytes=Bytes }),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One retained 192 MiB buffer; resource-limit stimulus.",["baseline","candidate"]);
    public void Prepare(ScenarioContext context)=>cancellation=context.Cancellation;
    public void Reset(ResetContext context)=>retained=null;
    public long ExecuteBatch(int operations)
    {
        if(operations!=1) throw new LabException("resource_limit","Memory-budget stimulus permits one operation.");
        cancellation.ThrowIfCancellationRequested(); retained=GC.AllocateUninitializedArray<byte>(Bytes); retained.AsSpan().Fill(42); return 1;
    }
    public ValidationResult Validate()=>new(retained is { Length:Bytes }&&retained[0]==42&&retained[^1]==42);
    public void Cleanup() { } // The registry retains the allocation until process exit.
}
