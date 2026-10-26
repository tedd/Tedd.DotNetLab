using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using Tedd.DotNetLab.Host;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ConnectionLoggingTests
{
    [Fact]
    public void RotationRetainsValidBoundedEntriesWithoutExceptionMessages()
    {
        using var workspace=new TestWorkspace();
        string path;
        using(var log=new ConnectionLog(workspace.Root,"connect",2048))
        {
            path=log.PathName;
            for(int i=0;i<30;i++) log.Record("test",new { index=i },new IOException("private-request-content"));
        }
        Assert.True(File.Exists(path+".1"));
        foreach(string file in new[]{path,path+".1"})
        {
            Assert.InRange(new FileInfo(file).Length,1,2048);
            Assert.DoesNotContain("private-request-content",File.ReadAllText(file));
            foreach(var row in Read(file))
            {
                Assert.Equal(Environment.ProcessId,row.GetProperty("process_id").GetInt32());
                Assert.Equal("System.IO.IOException",row.GetProperty("error").GetProperty("type").GetString());
                Assert.Equal("connect",row.GetProperty("role").GetString());
            }
        }
        Assert.Equal(29,Read(path).Last().GetProperty("data").GetProperty("index").GetInt32());
    }

    [Fact]
    public void UnwritableLogPathAndDisposedLoggerDoNotThrow()
    {
        using var workspace=new TestWorkspace();
        File.WriteAllText(Path.Combine(workspace.Root,"logs"),"blocked");
        using var log=new ConnectionLog(workspace.Root,"connect");
        log.Record("first"); log.Record("second");
        File.Delete(Path.Combine(workspace.Root,"logs"));
        log.Record("recovered"); log.Dispose(); log.Record("after_dispose");
        var row=Assert.Single(Read(log.PathName));
        Assert.Equal("recovered",Event(row)); Assert.Equal(2,row.GetProperty("dropped_entries").GetInt64());
    }

    [Fact]
    public void RetentionPrunesOldInactiveLogsAndPreservesOtherFiles()
    {
        using var workspace=new TestWorkspace();
        string logs=Path.Combine(workspace.Root,"logs"); Directory.CreateDirectory(logs);
        string old=Path.Combine(logs,"connect-1-1.jsonl"),unrelated=Path.Combine(logs,"user.jsonl");
        File.WriteAllText(old,"old"); File.SetLastWriteTimeUtc(old,DateTime.UtcNow.AddDays(-8));
        File.WriteAllText(unrelated,"keep");
        string active=Path.Combine(logs,"serve-1-2.jsonl");
        File.WriteAllText(active,"active"); File.SetLastWriteTimeUtc(active,DateTime.UtcNow.AddDays(-8));
        using var writer=new FileStream(active,FileMode.Append,FileAccess.Write,FileShare.Read);
        using(var log=new ConnectionLog(workspace.Root,"connect")) log.Record("test");
        Assert.False(File.Exists(old)); Assert.True(File.Exists(active)); Assert.Equal("keep",File.ReadAllText(unrelated));
    }

    [Theory,InlineData(false),InlineData(true)]
    public async Task StdioEofPreservesProtocolAndLogsExitEvenWhenLoggingIsUnavailable(bool blocked)
    {
        using var workspace=new TestWorkspace();
        if(blocked) File.WriteAllText(Path.Combine(workspace.Root,"logs"),"blocked");
        using var process=Start(workspace.Root,"serve"); var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            await Initialize(process);
            await Tool(process,"lab.inspect",new { action="record",id="private-request-content" },true);
            process.StandardInput.Close(); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(0,process.ExitCode);
            if(blocked) Assert.Contains("diagnostic logging unavailable",await stderr);
            else
            {
                var rows=Rows(workspace.Root,"serve");
                Assert.Contains(rows,r=>Event(r)=="stdio_server_stopped");
                Assert.Contains(rows,r=>Event(r)=="process_exit");
                Assert.DoesNotContain("private-request-content",string.Join("\n",rows));
            }
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public async Task ControllerDeathLogsOutputClosureAndFreshConnectionLogsIdleShutdown()
    {
        using var workspace=new TestWorkspace();
        using var first=Start(workspace.Root,"connect"); var firstErrors=first.StandardError.ReadToEndAsync();
        Process? controller=null;
        try
        {
            await Initialize(first); await Tool(first,"lab.discover",new { action="workspace" });
            var started=Rows(workspace.Root,"shared-controller").Single(r=>Event(r)=="process_started");
            int controllerId=started.GetProperty("process_id").GetInt32();
            controller=Process.GetProcessById(controllerId);
            Assert.Equal(controller.StartTime.ToUniversalTime().Ticks.ToString(),started.GetProperty("process_start_utc_ticks").GetString());
            if(OperatingSystem.IsWindows())
            {
                var connected=Rows(workspace.Root,"connect").Single(r=>Event(r)=="connector_connected");
                Assert.Equal(controllerId,connected.GetProperty("data").GetProperty("controller_process_id").GetInt32());
                Assert.Contains(Rows(workspace.Root,"shared-controller"),r=>Event(r)=="session_accepted"&&r.GetProperty("data").GetProperty("connector_process_id").GetInt32()==first.Id);
            }
            using(var malformed=new NamedPipeClientStream(".",SharedController.PipeName(workspace.Root),PipeDirection.InOut,PipeOptions.Asynchronous))
            {
                await malformed.ConnectAsync(5000); await malformed.WriteAsync(new byte[]{255});
                Assert.Equal(0,await malformed.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            }
            await WaitForEvent(workspace.Root,"session_rejected",TimeSpan.FromSeconds(10));
            var handshakeFailure=Rows(workspace.Root,"shared-controller").First(r=>Event(r)=="session_disconnected");
            Assert.Equal("handshake",handshakeFailure.GetProperty("data").GetProperty("stage").GetString());
            Assert.Equal("System.IO.EndOfStreamException",handshakeFailure.GetProperty("error").GetProperty("type").GetString());
            Assert.NotEmpty(handshakeFailure.GetProperty("error").GetProperty("stack").GetString()!);
            await WaitForEvent(workspace.Root,"controller_heartbeat",TimeSpan.FromSeconds(40));
            controller.Kill(); await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await first.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10)); // stdin deliberately stays open.
            Assert.Equal(0,first.ExitCode);
            var closed=Rows(workspace.Root,"connect").Single(r=>Event(r)=="connector_closing");
            Assert.Equal("controller_output",closed.GetProperty("data").GetProperty("trigger").GetString());
            Assert.Contains(Rows(workspace.Root,"connect"),r=>Event(r)=="stream_eof"||Event(r)=="stream_failed");
            Assert.DoesNotContain(Rows(workspace.Root,"shared-controller"),r=>Event(r)=="process_exit");

            using var second=Start(workspace.Root,"connect"); var secondErrors=second.StandardError.ReadToEndAsync();
            try
            {
                await Initialize(second); await Tool(second,"lab.discover",new { action="workspace" });
                second.StandardInput.Close(); await second.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(0,second.ExitCode);
                await WaitForIdleExit(workspace.Root);
                var rows=Rows(workspace.Root,"shared-controller");
                Assert.Contains(rows,r=>Event(r)=="controller_idle_exit");
                Assert.Contains(rows,r=>Event(r)=="controller_stopped");
                Assert.Contains(rows,r=>Event(r)=="process_exit");
            }
            finally { if(!second.HasExited) { second.Kill(); await second.WaitForExitAsync(); } }
        }
        finally
        {
            if(!first.HasExited) { first.Kill(); await first.WaitForExitAsync(); }
            controller?.Dispose(); await WaitForIdleExit(workspace.Root);
        }
    }

    private static Process Start(string workspace,string command)
    {
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll"));
        start.ArgumentList.Add(command); start.ArgumentList.Add(workspace);
        return Process.Start(TestWorkspace.PinRuntime(start))!;
    }
    private static async Task<JsonElement> Request(Process process,int id,string method,object parameters)
    {
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new { jsonrpc="2.0",id,method,@params=parameters }));
        await process.StandardInput.FlushAsync();
        while(true)
        {
            string? line=await process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.NotNull(line);
            using var doc=JsonDocument.Parse(line);
            if(doc.RootElement.TryGetProperty("id",out var found)&&found.ValueKind==JsonValueKind.Number&&found.GetInt32()==id)
            {
                Assert.False(doc.RootElement.TryGetProperty("error",out _),line);
                return doc.RootElement.GetProperty("result").Clone();
            }
        }
    }
    private static async Task Initialize(Process process)
    {
        await Request(process,1,"initialize",new { protocolVersion="2025-06-18",capabilities=new { },clientInfo=new { name="logging-test",version="1" } });
        await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}");
    }
    private static async Task Tool(Process process,string name,object request,bool error=false)
    {
        var reply=await Request(process,2,"tools/call",new { name,arguments=new { request } });
        Assert.Equal(error,reply.TryGetProperty("isError",out var value)&&value.GetBoolean());
    }
    private static string? Event(JsonElement row)=>row.GetProperty("event").GetString();
    private static JsonElement[] Read(string path)
    {
        using var file=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
        using var reader=new StreamReader(file);
        return reader.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries).Select(line=>
        {
            using var doc=JsonDocument.Parse(line); return doc.RootElement.Clone();
        }).ToArray();
    }
    private static JsonElement[] Rows(string workspace,string role)=>Directory.GetFiles(Path.Combine(workspace,"logs"),role+"-*.jsonl").SelectMany(Read).ToArray();
    private static async Task WaitForEvent(string workspace,string name,TimeSpan limit)
    {
        using var timeout=new CancellationTokenSource(limit);
        while(!Rows(workspace,"shared-controller").Any(r=>Event(r)==name)) await Task.Delay(100,timeout.Token);
    }
    private static async Task WaitForIdleExit(string workspace)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while(File.Exists(Path.Combine(workspace,"controller.lock")))
        {
            try { using var file=new FileStream(Path.Combine(workspace,"controller.lock"),FileMode.Open,FileAccess.ReadWrite,FileShare.None); break; }
            catch(IOException) { await Task.Delay(50,timeout.Token); }
        }
        // The lock is released just before the process-exit diagnostic is flushed.
        if(Directory.Exists(Path.Combine(workspace,"logs")))
            while(Rows(workspace,"shared-controller").Any(r=>Event(r)=="controller_idle_exit")&&!Rows(workspace,"shared-controller").Any(r=>Event(r)=="process_exit"))
                await Task.Delay(20,timeout.Token);
    }
}
