using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Harness;

public record HarnessControl(string SchemaVersion,string Action,TargetIdentity Target,RunRequest? Run = null,ProbeControl? Probe=null);
public record HarnessReply(string SchemaVersion,string Status,JsonElement? Payload,string? Code,string? Message);
public record HarnessHandshake(TargetIdentity Target,IReadOnlyList<ScenarioDescriptor> Scenarios,bool Tainted,ProbeHelperDescriptor? ProbeHelper=null,
    RuntimeFootprint? Runtime=null);
public sealed class HarnessHost
{
    private readonly TargetIdentity initialTarget;
    public TargetIdentity Target => initialTarget with { Epoch=NativeCoordination.Epoch(initialTarget.Epoch) };
    private readonly ScenarioRegistry registry;
    private readonly ScenarioRunner runner;
    private readonly IHarnessProbeSupport? probes;
    private bool ownsProcess;
    public HarnessHost(ScenarioRegistry registry,string workspace,IScenarioDispatcher? dispatcher = null,IHarnessProbeSupport? probes=null)
    {
        using var process = Process.GetCurrentProcess();
        long start = ProcessIdentity.StartTimeUtcTicks(process);
        string id = "target-" + Wire.Hash(new { pid=process.Id, start })[..24];
        initialTarget = new(id,process.Id,start,id,RuntimeSupport.Current,RuntimeInformation.ProcessArchitecture.ToString());
        this.registry = registry;
        this.probes=probes;
        runner = new(registry,Target,Path.GetFullPath(workspace),dispatcher);
    }
    public static string PipeName(TargetIdentity target) => "dotnetlab-"+target.ProcessId+"-"+target.Id;
    // A controller-owned process exits on either a shutdown command or controller pipe EOF.
    public async Task RunOwnedAsync()
    {
        ownsProcess=true;
        runner.EnableOwnedProcess();
        using var cancellation=new CancellationTokenSource();
        var listener=ListenAsync(cancellation.Token);
        Console.WriteLine(Wire.Serialize(Target));
        Console.Out.Flush();
        await Console.In.ReadLineAsync();
        cancellation.Cancel();
        await listener;
    }
    public async Task ListenAsync(CancellationToken cancellation)
    {
        var clients = new List<Task>();
        const int maximumClients=8;
        int maximumInstances=maximumClients+1+(OperatingSystem.IsLinux()?1:0);
        // Unix server streams share one listening socket. Retain an owner so
        // a synchronous reply cannot unlink the endpoint between accepts.
        using var listenerOwner=OperatingSystem.IsLinux()?new NamedPipeServerStream(PipeName(Target),PipeDirection.InOut,maximumInstances,
            PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly):null;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var pipe = new NamedPipeServerStream(PipeName(Target),PipeDirection.InOut,maximumInstances,
                    PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                try { await pipe.WaitForConnectionAsync(cancellation); }
                catch(UnauthorizedAccessException) { pipe.Dispose(); continue; }
                catch { pipe.Dispose(); throw; }
                clients.RemoveAll(t => t.IsCompleted);
                if (clients.Count >= maximumClients) { pipe.Dispose(); continue; }
                clients.Add(HandleAsync(pipe,cancellation));
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { await Task.WhenAll(clients); }
    }
    private async Task HandleAsync(NamedPipeServerStream pipe,CancellationToken cancellation)
    {
        await using (pipe)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromMinutes(11));
            try
            {
                var command = await Frames.ReadAsync<HarnessControl>(pipe,deadline.Token);
                if (command.SchemaVersion != Wire.Version) throw new LabException("schema_unsupported","Unsupported harness protocol.");
                if (command.Target.Id != Target.Id || command.Target.StartTimeUtcTicks != Target.StartTimeUtcTicks)
                    throw new LabException("target_identity_changed","Handshake identity differs.");
                object response = command.Action switch
                {
                    "handshake" when command.Run is null&&command.Probe is null => new HarnessHandshake(Target,registry.Describe(),runner.Tainted,probes?.Describe(),FootprintReader.Read() with { OwnedProcess=ownsProcess }),
                    "execute" when command.Run is not null&&command.Probe is null => await runner.ExecuteAsync(command.Run,deadline.Token),
                    "mutation_setup" when command.Run is not null&&command.Probe is null => await runner.SetupAsync(command.Run,deadline.Token),
                    "probe_control" when command.Run is null&&command.Probe is not null => await runner.ControlAsync(
                        ()=>probes?.Control(Target,command.Probe)??throw new LabException("capability_unsupported","Approved probe helper was not loaded through startup integration."),deadline.Token),
                    _ => throw new LabException("invalid_request","Invalid harness action or fields.")
                };
                await Frames.WriteAsync(pipe,new HarnessReply(Wire.Version,"ok",JsonSerializer.SerializeToElement(response,Wire.Json),null,null),deadline.Token);
            }
            catch (LabException e)
            {
                try { await Frames.WriteAsync(pipe,new HarnessReply(Wire.Version,"error",null,e.Code,e.Message),deadline.Token); }
                catch (IOException) { }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or JsonException) { }
        }
    }
}
public sealed class HarnessClient
{
    public async Task<HarnessHandshake> HandshakeAsync(TargetIdentity target,CancellationToken cancellation) =>
        await SendAsync<HarnessHandshake>(target,new(Wire.Version,"handshake",target),cancellation);
    public async Task<Measurement> ExecuteAsync(RunRequest run,CancellationToken cancellation) =>
        await SendAsync<Measurement>(run.Target,new(Wire.Version,"execute",run.Target,run),cancellation);
    public async Task<ValidationResult> SetupAsync(RunRequest run,CancellationToken cancellation)=>
        await SendAsync<ValidationResult>(run.Target,new(Wire.Version,"mutation_setup",run.Target,run),cancellation);
    public async Task<System.Text.Json.JsonElement> ProbeAsync(TargetIdentity target,ProbeControl request,CancellationToken cancellation)=>
        await SendAsync<System.Text.Json.JsonElement>(target,new(Wire.Version,"probe_control",target,Probe:request),cancellation);
    private static async Task<T> SendAsync<T>(TargetIdentity target,HarnessControl command,CancellationToken cancellation)
    {
        await using var pipe = new NamedPipeClientStream(".",HarnessHost.PipeName(target),PipeDirection.InOut,
            PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var connect = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        connect.CancelAfter(TimeSpan.FromSeconds(3));
        try { await pipe.ConnectAsync(connect.Token); }
        catch(UnauthorizedAccessException)
        { throw new LabException("permission_denied","Cooperative harness endpoint belongs to a different user or denies access."); }
        catch (OperationCanceledException) when (!cancellation.IsCancellationRequested)
        { throw new LabException("capability_unsupported","Cooperative harness endpoint is unavailable."); }
        PeerIdentity.VerifyServer(pipe,target);
        try
        {
        await Frames.WriteAsync(pipe,command,cancellation);
        var response = await Frames.ReadAsync<HarnessReply>(pipe,cancellation);
        if (response.SchemaVersion != Wire.Version) throw new LabException("schema_unsupported","Harness replied with unsupported schema.");
        if (response.Status != "ok") throw new LabException(response.Code ?? "scenario_failed",response.Message ?? "Harness failed.");
        if(response.Payload is null) throw new LabException("invalid_harness_response","Harness success response has no payload.");
        return response.Payload.Value.Deserialize<T>(Wire.Json)??throw new LabException("invalid_harness_response","Harness payload is null.");
        }
        catch(IOException)
        { throw new LabException("harness_disconnected","Harness disconnected; command completion and quiescence require reconciliation before another execution."); }
        catch(JsonException)
        { throw new LabException("invalid_harness_response","Harness response cannot establish command completion or quiescence."); }
    }
}
