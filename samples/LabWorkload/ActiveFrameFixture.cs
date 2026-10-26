using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;

[SupportedOSPlatform("windows")]
static class ActiveFrameFixture
{
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    public static async Task RunAsync(string workspace)
    {
        ProfilerAttachmentIdentity.RequireHost();
        string buildRoot=Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"..","..","..",".."));
        if(Environment.GetEnvironmentVariable("DOTNETLAB_DISPOSABLE")!="1"||
            (Directory.Exists(@"D:\Workspaces\AI")&&!buildRoot.StartsWith(@"D:\Workspaces\AI\",StringComparison.OrdinalIgnoreCase))||
            !Path.GetFullPath(workspace).StartsWith(Path.Combine(buildRoot,"tests")+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))
            throw new LabException("invalid_request","Active-frame fixture requires an owned disposable test workspace.");
        const int calibrationIterations=1_000_000;
        GC.KeepAlive(NativeFixture.ActiveLoop(0)); var clock=Stopwatch.StartNew();
        int calibration=NativeFixture.ActiveLoop(calibrationIterations); clock.Stop();
        long calibrationTicks=Math.Max(1,clock.ElapsedTicks);
        await Frames.WriteAsync(Console.OpenStandardOutput(),new { phase="ready",process_id=Environment.ProcessId,calibration,
            calibration_iterations=calibrationIterations,calibration_ticks=calibrationTicks,frequency=Stopwatch.Frequency },CancellationToken.None);
        var completed=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread? old=null; uint threadId=0; int iterations=0;
        while(true)
        {
            FrameCommand command;
            try { command=await Frames.ReadAsync<FrameCommand>(Console.OpenStandardInput(),CancellationToken.None); }
            catch(EndOfStreamException) { return; }
            switch(command.Action)
            {
                case "start" when old is null&&command.Iterations is >=1 and <=400_000_000:
                    iterations=command.Iterations;
                    var entered=new TaskCompletionSource<uint>(TaskCreationOptions.RunContinuationsAsynchronously);
                    old=new Thread(()=>
                    {
                        entered.SetResult(GetCurrentThreadId());
                        try { completed.SetResult(NativeFixture.ActiveLoop(iterations)); } catch(Exception error) { completed.SetException(error); }
                    }) { IsBackground=true,Name="qualified-old-frame" };
                    old.Start(); threadId=await entered.Task;
                    await Frames.WriteAsync(Console.OpenStandardOutput(),new { phase="started",thread_id=threadId,iterations },CancellationToken.None); break;
                case "future" when old is not null:
                    int value=NativeFixture.ActiveLoop(0);
                    await Frames.WriteAsync(Console.OpenStandardOutput(),new { phase="future",value,old_frame_completed=completed.Task.IsCompleted },CancellationToken.None); break;
                case "join" when old is not null:
                    int result=await completed.Task.WaitAsync(TimeSpan.FromSeconds(60)); old.Join();
                    await Frames.WriteAsync(Console.OpenStandardOutput(),new { phase="joined",result,iterations,thread_id=threadId },CancellationToken.None); break;
                case "exit" when old is null||completed.Task.IsCompleted:
                    await Frames.WriteAsync(Console.OpenStandardOutput(),new { phase="exited" },CancellationToken.None); return;
                default: throw new LabException("invalid_request","Unsupported active-frame fixture command.");
            }
        }
    }
    private record FrameCommand(string Action,int Iterations=0);
}
