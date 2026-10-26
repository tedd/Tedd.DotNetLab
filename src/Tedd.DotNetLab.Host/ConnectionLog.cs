using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Host;

// Independent of the catalog and MCP streams: diagnostics must survive a broken
// connection and must never turn a logging failure into a connection failure.
internal sealed class ConnectionLog : IDisposable
{
    internal const long MaximumFileBytes=2*1024*1024;
    private readonly object gate=new();
    private readonly string role;
    private readonly long started=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    private readonly long maximumBytes;
    private FileStream? output;
    private bool failed,disposed;
    private long sequence,droppedEntries;
    internal string PathName { get; }
    internal static ConnectionLog? Current { get; private set; }

    internal ConnectionLog(string workspace,string role,long maximumBytes=MaximumFileBytes)
    {
        this.role=role; this.maximumBytes=maximumBytes;
        PathName=Path.Combine(Path.GetFullPath(workspace),"logs",$"{role}-{Environment.ProcessId}-{started}.jsonl");
    }

    internal static void Start(string[] args)
    {
        if(args.Length<2||args[0] is not ("connect" or "serve" or "--shared-controller" or "--shared-bootstrap")) return;
        if(args[0] is "connect" or "serve"&&(args.Length>3||args.Length==3&&args[2]!="--enable-native-extension")) return;
        if(args[0] is "--shared-controller" or "--shared-bootstrap"&&args.Length!=2) return;
        try
        {
            Current=new(args[1],args[0].TrimStart('-'));
            Event("process_started",new { runtime=RuntimeInformation.FrameworkDescription,
                host=typeof(ConnectionLog).Assembly.Location,
                version=typeof(ConnectionLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion });
            AppDomain.CurrentDomain.UnhandledException+=(_,e)=>Event("unhandled_exception",new { terminating=e.IsTerminating },e.ExceptionObject as Exception);
            AppDomain.CurrentDomain.ProcessExit+=(_,_)=>
            {
                Event("process_exit",new { exit_code=Environment.ExitCode }); Current?.Dispose();
            };
        }
        catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { Fallback(e); }
    }

    internal static void Event(string name,object? data=null,Exception? error=null)=>Current?.Record(name,data,error);

    internal void Record(string name,object? data=null,Exception? error=null)
    {
        lock(gate)
        {
            if(disposed) return;
            try
            {
                byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(new { timestamp=DateTimeOffset.UtcNow,
                    sequence=++sequence,dropped_entries=droppedEntries,process_id=Environment.ProcessId,process_start_utc_ticks=started.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    role,@event=name,data,error=Describe(error,0) });
                if(bytes.Length+1>maximumBytes) throw new IOException("Diagnostic entry exceeds the log budget.");
                if(output is null)
                {
                    ValidatePath(); Directory.CreateDirectory(Path.GetDirectoryName(PathName)!);
                    Prune(); output=Open();
                }
                if(output.Length+bytes.Length+1>maximumBytes)
                {
                    output.Dispose(); output=null; ValidatePath();
                    File.Move(PathName,PathName+".1",true); output=Open();
                }
                output.Write(bytes); output.WriteByte((byte)'\n'); output.Flush();
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
            {
                droppedEntries++;
                try { output?.Dispose(); } catch(IOException) { }
                output=null;
                if(!failed) { failed=true; Fallback(e); }
            }
        }
    }

    // Exception messages and formatted SDK log state can contain request data.
    // Retain type, HResult, domain code and stack, including bounded inner causes.
    private static object? Describe(Exception? error,int depth)=>error is null?null:new
    {
        type=error.GetType().FullName,hresult=error.HResult,code=(error as LabException)?.Code,
        stack=error.StackTrace is { } stack?stack[..Math.Min(stack.Length,8192)]:null,
        inner=depth<3?Describe(error.InnerException,depth+1):null
    };

    private FileStream Open()=>new(PathName,FileMode.Append,FileAccess.Write,FileShare.Read);
    private void ValidatePath()
    {
        for(var dir=new DirectoryInfo(Path.GetDirectoryName(PathName)!);dir is not null;dir=dir.Parent)
            if(dir.Exists&&(dir.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Diagnostic path traverses a reparse point.");
        foreach(string file in new[]{PathName,PathName+".1"})
            if(File.Exists(file)&&(File.GetAttributes(file)&FileAttributes.ReparsePoint)!=0) throw new IOException("Diagnostic file is a reparse point.");
    }
    private void Prune()
    {
        // Active writers keep their current file open without delete sharing.
        var files=new DirectoryInfo(Path.GetDirectoryName(PathName)!).EnumerateFiles("*.jsonl*")
            .Where(f=>System.Text.RegularExpressions.Regex.IsMatch(f.Name,@"^(connect|serve|shared-controller|shared-bootstrap)-\d+-\d+\.jsonl(\.1)?$"))
            .OrderByDescending(f=>f.LastWriteTimeUtc).ToArray();
        for(int index=0;index<files.Length;index++)
        {
            var file=files[index];
            if(index<128&&file.LastWriteTimeUtc>DateTime.UtcNow.AddDays(-7)) continue;
            try
            {
                if((file.Attributes&FileAttributes.ReparsePoint)!=0) continue;
                using(var unused=new FileStream(file.FullName,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) { }
                File.Delete(file.FullName);
            }
            catch(Exception e) when(e is IOException or UnauthorizedAccessException) { }
        }
    }
    private static void Fallback(Exception error)
    {
        try { Console.Error.WriteLine($"DotNetLab diagnostic logging unavailable ({error.GetType().Name})."); }
        catch(Exception e) when(e is IOException or ObjectDisposedException) { }
    }
    public void Dispose()
    {
        lock(gate)
        {
            disposed=true;
            try { output?.Dispose(); } catch(IOException) { }
            output=null;
        }
    }

    internal sealed class Provider(string? session=null) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName)=>new Logger(categoryName,session);
        public void Dispose() { }
        private sealed class Logger(string category,string? session) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState:notnull=>null;
            public bool IsEnabled(LogLevel logLevel)=>logLevel>=LogLevel.Warning;
            public void Log<TState>(LogLevel logLevel,EventId eventId,TState state,Exception? exception,Func<TState,Exception?,string> formatter)
            {
                if(IsEnabled(logLevel)) Event("mcp_warning",new { session_id=session,category,level=logLevel.ToString(),event_id=eventId.Id,event_name=eventId.Name },exception);
            }
        }
    }

    internal static int? Peer(SafePipeHandle handle,bool server)
    {
        if(!OperatingSystem.IsWindows()) return null;
        return (server?GetNamedPipeServerProcessId(handle,out uint id):GetNamedPipeClientProcessId(handle,out id))?checked((int)id):null;
    }
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint processId);
    [DllImport("kernel32.dll",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeClientProcessId(SafePipeHandle pipe,out uint processId);
}
