using System.Globalization;
using System.Text;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Tedd.DotNetLab.Transport.Local;
using Tedd.DotNetLab.Host;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

try
{
    ConnectionLog.Start(args);
    if(args.Length==2&&args[0]=="--shared-bootstrap")
    {
        await SharedController.BootstrapAsync(args[1]); return;
    }
    if(args.Length==2&&args[0]=="--shared-controller")
    {
        await SharedController.RunAsync(args[1]); return;
    }
    if(args.Length>0&&args[0]=="connect")
    {
        if(args.Length is not (2 or 3)||args.Length==3&&args[2]!="--enable-native-extension")
            throw new LabException("invalid_request","Usage: dotnetlab connect <workspace> [--enable-native-extension]");
        await SharedController.ConnectAsync(args[1],args.Length==3); return;
    }
    if(args.Length==1&&args[0]=="--profiler-attach-worker")
    {
        try
        {
            var request=await Frames.ReadAsync<ProfilerAttachmentInvocation>(Console.OpenStandardInput(),CancellationToken.None);
            ProfilerAttachmentIdentity.RequireHost();
            if(request.SchemaVersion!=Wire.Version||!Path.IsPathFullyQualified(request.Workspace))
                throw new LabException("invalid_request","Invalid attachment invocation.");
            using var binary=ProfilerAttachmentIdentity.OpenBinary(request.BinaryPath);
            if(binary.Length!=request.BinaryLength||ProfilerAttachmentIdentity.Hash(binary)!=request.BinaryHash)
                throw new LabException("artifact_corrupt","Approved profiler bytes changed.");
            if(ProfilerAttachmentIdentity.VerifyRuntime(request.Target,true)!=request.RuntimeHash)
                throw new LabException("target_identity_changed","Approved runtime changed.");
            byte[] metadata=Encoding.UTF8.GetBytes(Wire.Serialize(new { schema_version=Wire.Version,target_id=request.Target.Id,
                process_id=request.Target.ProcessId,start_time_utc_ticks=request.Target.StartTimeUtcTicks,workspace=request.Workspace }));
            new DiagnosticsClient(request.Target.ProcessId).AttachProfiler(TimeSpan.FromSeconds(10),
                Guid.Parse(ProfilerAttachmentIdentity.ProfilerId),request.BinaryPath,metadata);
            await Frames.WriteAsync(Console.OpenStandardOutput(),new ProfilerAttachmentWorkerReply(Wire.Version,"ok",null),CancellationToken.None);
        }
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            await Frames.WriteAsync(Console.OpenStandardOutput(),new ProfilerAttachmentWorkerReply(Wire.Version,"error",
                error is LabException lab?lab.Code:"profiler_attach_failed"),CancellationToken.None); Environment.ExitCode=1;
        }
        return;
    }
    if(args.Length==1&&args[0]=="profiles") { Console.WriteLine(Wire.Serialize(Profiles.Names)); return; }
    if(args.Length>0&&args[0]=="compatibility")
    {
        if(args.Length!=1) throw new LabException("invalid_request","Usage: dotnetlab compatibility (no workspace or target arguments)");
        Console.WriteLine(Wire.Serialize(CompatibilityReports.Create(Path.Combine(AppContext.BaseDirectory,"Tedd.DotNetLab.Profiler.dll")))); return;
    }
    if(args.Length==2&&args[0]=="schema")
    {
        Console.WriteLine(SchemaDocuments.Create(args[1]).ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented=true })); return;
    }
    if(args.Length>0&&args[0] is "install" or "uninstall")
    {
        Console.WriteLine(Wire.Serialize(await Installer.RunAsync(args))); return;
    }
    if(args.Length>0&&args[0]=="scaffold")
    {
        if(args.Length<2||args.Length>5)
            throw new LabException("invalid_request","Usage: dotnetlab scaffold <output-dir> [--scenario <id>] [--force]");
        string outputDir=args[1];
        string scenarioId="scratch-bench";
        bool force=false;
        for(int i=2;i<args.Length;i++)
        {
            if(args[i]=="--force") force=true;
            else if(args[i]=="--scenario"&&i+1<args.Length) scenarioId=args[++i];
            else throw new LabException("invalid_request","Usage: dotnetlab scaffold <output-dir> [--scenario <id>] [--force]");
        }
        Console.WriteLine(Wire.Serialize(ScaffoldService.Create(outputDir,scenarioId,force)));
        return;
    }
    if(args.Length<2) throw new LabException("invalid_request","Usage: dotnetlab <install|uninstall> [all|codex|claude|cursor|antigravity] [options] | scaffold <output-dir> [--scenario <id>] [--force] | compatibility | profiles | schema <name> | <init|serve|connect|call|backup|checkpoint|grant|grant-scratch|grant-build|grant-profiler-attach|grant-cil|grant-confirmation|grant-export|grant-deletion|grant-release|capture|import> <workspace> [arguments]");
    if(args[0]=="serve"&&(args.Length is not (2 or 3)||args.Length==3&&args[2]!="--enable-native-extension"))
        throw new LabException("invalid_request","Usage: dotnetlab serve <workspace> [--enable-native-extension]");
    string command=args[0],workspace=args[1];
    if(command=="serve")
    {
        await using var serveService=new LabService(workspace);
        var builder=Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(options=>options.LogToStandardErrorThreshold=LogLevel.Trace);
        builder.Logging.AddProvider(new ConnectionLog.Provider());
        builder.Services.AddSingleton(serveService); builder.Services.AddSingleton<LabTools>();
        var server=builder.Services.AddMcpServer().WithStdioServerTransport().WithTools<McpTools>(McpCatalog.Json).WithResources<LabTools>();
        if(args.Length==3) server.WithTools<McpPatchTools>(McpCatalog.Json);
        using var host=builder.Build();
        ConnectionLog.Event("stdio_server_started");
        await host.RunAsync();
        ConnectionLog.Event("stdio_server_stopped");
        return;
    }
    string? stdIn=null;
    if(command=="call"&&args.Length==3)
    {
        var input=new StringBuilder();
        char[] buffer=new char[4096];
        int read;
        while((read=await Console.In.ReadAsync(buffer))>0)
        {
            input.Append(buffer,0,read);
            if(input.Length>1048576) throw new LabException("resource_limit","CLI request exceeds 1 MiB.");
        }
        stdIn=input.ToString();
    }
    if(await SharedController.TryForwardCliAsync(workspace,args,stdIn) is { } forwarded)
    {
        if(forwarded.StandardOutput.Length>0) Console.Out.Write(forwarded.StandardOutput);
        if(forwarded.StandardError.Length>0) Console.Error.Write(forwarded.StandardError);
        Environment.ExitCode=forwarded.ExitCode;
        return;
    }
    await using var service=new LabService(workspace);
    var (exitCode,output)=await CliCommands.ExecuteAsync(service,args,stdIn);
    if(output.Length>0) Console.Out.Write(output);
    Environment.ExitCode=exitCode;
    return;
}
catch(LabException e)
{
    ConnectionLog.Event("process_failed",error:e);
    Console.Error.WriteLine(Wire.Serialize(new { schema_version=Wire.Version,status="error",code=e.Code,message=e.Message,recovery=e.Recovery }));
    Environment.ExitCode=1;
}
catch(Exception e) when(e is FormatException or OverflowException or IOException or ArgumentException)
{
    ConnectionLog.Event("process_failed",error:e);
    Console.Error.WriteLine(Wire.Serialize(new { schema_version=Wire.Version,status="error",code="invalid_request",message=e.Message }));
    Environment.ExitCode=1;
}
catch(Exception e)
{
    ConnectionLog.Event("process_failed",error:e);
    throw;
}
