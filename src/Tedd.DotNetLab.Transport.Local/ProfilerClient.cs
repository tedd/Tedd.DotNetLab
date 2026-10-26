using System.IO.Pipes;
using System.Text.Json;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Transport.Local;

public record ProfilerRequest(string SchemaVersion,string Action,string TargetId,int ProcessId,long StartTimeUtcTicks,
    long ExpectedTargetEpoch);
public record ProfilerReply(string SchemaVersion,string Status,JsonElement? Data,string? Code,string? Message);
public record ProfilerHandshake(string SchemaVersion,string TargetId,int ProcessId,long StartTimeUtcTicks,
    string Runtime,string RuntimeString,string Architecture,long Epoch,bool Startup,string Workspace,string Interface,
    string NativeInspection,string Rejit,string Probes,string Restore,bool ExecutionActive=false,string InlinerHandling="unsupported",
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] bool? AttachmentComplete=null,
    [property:System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] uint? EventMaskLow=null);
public sealed class ProfilerClient
{
    public static string PipeName(TargetIdentity target)=>"dotnetlab-profiler-"+target.ProcessId+"-"+target.StartTimeUtcTicks;
    public async Task<ProfilerHandshake> HandshakeAsync(TargetIdentity target,CancellationToken cancellation)
    {
        var reply=await SendAsync(target,new ProfilerRequest(Wire.Version,"handshake",target.Id,target.ProcessId,
            target.StartTimeUtcTicks,target.Epoch),cancellation);
        return reply.Deserialize<ProfilerHandshake>(Wire.Json)!;
    }
    public async Task<JsonElement> SendAsync<T>(TargetIdentity target,T request,CancellationToken cancellation)
    {
        await using var pipe=new NamedPipeClientStream(".",PipeName(target),PipeDirection.InOut,
            PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        try { await pipe.ConnectAsync(timeout.Token); }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested)
        { throw new LabException("capability_unsupported","Native profiler endpoint is unavailable."); }
        try
        {
            PeerIdentity.VerifyServer(pipe,target);
            await Frames.WriteAsync(pipe,request,timeout.Token);
            var response=await Frames.ReadAsync<ProfilerReply>(pipe,timeout.Token);
            await pipe.WriteAsync(new byte[]{1},timeout.Token);
            if(response.SchemaVersion!=Wire.Version) throw new LabException("schema_unsupported","Native response version differs.");
            if(response.Status!="ok") throw new LabException(response.Code??"profiler_failed",response.Message??"Native operation failed.");
            return response.Data??throw new LabException("invalid_profiler_response","Native success reply has no data.");
        }
        catch(OperationCanceledException) when(!cancellation.IsCancellationRequested)
        { throw new LabException("profiler_timeout","Native reply exceeded its deadline; submission state must be reconciled from the journal."); }
        catch(IOException)
        { throw new LabException("profiler_disconnected","Native endpoint disconnected; an outstanding mutation is not assumed unapplied."); }
        catch(JsonException)
        { throw new LabException("invalid_profiler_response","Native reply does not match the protocol schema."); }
    }
}
