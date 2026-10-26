using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Artifacts;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class TraceWorkerTests
{
    private static string FaultWorker=>Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.ParserFault","Release","net10.0","Tedd.DotNetLab.ParserFault.dll");
    private static async Task<TraceWorkerRequest> Request(TestWorkspace workspace,TraceParserPolicy? policy=null)
    {
        var store=new ArtifactStore(workspace.Root); string input=store.CreateIncoming("source"); await File.WriteAllTextAsync(input,"intentionally not a trace");
        var artifact=await store.SealAsync(input,"application/vnd.microsoft.nettrace","fixture",true,1024);
        var capture=new Capture("fixture-capture",new("retained",1,1,"fixture","10.0.12","x64"),Profiles.Resolve("baseline-context"),CaptureState.Sealed,artifact.Id,null,DateTimeOffset.UtcNow);
        return new(Wire.Version,workspace.Root,await store.VerifiedPathAsync(artifact),artifact,capture,store.CreateIncoming("worker"),policy??new());
    }
    private static void AssertGone(int pid)
    {
        try { using var process=Process.GetProcessById(pid); Assert.True(process.HasExited,"Worker still exists."); }
        catch(ArgumentException) { }
    }
    private static ProcessStartInfo ControllerStart(string requestPath)
    {
        string host=Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..","dotnet"));
        var start=new ProcessStartInfo(host) { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.ControllerFault","Release","net10.0","Tedd.DotNetLab.ControllerFault.dll"));
        start.ArgumentList.Add("--parser-controller"); start.ArgumentList.Add(requestPath);
        return start;
    }
    [Fact,Case("IT-PARSER-LINUX-001","FR-016,FR-038,FR-072","M7","Integration")]
    public async Task LinuxAbruptControllerDeathAndDeadlineKillTheEntireWorkerGroup()
    {
        if(!OperatingSystem.IsLinux()) return;
        using var workspace=new TestWorkspace();
        foreach(bool abrupt in new[]{true,false})
        {
            var request=await Request(workspace,new(MaximumDurationSeconds:abrupt?60:3));
            string requestPath=Path.Combine(workspace.Root,"controller-request.json"); await File.WriteAllTextAsync(requestPath,Wire.Serialize(request));
            using var controller=Process.Start(ControllerStart(requestPath))!;
            string? group=null;
            try
            {
                using var readyDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(15));
                while(!File.Exists(request.OutputPath+".ready"))
                {
                    if(controller.HasExited) throw new InvalidOperationException(await controller.StandardError.ReadToEndAsync()+await controller.StandardOutput.ReadToEndAsync());
                    await Task.Delay(20,readyDeadline.Token);
                }
                using var ready=JsonDocument.Parse(await File.ReadAllTextAsync(request.OutputPath+".ready"));
                group=ready.RootElement.GetProperty("group").GetString()!;
                Assert.StartsWith(Environment.GetEnvironmentVariable("DOTNETLAB_PARSER_CGROUP_ROOT")+"/dotnetlab-worker-",group);
                string members=await File.ReadAllTextAsync(group+"/cgroup.procs");
                Assert.Contains(ready.RootElement.GetProperty("worker").GetInt32().ToString(),members.Split('\n'));
                Assert.Contains(ready.RootElement.GetProperty("descendant").GetInt32().ToString(),members.Split('\n'));
                if(abrupt) controller.Kill(); // Deliberately kill only the controller.
                await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                if(!abrupt)
                {
                    using var response=JsonDocument.Parse(await controller.StandardOutput.ReadToEndAsync());
                    Assert.Equal("parser_timeout",response.RootElement.GetProperty("code").GetString());
                    Assert.False(Directory.Exists(group));
                }
                else
                {
                    using var emptyDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while(!(await File.ReadAllTextAsync(group+"/cgroup.events")).Split('\n').Contains("populated 0")) await Task.Delay(20,emptyDeadline.Token);
                    Assert.Empty((await File.ReadAllTextAsync(group+"/cgroup.procs")).Trim());
                }
                await new ArtifactStore(workspace.Root).VerifyAsync(request.SourceArtifact.Hash,request.SourceArtifact.Length);
            }
            finally
            {
                if(!controller.HasExited) { controller.Kill(true); await controller.WaitForExitAsync(); }
                if(group is not null&&Directory.Exists(group))
                {
                    await File.WriteAllTextAsync(group+"/cgroup.kill","1");
                    using var cleanupDeadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    while(!(await File.ReadAllTextAsync(group+"/cgroup.events")).Split('\n').Contains("populated 0")) await Task.Delay(20,cleanupDeadline.Token);
                    Directory.Delete(group);
                }
            }
        }
    }
    [Fact,Case("IT-PARSER-LINUX-002","FR-016,FR-072","M7","Integration")]
    public async Task LinuxMissingDelegationFailsBeforeWorkerLaunch()
    {
        if(!OperatingSystem.IsLinux()) return;
        using var workspace=new TestWorkspace(); var request=await Request(workspace);
        string requestPath=Path.Combine(workspace.Root,"controller-request.json"); await File.WriteAllTextAsync(requestPath,Wire.Serialize(request));
        var start=ControllerStart(requestPath); start.Environment.Remove("DOTNETLAB_PARSER_CGROUP_ROOT");
        using var controller=Process.Start(TestWorkspace.PinRuntime(start))!; await controller.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(0,controller.ExitCode);
        using var response=JsonDocument.Parse(await controller.StandardOutput.ReadToEndAsync());
        Assert.Equal("capability_unsupported",response.RootElement.GetProperty("code").GetString());
        Assert.Equal(0,response.RootElement.GetProperty("starts").GetInt32());
        Assert.False(File.Exists(request.OutputPath)); Assert.False(File.Exists(request.OutputPath+".ready"));
        await new ArtifactStore(workspace.Root).VerifyAsync(request.SourceArtifact.Hash,request.SourceArtifact.Length);
    }
    [Theory,InlineData("stall","parser_timeout"),InlineData("crash","parser_failed"),InlineData("memory","parser_memory_limit"),
        InlineData("scratch","resource_limit"),InlineData("stack_source","invalid_parser_response"),InlineData("stack_policy","invalid_parser_response")]
    [Case("IT-PARSER-STACK-001","FR-013,FR-016,FR-023,FR-072","M7","Integration")]
    public async Task StackWorkerFaultsAndScratchQuotaDoNotPublishOrLeaveLiveWorkers(string mode,string code)
    {
        using var workspace=new TestWorkspace(); var basic=await Request(workspace,new(MaximumCommittedMemoryBytes:134217728,MaximumDurationSeconds:mode=="stall"?1:10));
        var request=basic with { Kind="stacks",StackPolicy=new(MaximumTemporaryBytes:1048576),Capture=basic.Capture with { Profile=Profiles.Resolve("cpu-diagnostic") } };
        int pid=0; await using var worker=new TraceWorkerClient(FaultWorker,[mode],(id,_)=>pid=id);
        var error=await Assert.ThrowsAsync<LabException>(()=>worker.AnalyzeStacksAsync(request)); Assert.Equal(code,error.Code);
        AssertGone(pid); Assert.False(File.Exists(request.OutputPath)); Assert.True(File.Exists(request.SourcePath));
        Assert.False(Directory.EnumerateDirectories(Path.Combine(workspace.Root,"incoming"),"stack-conversion-*",SearchOption.AllDirectories).Any());
    }
    [Fact,Case("IT-PARSER-STACK-002","FR-016,FR-070,FR-072","M7","Integration")]
    public async Task QueuedCancellationCannotDeleteTheActiveWorkersNamespace()
    {
        using var workspace=new TestWorkspace(); var basic=await Request(workspace); var request=basic with { Kind="stacks",StackPolicy=new() };
        using var activeStop=new CancellationTokenSource(); using var queuedStop=new CancellationTokenSource();
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker=new TraceWorkerClient(FaultWorker,["stall"],(_,_)=>started.TrySetResult());
        var active=worker.AnalyzeStacksAsync(request,activeStop.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var queued=worker.AnalyzeStacksAsync(request,queuedStop.Token);
        string scratch=Path.Combine(Path.GetDirectoryName(request.OutputPath)!,"stack-conversion-"+Path.GetFileNameWithoutExtension(request.OutputPath));
        Directory.CreateDirectory(scratch); string marker=Path.Combine(scratch,"trace.etlx"); await File.WriteAllTextAsync(marker,"active");
        await File.WriteAllTextAsync(request.OutputPath,"active output"); queuedStop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>queued);
        Assert.Equal("active",await File.ReadAllTextAsync(marker)); Assert.Equal("active output",await File.ReadAllTextAsync(request.OutputPath));
        activeStop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>active);
        Assert.False(Directory.Exists(scratch)); Assert.False(File.Exists(request.OutputPath));
    }
    [Theory,InlineData("stall","parser_timeout"),InlineData("crash","parser_failed"),InlineData("identity","invalid_parser_response"),InlineData("memory","parser_memory_limit"),
        InlineData("source","invalid_parser_response"),InlineData("hash","artifact_corrupt")]
    [Case("IT-PARSER-001","FR-013,FR-016,FR-072","M7","Integration")]
    public async Task WorkerFaultsRespectContainmentAndVerifyShutdownWithoutDeletingRawEvidence(string mode,string code)
    {
        using var workspace=new TestWorkspace(); var request=await Request(workspace,new(MaximumCommittedMemoryBytes:134217728,MaximumDurationSeconds:mode=="stall"?1:10));
        int pid=0; long peak=0; bool terminated=false;
        await using var worker=new TraceWorkerClient(FaultWorker,[mode],(id,_)=>pid=id,(id,bytes)=> { Assert.Equal(pid,id); peak=bytes; terminated=true; });
        var error=await Assert.ThrowsAsync<LabException>(()=>worker.AnalyzeAsync(request)); Assert.Equal(code,error.Code);
        Assert.True(terminated); AssertGone(pid); Assert.True(peak>0);
        if(OperatingSystem.IsWindows()||mode!="memory") Assert.True(peak<=request.Policy.MaximumCommittedMemoryBytes);
        if(mode=="memory") Assert.True(peak>request.Policy.MaximumCommittedMemoryBytes/2);
        Assert.True(File.Exists(request.SourcePath)); Assert.False(File.Exists(request.OutputPath));
        await new ArtifactStore(workspace.Root).VerifyAsync(request.SourceArtifact.Hash,request.SourceArtifact.Length);
    }
    [Fact,Case("IT-PARSER-002","FR-016,FR-038","M7","Integration")]
    public async Task CancellationAndControllerDisposalJoinTheWorker()
    {
        using var workspace=new TestWorkspace(); var request=await Request(workspace); using var cancel=new CancellationTokenSource(); int pid=0;
        await using(var worker=new TraceWorkerClient(FaultWorker,["stall"],(id,_)=> { pid=id; cancel.Cancel(); }))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>worker.AnalyzeAsync(request,cancel.Token));
        AssertGone(pid);
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disposable=new TraceWorkerClient(FaultWorker,["stall"],(id,_)=> { pid=id; started.SetResult(); });
        var pending=disposable.AnalyzeAsync(request); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await disposable.DisposeAsync(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending); AssertGone(pid);
    }
    [Fact,Case("IT-PARSER-003,UT-PARSER-002","FR-016,FR-026,FR-038,FR-072,FR-073","M1","Integration")]
    public async Task RealTraceReanalysisRunsInSeparateProcessAndRetainsSourceAndFootprintAcrossRestart()
    {
        using var workspace=new TestWorkspace(); string analysisId,captureId;
        await using(var service=new LabService(workspace.Root))
        {
            using var target=await CaptureTests.StartTarget();
            try
            {
                var identity=service.Discover().Single(t=>t.Identity.ProcessId==target.Id).Identity;
                var grant=service.IssueGrant(identity.Id,Permission.Observe,60,1);
                var completed=await ServiceTests.Wait(service,service.StartCapture(identity.Id,0,"baseline-context",2,grant.Id,"worker-capture").Id);
                Assert.Equal(OperationState.Completed,completed.State); captureId=completed.ResultId!;
            }
            finally { if(!target.HasExited) target.Kill(true); await target.WaitForExitAsync(); }
            var capture=service.Catalog.Get<Capture>(captureId); var artifact=service.Catalog.Get<Artifact>(capture.ArtifactId!);
            var direct=new TraceAnalyzer().Analyze(await service.Artifacts.VerifiedPathAsync(artifact),capture);
            var tools=new LabTools(service); var response=await tools.Run(new StartTraceAnalysis(captureId,"reanalysis")); Assert.Equal("ok",response.Status);
            var accepted=response.Data!.Value.Deserialize<Operation>(Wire.Json)!;
            Assert.Equal(accepted.Id,service.StartTraceAnalysis(captureId,"reanalysis").Id);
            var done=await ServiceTests.Wait(service,accepted.Id); Assert.Equal(OperationState.Completed,done.State); analysisId=done.ResultId!;
            var summary=await service.AnalysisAsync(analysisId); Assert.NotEmpty(summary.GcCycles);
            Assert.Equal(Wire.Hash(direct with { Id="same" }),Wire.Hash(summary with { Id="same",ParserExecution=null }));
            var execution=Assert.IsType<TraceParserExecution>(summary.ParserExecution); Assert.NotEqual(Environment.ProcessId,execution.ProcessId);
            Assert.True(execution.ExitVerified); Assert.Equal("isolated_trace_parser",execution.Scope); Assert.Equal(TimeSpan.TicksPerSecond,execution.CpuFrequency);
            if(OperatingSystem.IsWindows()) Assert.True(execution.PeakCommittedBytes>0&&execution.PeakCommittedBytes<=execution.Policy.MaximumCommittedMemoryBytes);
            else { Assert.Equal(0,execution.PeakCommittedBytes); Assert.False(execution.PrivateCommitCollected); Assert.True(execution.PeakAccountedMemoryBytes>0&&execution.PeakAccountedMemoryBytes<=execution.Policy.MaximumCommittedMemoryBytes); Assert.Contains("cgroup_v2",execution.MemoryAccounting); Assert.Equal(0,execution.OomKills); }
            AssertGone(execution.ProcessId); Assert.True(File.Exists(await service.Artifacts.VerifiedPathAsync(artifact)));
            Assert.True(System.Text.Encoding.UTF8.GetByteCount(Wire.Serialize(direct))>1024);
            await using var bounded=new TraceWorkerClient(); var request=new TraceWorkerRequest(Wire.Version,workspace.Root,await service.Artifacts.VerifiedPathAsync(artifact),
                artifact,capture,service.Artifacts.CreateIncoming("output-cap"),new(MaximumOutputBytes:1024));
            Assert.Equal("resource_limit",(await Assert.ThrowsAsync<LabException>(()=>bounded.AnalyzeAsync(request))).Code);
            Assert.False(File.Exists(request.OutputPath)); await service.Artifacts.VerifyAsync(artifact.Hash,artifact.Length);
            var capped=await bounded.AnalyzeAsync(request with { OutputPath=service.Artifacts.CreateIncoming("event-cap"),Policy=new(MaximumEvents:1) });
            Assert.Contains("analysis_event_cap",capped.Quality.Warnings); Assert.Equal("partial",capped.Quality.Coverage);
            Assert.True(capped.ParserExecution!.ExitVerified);
        }
        await using var reopened=new LabService(workspace.Root); Assert.True((await reopened.AnalysisAsync(analysisId)).ParserExecution!.ExitVerified);
        Assert.Equal(captureId,(await reopened.AnalysisAsync(analysisId)).CaptureId);
    }
    [Fact,Case("IT-PARSER-004","FR-016,FR-038","M7","Integration")]
    public async Task QueuedParserRequestsHaveDeadlinesAndCannotStartAnotherWorker()
    {
        using var workspace=new TestWorkspace(); var request=await Request(workspace); using var stop=new CancellationTokenSource(); int starts=0;
        var started=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var worker=new TraceWorkerClient(FaultWorker,["stall"],(_,_)=> { Interlocked.Increment(ref starts); started.TrySetResult(); });
        var first=worker.AnalyzeAsync(request,stop.Token); await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second=request with { OutputPath=new ArtifactStore(workspace.Root).CreateIncoming("queued"),Policy=new(MaximumDurationSeconds:1) };
        Assert.Equal("parser_timeout",(await Assert.ThrowsAsync<LabException>(()=>worker.AnalyzeAsync(second))).Code); Assert.Equal(1,starts);
        stop.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>first);
    }
    [Fact,Case("UT-PARSER-001","FR-016,FR-070,FR-071","M7","Contract")]
    public async Task UnsafeOrPreexistingOutputNamesAreRejectedWithoutDeletingFiles()
    {
        using var workspace=new TestWorkspace(); var request=await Request(workspace); await using var worker=new TraceWorkerClient();
        string unrelated=Path.Combine(workspace.Root,"retained.json"); await File.WriteAllTextAsync(unrelated,"keep");
        Assert.Equal("invalid_path",(await Assert.ThrowsAsync<LabException>(()=>worker.AnalyzeAsync(request with { OutputPath=unrelated }))).Code);
        Assert.Equal("keep",await File.ReadAllTextAsync(unrelated));
        await File.WriteAllTextAsync(request.OutputPath,"keep partial");
        Assert.Equal("invalid_path",(await Assert.ThrowsAsync<LabException>(()=>worker.AnalyzeAsync(request))).Code);
        Assert.Equal("keep partial",await File.ReadAllTextAsync(request.OutputPath));
    }
}
