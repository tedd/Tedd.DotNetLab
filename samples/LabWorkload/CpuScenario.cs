using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

public sealed class CpuScenario : ISynchronousScenario
{
    private string variant = "baseline";
    private long checksum;
    private long completed;
    private CancellationToken cancellation;
    public ScenarioDescriptor Describe() => new("cpu-sum","1",Wire.Hash(new { n=100 }),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One sum of integers 0..99",["baseline","candidate"]);
    public void Prepare(ScenarioContext context) { variant=context.Variant; cancellation=context.Cancellation; }
    public void Reset(ResetContext context) { checksum=0; completed=0; }
    public long ExecuteBatch(int operationCount)
    {
        for (int op=0;op<operationCount;op++)
        {
            cancellation.ThrowIfCancellationRequested();
            if (variant == "baseline") checksum += Enumerable.Range(0,100).Sum();
            else
            {
                long value=0;
                for (int i=0;i<100;i++) value += i;
                checksum += value;
            }
            completed++;
        }
        return operationCount;
    }
    public ValidationResult Validate() => new(checksum == completed*4950);
    public void Cleanup() { }
}
public sealed class AsyncScenario : IAsynchronousScenario
{
    private int completed;
    public ScenarioDescriptor Describe() => new("async-yield","1",Wire.Hash("async-yield"),ResetLevel.LogicalState,
        ThreadAffinity.Any,true,"One awaited continuation",["baseline","candidate"]);
    public ValueTask PrepareAsync(ScenarioContext context) => ValueTask.CompletedTask;
    public ValueTask ResetAsync(ResetContext context) { completed=0; return ValueTask.CompletedTask; }
    public async ValueTask<long> ExecuteBatchAsync(int operations,CancellationToken cancellation)
    {
        for (int i=0;i<operations;i++) { cancellation.ThrowIfCancellationRequested(); await Task.Yield(); completed++; }
        return completed;
    }
    public ValueTask<ValidationResult> ValidateAsync() => ValueTask.FromResult(new ValidationResult(completed>0));
    public ValueTask CleanupAsync() => ValueTask.CompletedTask;
}
