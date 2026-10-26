using System.Diagnostics;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Transport.Local;

namespace Tedd.DotNetLab.Host;

public static class SharedController
{
    public static string PipeName(string workspace)
    {
        string root=Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace));
        if(OperatingSystem.IsWindows()) root=root.ToUpperInvariant();
        string identity=Environment.UserDomainName+"\\"+Environment.UserName+"\n"+root;
        return "dotnetlab-controller-"+Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..32];
    }

    private static NamedPipeClientStream Client(string pipe)=>new(".",pipe,PipeDirection.InOut,
        PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);

    public static async Task ConnectAsync(string workspace,bool nativeExtension,CancellationToken cancellation=default)
    {
        string name=PipeName(workspace);
        ConnectionLog.Event("connector_starting",new { pipe=name,native_extension=nativeExtension });
        using var startup=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        startup.CancelAfter(TimeSpan.FromSeconds(15));
        NamedPipeClientStream pipe;
        // Startup is serialized independently of measurement admission. Only the
        // controller opens the catalog; MCP connector processes never do so.
        using(await MachineMutex.AcquireAsync(name+"-startup",startup.Token))
        {
            pipe=Client(name);
            try { await pipe.ConnectAsync(150,startup.Token); }
            catch(TimeoutException)
            {
                ConnectionLog.Event("controller_start_required");
                pipe.Dispose(); pipe=Client(name);
                var start=RuntimeSupport.ControllerStartInfo();
                start.ArgumentList.Add(typeof(SharedController).Assembly.Location);
                start.ArgumentList.Add("--shared-bootstrap"); start.ArgumentList.Add(Path.GetFullPath(workspace));
                start.RedirectStandardInput=true; start.RedirectStandardOutput=true; start.RedirectStandardError=true;
                using var controller=Process.Start(start)??throw new LabException("controller_unavailable","Shared controller could not start.");
                ConnectionLog.Event("bootstrap_started",new { bootstrap_process_id=controller.Id });
                controller.StandardInput.Close();
                var errors=controller.StandardError.ReadToEndAsync(startup.Token);
                try
                {
                    // Let the short-lived bootstrap exit before handing the client
                    // a session. MCP clients may kill their connector's process tree.
                    await controller.WaitForExitAsync(startup.Token);
                    ConnectionLog.Event("bootstrap_exited",new { bootstrap_process_id=controller.Id,exit_code=controller.ExitCode });
                    if(controller.ExitCode!=0) throw new IOException(await errors);
                    await pipe.ConnectAsync(12000,startup.Token);
                }
                catch(Exception error)
                {
                    ConnectionLog.Event("controller_start_failed",error:error);
                    pipe.Dispose();
                    string detail=controller.HasExited?await errors:"Shared controller startup timed out.";
                    throw new LabException("controller_unavailable",detail.Length<=4096?detail:detail[..4096]);
                }
            }
            catch { pipe.Dispose(); throw; }
        }
        using(pipe)
        using(var connection=CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            ConnectionLog.Event("connector_connected",new { controller_process_id=ConnectionLog.Peer(pipe.SafePipeHandle,true) });
            await pipe.WriteAsync(new byte[]{nativeExtension?(byte)1:(byte)0},connection.Token);
            var incoming=CopyAsync(Console.OpenStandardInput(),pipe,"client_input",connection.Token);
            var outgoing=CopyAsync(pipe,Console.OpenStandardOutput(),"controller_output",connection.Token);
            var finished=await Task.WhenAny(incoming,outgoing);
            ConnectionLog.Event("connector_closing",new { trigger=finished==incoming?"client_input":"controller_output",state=finished.Status.ToString(),cancellation_requested=cancellation.IsCancellationRequested });
            connection.Cancel(); pipe.Dispose();
            // A console read may remain blocked when the remote controller exits.
            // Do not wait for client input after the remote output has closed.
            try { await outgoing; }
            catch(Exception error) when(error is OperationCanceledException or IOException or ObjectDisposedException) { }
            _=incoming.ContinueWith(task=>_ = task.Exception,TaskContinuationOptions.OnlyOnFaulted);
            ConnectionLog.Event("connector_closed");
        }
    }

    private static async Task CopyAsync(Stream source,Stream destination,string direction,CancellationToken cancellation)
    {
        long bytes=0;
        string stage="read";
        try
        {
            byte[] buffer=new byte[81920];
            while(true)
            {
                stage="read";
                int read=await source.ReadAsync(buffer,cancellation);
                if(read==0) break;
                stage="write";
                await destination.WriteAsync(buffer.AsMemory(0,read),cancellation); bytes+=read;
            }
            ConnectionLog.Event("stream_eof",new { direction,bytes });
        }
        catch(Exception error)
        {
            ConnectionLog.Event("stream_failed",new { direction,stage,bytes,cancellation_requested=cancellation.IsCancellationRequested },error);
            throw;
        }
    }

    public static async Task BootstrapAsync(string workspace)
    {
        var start=RuntimeSupport.ControllerStartInfo();
        start.ArgumentList.Add(typeof(SharedController).Assembly.Location);
        start.ArgumentList.Add("--shared-controller"); start.ArgumentList.Add(Path.GetFullPath(workspace));
        start.RedirectStandardInput=true; start.RedirectStandardOutput=true; start.RedirectStandardError=true;
        using var process=Process.Start(start)??throw new LabException("controller_unavailable","Shared controller could not start.");
        ConnectionLog.Event("controller_spawned",new { controller_process_id=process.Id });
        process.StandardInput.Close();
        var errors=process.StandardError.ReadToEndAsync();
        using var ready=Client(PipeName(workspace));
        try { await ready.ConnectAsync(10000); ConnectionLog.Event("controller_ready",new { controller_process_id=process.Id }); }
        catch(TimeoutException)
        {
            string detail=process.HasExited?await errors:"Shared controller startup timed out.";
            throw new LabException("controller_unavailable",detail.Length<=4096?detail:detail[..4096]);
        }
    }

    public static async Task RunAsync(string workspace,CancellationToken cancellation=default)
    {
        ConnectionLog.Event("controller_starting",new { pipe=PipeName(workspace) });
        await using var service=new LabService(workspace);
        ConnectionLog.Event("controller_started",new { workspace_id=service.Workspace.Workspace.Id });
        string name=PipeName(workspace);
        var sessions=new List<Task>();
        long lastHeartbeat=Stopwatch.GetTimestamp();
        try
        {
            while(!cancellation.IsCancellationRequested)
            {
                if(Stopwatch.GetElapsedTime(lastHeartbeat)>=TimeSpan.FromSeconds(30))
                {
                    ConnectionLog.Event("controller_heartbeat",new { sessions=sessions.Count(task=>!task.IsCompleted),active_operations=service.HasActiveOperations });
                    lastHeartbeat=Stopwatch.GetTimestamp();
                }
                if(sessions.Count>=31)
                {
                    await Task.WhenAny(sessions).WaitAsync(cancellation);
                    sessions.RemoveAll(task=>task.IsCompleted);
                }
                var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,32,PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                using var idle=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                idle.CancelAfter(TimeSpan.FromSeconds(5));
                try { await pipe.WaitForConnectionAsync(idle.Token); }
                catch(OperationCanceledException) when(!cancellation.IsCancellationRequested)
                {
                    pipe.Dispose(); sessions.RemoveAll(task=>task.IsCompleted);
                    if(sessions.Count==0&&!service.HasActiveOperations)
                    {
                        ConnectionLog.Event("controller_idle_exit"); return;
                    }
                    continue;
                }
                catch { pipe.Dispose(); throw; }
                sessions.RemoveAll(task=>task.IsCompleted);
                sessions.Add(ServeAsync(pipe,service,cancellation));
            }
        }
        catch(Exception error) { ConnectionLog.Event("controller_failed",error:error); throw; }
        finally
        {
            ConnectionLog.Event("controller_draining",new { sessions=sessions.Count(task=>!task.IsCompleted),active_operations=service.HasActiveOperations });
            await Task.WhenAll(sessions);
            ConnectionLog.Event("controller_stopped");
        }
    }

    private static async Task ServeAsync(NamedPipeServerStream pipe,LabService service,CancellationToken cancellation)
    {
        string session=Guid.NewGuid().ToString("N"),stage="handshake";
        ConnectionLog.Event("session_accepted",new { session_id=session,connector_process_id=ConnectionLog.Peer(pipe.SafePipeHandle,false) });
        using(pipe)
        {
            try
            {
                using var hello=CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                hello.CancelAfter(TimeSpan.FromSeconds(5));
                byte[] mode=new byte[1]; await pipe.ReadExactlyAsync(mode,hello.Token);
                if(mode[0]==2)
                {
                    stage="cli_forward";
                    var cliRequest=await Frames.ReadAsync<CliForwardRequest>(pipe,cancellation);
                    if(cliRequest.SchemaVersion!=Wire.Version)
                        throw new LabException("schema_unsupported","Unsupported CLI forward schema.");
                    var cliReply=await CliCommands.ExecuteCapturedAsync(service,cliRequest.Args,cliRequest.StandardInput);
                    await Frames.WriteAsync(pipe,cliReply,cancellation);
                    return;
                }
                if(mode[0]>1)
                {
                    ConnectionLog.Event("session_rejected",new { session_id=session,reason="invalid_mode" }); return;
                }
                ConnectionLog.Event("session_started",new { session_id=session,native_extension=mode[0]==1 });
                stage="mcp";
                var services=new ServiceCollection();
                services.AddLogging(logging=>logging.AddProvider(new ConnectionLog.Provider(session)));
                services.AddSingleton(service); services.AddSingleton<LabTools>();
                var builder=services.AddMcpServer().WithTools<McpTools>(McpCatalog.Json).WithResources<LabTools>();
                if(mode[0]==1) builder.WithTools<McpPatchTools>(McpCatalog.Json);
                await using var provider=services.BuildServiceProvider();
                await using var server=McpServer.Create(new StreamServerTransport(pipe,pipe),
                    provider.GetRequiredService<IOptions<McpServerOptions>>().Value,serviceProvider:provider);
                await server.RunAsync(cancellation);
                ConnectionLog.Event("session_server_returned",new { session_id=session,cancellation_requested=cancellation.IsCancellationRequested });
            }
            catch(Exception error) when(error is IOException or OperationCanceledException or ObjectDisposedException
                or ModelContextProtocol.McpException or System.Text.Json.JsonException)
            { ConnectionLog.Event("session_disconnected",new { session_id=session,stage,cancellation_requested=cancellation.IsCancellationRequested },error); }
            catch(Exception error)
            { ConnectionLog.Event("session_failed",new { session_id=session,stage },error); throw; }
            finally { ConnectionLog.Event("session_closed",new { session_id=session }); }
        }
    }

    internal static async Task<CliForwardReply?> TryForwardCliAsync(string workspace,string[] args,string? standardInput,CancellationToken cancellation=default)
    {
        string fullWorkspace=Path.GetFullPath(workspace);
        if(!Directory.Exists(fullWorkspace)||!File.Exists(Path.Combine(fullWorkspace,"controller.lock")))
            return null;
        using var pipe=Client(PipeName(fullWorkspace));
        try
        {
            await pipe.ConnectAsync(150,cancellation);
        }
        catch(Exception e) when(e is TimeoutException or IOException or OperationCanceledException)
        {
            return null;
        }
        await pipe.WriteAsync(new byte[]{2},cancellation);
        await Frames.WriteAsync(pipe,new CliForwardRequest(Wire.Version,args,standardInput),cancellation);
        return await Frames.ReadAsync<CliForwardReply>(pipe,cancellation);
    }
}
