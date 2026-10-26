using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

sealed class ApplicationThreadScenario(int owningThread) : ISynchronousScenario
{
    private CancellationToken cancellation;
    private long completed;
    private void VerifyThread()
    {
        if(Environment.CurrentManagedThreadId!=owningThread) throw new InvalidOperationException("Scenario lifecycle left the application owning thread.");
    }
    public ScenarioDescriptor Describe()=>new("application-thread","1",Wire.Hash("application-thread-increment"),ResetLevel.LogicalState,
        ThreadAffinity.ApplicationThread,false,"One increment on the application-owned main thread",["baseline","candidate"]);
    public void Prepare(ScenarioContext context) { VerifyThread(); cancellation=context.Cancellation; }
    public void Reset(ResetContext context) { VerifyThread(); completed=0; }
    public long ExecuteBatch(int operations)
    {
        VerifyThread();
        for(int index=0;index<operations;index++) { cancellation.ThrowIfCancellationRequested(); completed++; }
        return operations;
    }
    public ValidationResult Validate() { VerifyThread(); return new(completed>0); }
    public void Cleanup() { VerifyThread(); }
}
