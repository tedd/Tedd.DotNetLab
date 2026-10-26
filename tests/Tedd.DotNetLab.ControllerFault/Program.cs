using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Transport.Local;
using Microsoft.Diagnostics.NETCore.Client;

if(args.Length==1&&args[0]=="--measurement-mutex-worker")
{
    using var permit=await MachineMutex.AcquireAsync(MachineMutex.MeasurementName);
    Console.WriteLine("acquired");
    await Console.In.ReadLineAsync(); return;
}

static string OwnedTestsRoot()
{
    string build=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
    string? designated=OperatingSystem.IsWindows()&&Directory.Exists(@"D:\Workspaces\AI")?@"D:\Workspaces\AI\":
        OperatingSystem.IsLinux()&&Directory.Exists("/mnt/d/Workspaces/AI")?"/mnt/d/Workspaces/AI/":null;
    if(designated is not null&&!build.StartsWith(designated,StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Fault fixture build must be in a designated AI workspace.");
    return Path.Combine(build,"tests")+Path.DirectorySeparatorChar;
}

if(args.Length==1&&args[0]=="--profiler-attach-worker")
{
    var invocation=await Frames.ReadAsync<ProfilerAttachmentInvocation>(Console.OpenStandardInput(),CancellationToken.None);
    string root=Path.GetFullPath(invocation.Workspace);
    if(!OperatingSystem.IsWindows()||!root.StartsWith(OwnedTestsRoot(),StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Attachment fault fixture requires an owned Windows test workspace.");
    string mode=await File.ReadAllTextAsync(Path.Combine(root,"attachment-fault-mode"));
    if(mode=="lost_ack")
    {
        using var binary=ProfilerAttachmentIdentity.OpenBinary(invocation.BinaryPath);
        if(ProfilerAttachmentIdentity.Hash(binary)!=invocation.BinaryHash) throw new InvalidOperationException("Fault fixture binary changed.");
        byte[] metadata=System.Text.Encoding.UTF8.GetBytes(Wire.Serialize(new { schema_version=Wire.Version,target_id=invocation.Target.Id,
            process_id=invocation.Target.ProcessId,start_time_utc_ticks=invocation.Target.StartTimeUtcTicks,workspace=root }));
        new DiagnosticsClient(invocation.Target.ProcessId).AttachProfiler(TimeSpan.FromSeconds(10),Guid.Parse(ProfilerAttachmentIdentity.ProfilerId),invocation.BinaryPath,metadata);
    }
    else if(mode!="stall") throw new InvalidOperationException("Unknown attachment fault mode.");
    await File.WriteAllTextAsync(Path.Combine(root,"attachment-fault-ready"),Wire.Serialize(new { process_id=Environment.ProcessId }));
    await Task.Delay(Timeout.Infinite); return;
}

if(args.Length==2&&args[0]=="--parser-controller")
{
    var parserRequest=Wire.Deserialize<TraceWorkerRequest>(await File.ReadAllTextAsync(args[1]));
    string ownedRoot=OwnedTestsRoot();
    if(!OperatingSystem.IsLinux()||!Path.GetFullPath(parserRequest.Root).StartsWith(ownedRoot,StringComparison.Ordinal))
        throw new InvalidOperationException("Parser controller fixture requires an owned Linux test workspace.");
    string fault=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..","Tedd.DotNetLab.ParserFault","Release","net10.0","Tedd.DotNetLab.ParserFault.dll"));
    int starts=0;
    await using var parser=new TraceWorkerClient(fault,["descendants"],(_,_)=>starts++);
    try { await parser.AnalyzeAsync(parserRequest); }
    catch(LabException error) { Console.WriteLine(Wire.Serialize(new { code=error.Code,starts })); }
    return;
}

var request=Wire.Deserialize<FaultRequest>(await File.ReadAllTextAsync(args.Single()));
string workspace=Path.GetFullPath(request.Workspace);
string taskTests=OwnedTestsRoot();
if(!workspace.StartsWith(taskTests,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
    throw new InvalidOperationException("Crash fixture requires a task-owned disposable test catalog.");
var service=new LabService(workspace,mutationJournalCommitted:item=>
{
    if(item.Phase==request.Checkpoint)
    {
        Console.WriteLine(Wire.Serialize(new { transaction_id=item.TransactionId,checkpoint=item.Phase }));
        Console.Out.Flush();
        // Immediate process exit deliberately skips coordinator rollback and catalog lease release.
        Environment.Exit(71);
    }
});
Operation operation=request.RecoverTransactionId is not null
    ?await service.RecoverMutationAsync(request.RecoverTransactionId,request.Epoch,request.GrantId,request.ExecutionGrantId,"crash-recover")
    :request.TransactionId is null
    ?await service.ApplyCilAsync(request.TargetId,request.Epoch,request.CandidateId,request.GrantId,request.ExecutionGrantId,request.ScenarioId,"crash-apply")
    :await service.RestoreCilAsync(request.TransactionId,request.Epoch,request.GrantId,request.ExecutionGrantId,"crash-restore");
while(!Lifecycle.Terminal((operation=service.Status(operation.Id)).State)) await Task.Delay(10);
// Join the worker before publishing fallback output; crash checkpoints precede terminal publication.
await service.DisposeAsync();
Console.WriteLine(Wire.Serialize(operation)); Environment.ExitCode=72;

record FaultRequest(string Workspace,string TargetId,long Epoch,string CandidateId,string GrantId,string ExecutionGrantId,
    string ScenarioId,string Checkpoint,string? TransactionId=null,string? RecoverTransactionId=null);
