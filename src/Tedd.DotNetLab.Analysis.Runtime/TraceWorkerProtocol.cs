using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Runtime;

public record TraceParserPolicy(long MaximumInputBytes=2147483648,long MaximumCommittedMemoryBytes=536870912,
    int MaximumDurationSeconds=300,int MaximumEvents=1000000,long MaximumOutputBytes=16777216)
{
    public void Validate()
    {
        if(MaximumInputBytes is <1 or >2147483648||MaximumCommittedMemoryBytes is <134217728 or >2147483648||
            MaximumDurationSeconds is <1 or >600||MaximumEvents is <1 or >1000000||MaximumOutputBytes is <1024 or >16777216)
            throw new LabException("invalid_plan","Trace parser resource policy exceeds its supported bounds.");
    }
}
public record TraceWorkerRequest(string SchemaVersion,string Root,string SourcePath,Artifact SourceArtifact,Capture Capture,
    string OutputPath,TraceParserPolicy Policy,string Kind="runtime",StackParserPolicy? StackPolicy=null);
public record TraceWorkerReply(string SchemaVersion,string Status,string? Code,string? OutputHash,long OutputBytes,int ProcessId,
    long ProcessStartUtcTicks,long CpuTicks,long PeakWorkingSetBytes);
public record TraceParserExecution(string WorkerVersion,int ProcessId,long ProcessStartUtcTicks,string WorkerAssemblyHash,
    TraceParserPolicy Policy,long CpuTicks,long CpuFrequency,long PeakWorkingSetBytes,long PeakCommittedBytes,bool ExitVerified,
    string Containment="windows_job_process_commit_limit_single_process_kill_on_close",
    string Scope="isolated_trace_parser",string Meaning="Worker CPU and observed peak working set/committed memory; excludes target costs. Wall deadline includes launch, IPC, parsing, and verified shutdown.",
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? ObservedPeakTemporaryBytes=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? TemporaryByteEnforcement=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? PeakAccountedMemoryBytes=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] string? MemoryAccounting=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? MemoryLimitEvents=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? OomKills=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? PrivateCommitCollected=null);
