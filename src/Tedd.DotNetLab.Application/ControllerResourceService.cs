using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;

namespace Tedd.DotNetLab.Application;

public record ControllerResourceSnapshot(string Id,string? OperationId,string Phase,int ProcessId,long ProcessStartUtcTicks,
    long TimestampTicks,long Frequency,long CpuTicks,long CpuFrequency,long WorkingSetBytes,long PrivateMemoryBytes,long ManagedHeapBytes,
    long ApproximateTotalAllocatedBytes,int ThreadCount,int OtherActiveOperations,string Placement,
    string Scope="controller_process",string Meaning="Process-wide point/cumulative counters; other controller jobs, runtime work, and logging are included. No target metric or assumed overhead subtraction.");
public record ControllerResourceEvidence(string OperationId,Availability Availability,ControllerResourceSnapshot? Before,ControllerResourceSnapshot? After,
    long? IntervalTicks,long? CpuDeltaTicks,long? ApproximateAllocatedDeltaBytes,IReadOnlyList<string> Warnings,
    string Meaning="Snapshots bracket controller orchestration outside this operation's own target timer; concurrent operations may overlap. Memory values are endpoint samples, not peak usage.");

public sealed partial class LabService
{
    public ControllerResourceSnapshot ControllerResources()
    {
        return ReadControllerResourceSnapshot(Wire.Id("controller-snapshot"),null,"explicit_query",jobs.Count,
            jobs.IsEmpty?"controller_idle_query":"explicit_query_target_timing_may_overlap");
    }
    private static ControllerResourceSnapshot ReadControllerResourceSnapshot(string id,string? operationId,string phase,int others,string placement)
    {
        using var process=Process.GetCurrentProcess(); process.Refresh();
        return new(id,operationId,phase,process.Id,process.StartTime.ToUniversalTime().Ticks,Stopwatch.GetTimestamp(),Stopwatch.Frequency,
            process.TotalProcessorTime.Ticks,TimeSpan.TicksPerSecond,process.WorkingSet64,process.PrivateMemorySize64,GC.GetTotalMemory(false),
            GC.GetTotalAllocatedBytes(precise:false),process.Threads.Count,others,placement);
    }
    private void RecordControllerResources(string operationId,string phase,bool quiescent)
    {
        if(!collectControllerResources) return;
        string id=operationId+"-controller-"+phase;
        try
        {
            if(!quiescent)
            {
                Catalog.Put(id,"controller_resource_unavailable",new { operation_id=operationId,phase,availability="not_collected",
                    reason="target_quiescence_unknown; no endpoint sample was taken" }); return;
            }
            int others=jobs.Keys.Count(key=>key!=operationId);
            var snapshot=ReadControllerResourceSnapshot(id,operationId,phase,others,
                others==0?"outside_own_target_timing":"outside_own_target_timing_other_operations_may_overlap");
            Catalog.Put(id,"controller_resource_snapshot",snapshot);
        }
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            // Optional diagnostics must not alter mutation submission/restoration semantics.
            try { Catalog.Put(id,"controller_resource_unavailable",new { operation_id=operationId,phase,availability="not_collected",reason=error.GetType().Name }); }
            catch(Exception ignored) when(ignored is not OutOfMemoryException) { }
        }
    }
    public async Task<ControllerResourceEvidence> ControllerResourcesAsync(string operationId,CancellationToken cancellation=default)
    {
        var operation=Catalog.GetOperation(operationId);
        if(Lifecycle.Terminal(operation.State)&&jobs.TryGetValue(operationId,out var job))
        {
            try { await job.Work.WaitAsync(TimeSpan.FromSeconds(2),cancellation); }
            catch(TimeoutException) { }
        }
        ControllerResourceSnapshot? Read(string phase)
        {
            try
            {
                var record=Catalog.Get(operationId+"-controller-"+phase);
                return record.Kind=="controller_resource_snapshot"?Wire.Deserialize<ControllerResourceSnapshot>(record.Json):null;
            }
            catch(LabException error) when(error.Code=="record_not_found") { return null; }
        }
        var before=Read("before"); var after=Read("after"); var warnings=new List<string>();
        if(before is null) warnings.Add("before_snapshot_not_collected"); if(after is null) warnings.Add("after_snapshot_not_collected_or_pending");
        if(before?.OtherActiveOperations>0||after?.OtherActiveOperations>0) warnings.Add("concurrent_controller_work_included");
        if(before is null||after is null) return new(operationId,Availability.NotCollected,before,after,null,null,null,warnings);
        if(before.ProcessId!=after.ProcessId||before.ProcessStartUtcTicks!=after.ProcessStartUtcTicks||before.Frequency!=after.Frequency||before.CpuFrequency!=after.CpuFrequency||
            after.TimestampTicks<before.TimestampTicks||after.CpuTicks<before.CpuTicks||after.ApproximateTotalAllocatedBytes<before.ApproximateTotalAllocatedBytes)
            return new(operationId,Availability.Invalid,before,after,null,null,null,["controller_counter_identity_or_monotonicity_mismatch"]);
        return new(operationId,warnings.Count==0?Availability.Available:Availability.Partial,before,after,after.TimestampTicks-before.TimestampTicks,
            after.CpuTicks-before.CpuTicks,after.ApproximateTotalAllocatedBytes-before.ApproximateTotalAllocatedBytes,warnings);
    }
}
