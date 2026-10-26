using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

// Explicit diagnostic stimulus: forced generation-zero GC is part of this registered workload, not harness reset policy.
sealed class GcWindowScenario : ISynchronousScenario
{
    private CancellationToken cancellation;
    private long checksum,expected;
    public ScenarioDescriptor Describe()=>new("gc-window","1",Wire.Hash(new { bytes=16384,collect_every=64 }),ResetLevel.LogicalState,
        ThreadAffinity.Any,false,"One allocated buffer and checksum per operation; explicit GC stimulus every 64 operations.",["baseline","candidate"]);
    public void Prepare(ScenarioContext context)=>cancellation=context.Cancellation;
    public void Reset(ResetContext context) { checksum=0; expected=0; }
    public long ExecuteBatch(int count)
    {
        checksum=0; expected=0;
        for(int i=0;i<count;i++)
        {
            cancellation.ThrowIfCancellationRequested(); var buffer=new byte[16384]; buffer[0]=(byte)i;
            checksum+=buffer[0]; expected+=(byte)i;
            if((i&63)==0) GC.Collect(0,GCCollectionMode.Forced,true);
        }
        return count;
    }
    public ValidationResult Validate()=>new(checksum==expected);
    public void Cleanup() { }
}
