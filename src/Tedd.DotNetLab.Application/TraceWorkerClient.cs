using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Application;

public interface ITraceParserClient : IAsyncDisposable
{
    Task<TraceSummary> AnalyzeAsync(TraceWorkerRequest request,CancellationToken cancellation=default);
    Task<(StackParserResult Result,TraceParserExecution Execution)> AnalyzeStacksAsync(TraceWorkerRequest request,CancellationToken cancellation=default);
}
public sealed class TraceWorkerClient(string? workerAssemblyPath=null,IReadOnlyList<string>? workerArguments=null,
    Action<int,long>? workerStarted=null,Action<int,long>? workerTerminated=null) : ITraceParserClient
{
    public const string Version="isolated-trace-parser/windows-job/v1";
    private readonly SemaphoreSlim workerGate=new(1,1);
    private readonly CancellationTokenSource stopping=new();
    public async Task<TraceSummary> AnalyzeAsync(TraceWorkerRequest request,CancellationToken cancellation=default)
    {
        if(request.Kind!="runtime"||request.StackPolicy is not null) throw new LabException("invalid_request","Expected a runtime parser request.");
        var output=await RunAsync(request,cancellation); cancellation.ThrowIfCancellationRequested();
        var summary=Wire.Deserialize<TraceSummary>(System.Text.Encoding.UTF8.GetString(output.Bytes));
        if(summary.CaptureId!=request.Capture.Id||summary.ArtifactId!=request.SourceArtifact.Id||summary.ProfileHash!=request.Capture.Profile.Hash||summary.ParserVersion!=TraceAnalyzer.Version||summary.ParserExecution is not null)
            throw new LabException("invalid_parser_response","Trace worker summary source provenance differs.");
        return summary with { ParserExecution=output.Execution };
    }
    public async Task<(StackParserResult Result,TraceParserExecution Execution)> AnalyzeStacksAsync(TraceWorkerRequest request,CancellationToken cancellation=default)
    {
        if(request.Kind!="stacks"||request.StackPolicy is null) throw new LabException("invalid_request","Expected a stack parser request.");
        request.StackPolicy.Validate();
        if(request.StackPolicy.MaximumEvents>request.Policy.MaximumEvents||request.StackPolicy.MaximumRawBytes>request.Policy.MaximumInputBytes)
            throw new LabException("resource_limit","Stack policy exceeds its worker bounds.");
        var output=await RunAsync(request,cancellation); cancellation.ThrowIfCancellationRequested();
        var result=Wire.Deserialize<StackParserResult>(System.Text.Encoding.UTF8.GetString(output.Bytes));
        if(result.CaptureId!=request.Capture.Id||result.TraceArtifactId!=request.SourceArtifact.Id||result.ProfileHash!=request.Capture.Profile.Hash||result.ParserVersion!=StackParser.Version||Wire.Hash(result.Policy)!=Wire.Hash(request.StackPolicy))
            throw new LabException("invalid_parser_response","Stack worker source or policy provenance differs.");
        return (result,output.Execution);
    }
    private async Task<(byte[] Bytes,TraceParserExecution Execution)> RunAsync(TraceWorkerRequest request,CancellationToken cancellation)
    {
        request.Policy.Validate();
        string output=VerifyOutputPath(request);
        if(File.Exists(output)) throw new LabException("invalid_path","Trace worker output must be a fresh controller-owned name.");
        string? scratch=request.Kind=="stacks"?Path.Combine(Path.GetDirectoryName(output)!,"stack-conversion-"+Path.GetFileNameWithoutExtension(output)):null;
        if(scratch is not null&&Directory.Exists(scratch)) throw new LabException("invalid_path","Stack scratch must be a fresh worker-owned directory.");
        if(!(OperatingSystem.IsWindows()||OperatingSystem.IsLinux())||RuntimeInformation.ProcessArchitecture!=Architecture.X64)
            throw new LabException("capability_unsupported","Isolated trace parser containment requires separately qualified OS support.");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellation,stopping.Token);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.Policy.MaximumDurationSeconds));
        bool entered=false,ownsNamespace=false,exitVerified=false;
        try
        {
            await workerGate.WaitAsync(deadline.Token); entered=true;
            if(File.Exists(output)||scratch is not null&&Directory.Exists(scratch)) throw new LabException("invalid_path","Queued parser output is no longer fresh.");
            string worker=Path.GetFullPath(workerAssemblyPath??Path.Combine(AppContext.BaseDirectory,"Tedd.DotNetLab.TraceWorker.dll"));
            if(!File.Exists(worker)||new FileInfo(worker).Length>16777216) throw new LabException("capability_unsupported","Packaged trace worker is unavailable.");
            string workerHash=Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(worker,deadline.Token)));
            string dotnet=Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet"));
            var start=new ProcessStartInfo(dotnet) { WorkingDirectory=Path.GetDirectoryName(request.OutputPath)!,UseShellExecute=false,
                RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true };
            RuntimeSupport.AddRuntimeArguments(start); start.ArgumentList.Add(worker); foreach(string argument in workerArguments??[]) start.ArgumentList.Add(argument);
            start.Environment.Clear();
            foreach(string key in new[]{"SystemRoot","WINDIR","ComSpec"})
                if(Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key]=value;
            start.Environment["TEMP"]=start.WorkingDirectory; start.Environment["TMP"]=start.WorkingDirectory;
            start.Environment["DOTNET_EnableDiagnostics"]="0"; start.Environment["DOTNET_DbgEnableMiniDump"]="0";
            start.Environment["DOTNET_GCHeapHardLimit"]=(request.Policy.MaximumCommittedMemoryBytes/2).ToString("x",System.Globalization.CultureInfo.InvariantCulture);
            ParserJob? containment=null; LinuxParserGroup? linux=null; Task<string>? stderr=null;
            using var process=new Process { StartInfo=start };
            bool started=false; long temporaryPeak=0;
            using var monitorStop=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token); Task? monitor=null;
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
                if(OperatingSystem.IsLinux())
                {
                    linux=new(request.Policy.MaximumCommittedMemoryBytes);
                    start.FileName="/bin/sh"; start.ArgumentList.Clear();
                    start.ArgumentList.Add("-c"); start.ArgumentList.Add("printf '%s' \"$$\" > \"$1/cgroup.procs\" || exit 125; shift; exec \"$@\"");
                    start.ArgumentList.Add("dotnetlab-parser-launch"); start.ArgumentList.Add(linux.Path); start.ArgumentList.Add(dotnet);
                    RuntimeSupport.AddRuntimeArguments(start);
                    start.ArgumentList.Add(worker); foreach(string argument in workerArguments??[]) start.ArgumentList.Add(argument);
                    start.Environment["TMPDIR"]=start.WorkingDirectory;
                    using var controller=Process.GetCurrentProcess();
                    start.Environment["DOTNETLAB_PARSER_PARENT_PID"]=controller.Id.ToString(System.Globalization.CultureInfo.InvariantCulture);
                    start.Environment["DOTNETLAB_PARSER_PARENT_START"]=ProcessIdentity.StartTimeUtcTicks(controller).ToString(System.Globalization.CultureInfo.InvariantCulture);
                    start.Environment["DOTNETLAB_PARSER_OWNED_GROUP"]=linux.Path;
                }
                if(!process.Start()) throw new LabException("parser_failed","Trace worker did not start.");
                started=true; ownsNamespace=true; long startTicks=ProcessIdentity.StartTimeUtcTicks(process);
                stderr=DrainErrorAsync(process.StandardError);
                if(OperatingSystem.IsWindows()) containment=new(process,request.Policy.MaximumCommittedMemoryBytes);
                if(linux is not null) await linux.VerifyProcessAsync(process.Id,deadline.Token);
                workerStarted?.Invoke(process.Id,startTicks);
                await Frames.WriteAsync(process.StandardInput.BaseStream,request,deadline.Token); process.StandardInput.Close();
                var replyTask=Frames.ReadAsync<TraceWorkerReply>(process.StandardOutput.BaseStream,deadline.Token);
                if(scratch is not null)
                {
                    monitor=MonitorTemporaryAsync(scratch,request.StackPolicy!.MaximumTemporaryBytes,bytes=>temporaryPeak=Math.Max(temporaryPeak,bytes),monitorStop.Token);
                    if(await Task.WhenAny(replyTask,monitor)==monitor) await monitor;
                }
                var reply=await replyTask;
                await process.WaitForExitAsync(deadline.Token);
                monitorStop.Cancel();
                if(monitor is not null) { try { await monitor; } catch(OperationCanceledException) when(monitorStop.IsCancellationRequested) { } }
                if(reply.SchemaVersion!=Wire.Version||reply.ProcessId!=process.Id) throw new LabException("invalid_parser_response","Trace worker identity or schema differs.");
                if(reply.Status!="ok") throw new LabException(reply.Code??"parser_failed","Isolated parser rejected its input or exceeded a resource budget.");
                if(process.ExitCode!=0||reply.ProcessStartUtcTicks!=startTicks||reply.OutputBytes is <1||reply.OutputBytes>request.Policy.MaximumOutputBytes||
                    reply.CpuTicks<0||reply.PeakWorkingSetBytes<0) throw new LabException("invalid_parser_response","Trace worker did not verify a complete bounded result.");
                long peak=OperatingSystem.IsWindows()?containment!.PeakCommittedBytes():linux!.PeakBytes();
                if(peak>request.Policy.MaximumCommittedMemoryBytes) throw new LabException("parser_memory_limit","Worker committed memory exceeded its configured limit.");
                var file=new FileInfo(request.OutputPath);
                if(!file.Exists||file.Length!=reply.OutputBytes||(file.Attributes&FileAttributes.ReparsePoint)!=0) throw new LabException("artifact_corrupt","Trace worker output is missing or differs.");
                byte[] bytes=await File.ReadAllBytesAsync(file.FullName,deadline.Token);
                if(Convert.ToHexStringLower(SHA256.HashData(bytes))!=reply.OutputHash) throw new LabException("artifact_corrupt","Trace worker output hash differs.");
                return (bytes,new(linux is null?Version:"isolated-trace-parser/linux-cgroup-v2/v1",process.Id,startTicks,workerHash,request.Policy,reply.CpuTicks,TimeSpan.TicksPerSecond,reply.PeakWorkingSetBytes,linux is null?peak:0,true,
                    Containment:linux is null?"windows_job_process_commit_limit_single_process_kill_on_close":"linux_cgroup_v2_memory_max_swap_zero_oom_group_pids_256_kill_and_empty_verify",
                    Meaning:linux is null?"Worker CPU and observed peak working set/committed memory; excludes target costs. Wall deadline includes launch, IPC, parsing, and verified shutdown.":"Worker CPU and kernel charged memory; Windows private commit is not collected. Charged limit includes anon/file/kernel memory; kernel transient overshoot remains possible. Startup, IPC, parsing, and verified shutdown are within the wall deadline.",
                    ObservedPeakTemporaryBytes:scratch is null?null:temporaryPeak,TemporaryByteEnforcement:scratch is null?null:"sampled_watchdog_and_final_worker_check; transient_write_overshoot_possible",
                    PeakAccountedMemoryBytes:linux is null?null:peak,MemoryAccounting:linux is null?null:LinuxParserGroup.Accounting,
                    MemoryLimitEvents:linux?.Event("max"),OomKills:linux?.Event("oom_kill"),PrivateCommitCollected:linux is null?null:false));
            }
            catch(Exception error) when(error is System.IO.IOException or System.Text.Json.JsonException)
            { throw new LabException(linux?.Event("oom_kill")>0?"parser_memory_limit":"parser_failed","Trace worker terminated or returned invalid protocol data."); }
            finally
            {
                try
                {
                    if(linux is not null) await linux.StopAsync();
                    if(started&&!process.HasExited) process.Kill(true);
                    if(started) await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    exitVerified=started;
                    if(stderr is not null) await stderr.WaitAsync(TimeSpan.FromSeconds(2));
                    if(started) workerTerminated?.Invoke(process.Id,OperatingSystem.IsWindows()?containment?.PeakCommittedBytes()??0:linux?.PeakBytes()??0);
                }
                catch(Exception error) when(error is not OutOfMemoryException)
                { throw new LabException("parser_shutdown_unverified","Trace worker shutdown could not be verified."); }
                finally
                {
                    if(OperatingSystem.IsWindows()) containment?.Dispose(); linux?.Dispose(); monitorStop.Cancel();
                    if(monitor is not null) { try { await monitor; } catch(Exception error) when(error is not OutOfMemoryException) { } }
                }
            }
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested&&!stopping.IsCancellationRequested)
        { throw new LabException("parser_timeout","Trace parser exceeded its wall deadline and was stopped."); }
        finally
        {
            try
            {
                // Queued/rejected requests cannot delete another invocation's active files.
                if(ownsNamespace&&exitVerified)
                {
                    if(File.Exists(output)&&VerifyOutputPath(request)==output) File.Delete(output);
                    if(scratch is not null&&Directory.Exists(scratch))
                    {
                        VerifyOutputPath(request);
                        await Tedd.DotNetLab.Artifacts.OwnedDirectoryCleanup.DeleteAsync(Path.Combine(Path.GetFullPath(request.Root),"incoming"),scratch);
                    }
                }
            }
            finally { if(entered) workerGate.Release(); }
        }
    }
    private static async Task MonitorTemporaryAsync(string scratch,long maximum,Action<long> observed,CancellationToken cancellation)
    {
        while(true)
        {
            cancellation.ThrowIfCancellationRequested(); long bytes=0; int files=0;
            if(Directory.Exists(scratch))
            {
                if((File.GetAttributes(scratch)&FileAttributes.ReparsePoint)!=0) throw new LabException("invalid_path","Stack scratch became a reparse point.");
                foreach(string path in Directory.EnumerateFileSystemEntries(scratch))
                {
                    if(++files>128) throw new LabException("resource_limit","Stack scratch file cardinality exceeds its budget.");
                    try
                    {
                        var file=new FileInfo(path);
                        if((file.Attributes&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0) throw new LabException("invalid_path","Unexpected stack scratch entry.");
                        bytes=checked(bytes+file.Length);
                    }
                    catch(FileNotFoundException) { }
                }
            }
            observed(bytes); if(bytes>maximum) throw new LabException("resource_limit","Stack scratch exceeds its sampled byte quota.");
            await Task.Delay(25,cancellation);
        }
    }
    private static string VerifyOutputPath(TraceWorkerRequest request)
    {
        string full=Path.GetFullPath(request.OutputPath),prefix=Path.Combine(Path.GetFullPath(request.Root),"incoming")+Path.DirectorySeparatorChar;
        if(!full.StartsWith(prefix,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||!full.EndsWith(".partial",StringComparison.Ordinal))
            throw new LabException("invalid_path","Trace worker output escaped its incoming namespace.");
        for(string? current=Path.GetDirectoryName(full);current is not null;current=Path.GetDirectoryName(current))
            if(Directory.Exists(current)&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new LabException("invalid_path","Trace worker output cannot traverse reparse points.");
        return full;
    }
    private static async Task<string> DrainErrorAsync(StreamReader reader)
    {
        var retained=new System.Text.StringBuilder(); char[] buffer=new char[2048]; int count;
        while((count=await reader.ReadAsync(buffer))>0) if(retained.Length<8192) retained.Append(buffer,0,Math.Min(count,8192-retained.Length));
        return retained.ToString();
    }
    public async ValueTask DisposeAsync()
    {
        stopping.Cancel();
        if(!await workerGate.WaitAsync(TimeSpan.FromSeconds(20))) throw new LabException("parser_shutdown_unverified","Active trace parser did not finish shutdown.");
        workerGate.Release();
    }
}
