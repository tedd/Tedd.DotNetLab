using System.Diagnostics;
using System.Text.Json;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Domain;
using Tedd.DotNetLab.Harness;
using Tedd.DotNetLab.Host;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ToolingEnhancementsTests
{
    private static readonly TargetIdentity TestTarget = new("test-target", 1, 1, "test-target", "10.0.12", "x64");

    private static Tedd.DotNetLab.Contracts.RunRequest MakeRunRequest(ScenarioDescriptor descriptor) =>
        new("exec-1", "run-1", "block-1", descriptor.Id, "baseline", 100, 2, true, !descriptor.IsAsync, 0, TestTarget,
            new("grant-1", "workspace", TestTarget.Id, 1, Permission.ExecuteRegisteredScenario, DateTimeOffset.UtcNow.AddMinutes(5), 1000),
            DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class TelemetryStimulusScenario : ISynchronousScenario
    {
        private long sum;
        public ScenarioDescriptor Describe() => new("stimulus", "1", "hash", ResetLevel.LogicalState, ThreadAffinity.Any, false, "doc", ["baseline", "candidate"]);
        public void Prepare(ScenarioContext c) { }
        public void Reset(ResetContext c) => sum = 0;
        public long ExecuteBatch(int operations)
        {
            long local = 0;
            for (int i = 0; i < operations; i++)
            {
                var arr = new byte[1024];
                arr[i & 1023] = (byte)i;
                local += arr[i & 1023];
            }
            sum += local;
            return operations;
        }
        public ValidationResult Validate() => new(sum > 0);
        public void Cleanup() { }
    }

    [Fact, Case("UT-TELEMETRY-001", "FR-TELEMETRY", "M1", "Unit")]
    public async Task BatchTelemetry_CapturesCpuAndGcSnapshot_AndPopulatesDomainMetrics()
    {
        var scenario = new TelemetryStimulusScenario();
        var registry = new ScenarioRegistry();
        registry.Register(scenario);

        var runner = new ScenarioRunner(registry, TestTarget, "workspace");
        var measurement = await runner.ExecuteAsync(MakeRunRequest(scenario.Describe()), CancellationToken.None);

        Assert.NotNull(measurement.Telemetry);
        var telemetry = measurement.Telemetry;

        Assert.True(telemetry.HeapSizeBytes > 0, "HeapSizeBytes should be positive.");
        Assert.True(telemetry.TotalCommittedBytes > 0, "TotalCommittedBytes should be positive.");
        Assert.True(telemetry.Gen0Collections >= 0);
        Assert.True(telemetry.Gen1Collections >= 0);
        Assert.True(telemetry.Gen2Collections >= 0);

        if (OperatingSystem.IsWindows())
        {
            Assert.NotNull(telemetry.ThreadCpuCycles);
            Assert.True(telemetry.ThreadCpuCycles > 0, "Windows thread CPU cycles should be positive.");

            var cycleMetric = Metrics.Read("cpu.thread_cycles_per_operation", measurement);
            Assert.Equal(Availability.Available, cycleMetric.Status);
            Assert.NotNull(cycleMetric.Value);
            Assert.True(cycleMetric.Value > 0);
        }

        var heapMetric = Metrics.Read("gc.heap_size_bytes", measurement);
        Assert.Equal(Availability.Available, heapMetric.Status);
        Assert.NotNull(heapMetric.Value);
        Assert.True(heapMetric.Value > 0);

        var commitMetric = Metrics.Read("gc.committed_bytes", measurement);
        Assert.Equal(Availability.Available, commitMetric.Status);
        Assert.NotNull(commitMetric.Value);
        Assert.True(commitMetric.Value > 0);

        var gen0Metric = Metrics.Read("gc.gen0_collections", measurement);
        Assert.Equal(Availability.Available, gen0Metric.Status);
        Assert.NotNull(gen0Metric.Value);
        Assert.True(gen0Metric.Value >= 0);
    }

    [Fact, Case("IT-DIAG-JIT-CONTENTION-001", "FR-JIT-DIAG", "M7", "Integration")]
    public async Task JitAndContentionProfile_CapturesEvents_AndServesMcpQueries()
    {
        using var workspace = new TestWorkspace();
        using var target = await CaptureTests.StartTarget(workspace.Root);

        await using var service = new LabService(workspace.Root);
        var identity = service.Discover().Single(t => t.Identity.ProcessId == target.Id).Identity;
        var handshake = await service.ScenariosAsync(identity.Id, CancellationToken.None);
        identity = handshake.Target;
        var scenario = handshake.Scenarios.Single(s => s.Id == "native-inliner");

        var plan = service.Plan(new("", scenario.Id, scenario.Version, scenario.InputHash, scenario.Variants,
            2, 2048, 1, 42, "jit-and-contention-diagnostic", false, "", false, 60, new()));

        var executeGrant = service.IssueGrant(identity.Id, Permission.ExecuteRegisteredScenario, 120, 4);
        var observeGrant = service.IssueGrant(identity.Id, Permission.Observe, 120, 1);

        var tools = new LabTools(service);
        var startReq = new StartExecution(identity.Id, "0", plan.Id, executeGrant.Id, "jit-contention-exec",
            ObservationGrantId: observeGrant.Id);

        var accepted = await tools.Run(startReq);
        Assert.Equal("ok", accepted.Status);
        var op = accepted.Data!.Value.Deserialize<Operation>(Wire.Json)!;
        var completed = await ServiceTests.Wait(service, op.Id);
        Assert.Equal(OperationState.Completed, completed.State);

        var execution = service.Catalog.Get<ExecutionRecord>(completed.ResultId!);
        Assert.NotNull(execution.AnalysisId);
        string analysisId = execution.AnalysisId!;

        var summary = await service.AnalysisAsync(analysisId);
        Assert.NotNull(summary.JitSummary);
        Assert.NotNull(summary.ContentionSummary);

        // Verify MCP Query views
        var methodsQuery = new QueryJitMethods(analysisId, Limit: 50);
        var methodsObj = await tools.Query(methodsQuery);
        Assert.NotNull(methodsObj);

        var inliningQuery = new QueryJitInlining(analysisId, FailuresOnly: false, Limit: 50);
        var inliningObj = await tools.Query(inliningQuery);
        Assert.NotNull(inliningObj);

        var contentionQuery = new QueryContentions(analysisId, Limit: 50);
        var contentionObj = await tools.Query(contentionQuery);
        Assert.NotNull(contentionObj);

        // Verify metrics
        var metricQuery = new QueryMetric(analysisId, "jit.methods_compiled");
        var metricObj = await tools.Query(metricQuery);
        Assert.NotNull(metricObj);
    }

    [Fact, Case("IT-SCAFFOLD-SCRATCH-JITDISASM-001", "FR-SCRATCH-DISASM", "M7", "Integration")]
    public async Task ScaffoldService_ScratchGrant_AndJitDisasm_EndToEnd()
    {
        using var workspace = new TestWorkspace();
        string scratchDir = Path.Combine(workspace.Root, "my-scratch-bench");

        // 1. Scaffold
        var scaffold = ScaffoldService.Create(scratchDir, "scratch-bench", overwrite: true);
        Assert.True(File.Exists(scaffold.ProjectPath));
        Assert.True(File.Exists(scaffold.ProgramPath));

        // 2. Build the scratch project using host dotnet
        var psi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = scratchDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.Environment["TEMP"] = workspace.Root;
        psi.Environment["TMP"] = workspace.Root;
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(scaffold.ProjectPath);
        psi.ArgumentList.Add("-c");
        psi.ArgumentList.Add("Release");
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v");
        psi.ArgumentList.Add("q");

        using var buildProcess = Process.Start(psi)!;
        string buildStdout = await buildProcess.StandardOutput.ReadToEndAsync();
        string buildStderr = await buildProcess.StandardError.ReadToEndAsync();
        await buildProcess.WaitForExitAsync();
        Assert.True(buildProcess.ExitCode == 0, $"Build failed:\n{buildStderr}\n{buildStdout}");

        string dllPath = Path.Combine(scratchDir, "bin", "Release", "net10.0", "ScratchBench.dll");
        Assert.True(File.Exists(dllPath), $"Expected output DLL not found at: {dllPath}");

        // 3. Issue ONE scratch grant pair for the scratch directory
        await using var service = new LabService(workspace.Root);
        var grantPair = service.IssueScratchGrantPair(scratchDir, 180, 20);
        Assert.Equal(Permission.Observe, grantPair.ObserveGrant.Permission);
        Assert.Equal(Permission.ExecuteRegisteredScenario, grantPair.ExecuteGrant.Permission);

        // 4. Capture JIT Disassembly on the scratch executable
        var disasmOp = service.StartJitDisasm(
            methodFilter: "*Execute*",
            scenarioId: "scratch-bench",
            variant: "candidate",
            grantId: grantPair.ExecuteGrant.Id,
            idempotencyKey: "test-scratch-disasm-1",
            executablePath: dllPath,
            operations: 100,
            warmupBatches: 2,
            tieredPgo: true,
            maximumDurationSeconds: 60);

        var completedDisasm = await ServiceTests.Wait(service, disasmOp.Id);
        Assert.Equal(OperationState.Completed, completedDisasm.State);
        Assert.NotNull(completedDisasm.ResultId);

        // 5. Query JIT Disasm result via Catalog and QueryJitDisasmAsync
        var disasmRecord = service.Catalog.Get<JitDisasmResult>(completedDisasm.ResultId!);
        Assert.NotEmpty(disasmRecord.Methods);

        var candMethod = disasmRecord.Methods.FirstOrDefault(m => m.MethodSignature.Contains("ExecuteCandidate") || m.MethodSignature.Contains("ExecuteBaseline"));
        Assert.NotNull(candMethod);
        Assert.True(candMethod.CodeSizeBytes > 0, "Disassembled method should have positive code size in bytes.");

        var queryObj = await service.QueryJitDisasmAsync(completedDisasm.ResultId!);
        var queryJson = JsonSerializer.SerializeToNode(queryObj, Wire.Json)!.AsObject();
        Assert.True(queryJson["matched_methods"]!.GetValue<int>() > 0);

        // 6. Test grant durability across recompile under the same scratch directory without a new grant!
        // Append a dummy comment to Program.cs and rebuild
        await File.AppendAllTextAsync(scaffold.ProgramPath, "\n// Durable grant verification rebuild comment\n");

        var rebuildPsi = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = scratchDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        rebuildPsi.Environment["TEMP"] = workspace.Root;
        rebuildPsi.Environment["TMP"] = workspace.Root;
        rebuildPsi.ArgumentList.Add("build");
        rebuildPsi.ArgumentList.Add(scaffold.ProjectPath);
        rebuildPsi.ArgumentList.Add("-c");
        rebuildPsi.ArgumentList.Add("Release");
        rebuildPsi.ArgumentList.Add("-nologo");
        rebuildPsi.ArgumentList.Add("-v");
        rebuildPsi.ArgumentList.Add("q");

        using var rebuildProcess = Process.Start(rebuildPsi)!;
        await rebuildProcess.WaitForExitAsync();
        Assert.Equal(0, rebuildProcess.ExitCode);

        // Verify the SAME grant still authorizes disasm run #2 on the rebuilt DLL
        var disasmOp2 = service.StartJitDisasm(
            methodFilter: "*Execute*",
            scenarioId: "scratch-bench",
            variant: "candidate",
            grantId: grantPair.ExecuteGrant.Id,
            idempotencyKey: "test-scratch-disasm-2",
            executablePath: dllPath,
            operations: 100,
            warmupBatches: 2,
            tieredPgo: true,
            maximumDurationSeconds: 60);

        var completedDisasm2 = await ServiceTests.Wait(service, disasmOp2.Id);
        Assert.Equal(OperationState.Completed, completedDisasm2.State);
        Assert.NotNull(completedDisasm2.ResultId);
    }

    [Fact, Case("IT-CLI-FORWARD-001", "FR-CLI-FORWARD", "M0", "Integration")]
    public async Task SharedController_ForwardsCliGrantCommandsOverPipe()
    {
        using var workspace = new TestWorkspace();
        string scratchDir = Path.Combine(workspace.Root, "scratch-area");
        Directory.CreateDirectory(scratchDir);

        // When controller.lock does NOT exist, TryForwardCliAsync returns null (falls back to local execution)
        var noForward = await SharedController.TryForwardCliAsync(workspace.Root, ["grant-scratch", workspace.Root, scratchDir, "60", "5"], null);
        Assert.Null(noForward);

        // Start shared controller session
        using var cancel = new CancellationTokenSource();
        var controllerTask = Task.Run(() => SharedController.RunAsync(workspace.Root, cancel.Token));

        // Wait up to 5 seconds for controller.lock to appear and pipe to be ready
        string lockFile = Path.Combine(workspace.Root, "controller.lock");
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(5) && !File.Exists(lockFile))
            await Task.Delay(50);
        Assert.True(File.Exists(lockFile), "controller.lock should exist while SharedController is running.");

        try
        {
            // Now invoke TryForwardCliAsync with a grant-scratch command
            var forwarded = await SharedController.TryForwardCliAsync(
                workspace.Root,
                ["grant-scratch", workspace.Root, scratchDir, "120", "10"],
                null);

            Assert.NotNull(forwarded);
            Assert.Equal(0, forwarded.ExitCode);
            Assert.Contains("execute_grant", forwarded.StandardOutput);
            Assert.Contains("observe_grant", forwarded.StandardOutput);
        }
        finally
        {
            cancel.Cancel();
            try { await controllerTask; } catch (OperationCanceledException) { }
        }

        // Re-check through catalog after controller stops that the grant was recorded
        await using var service = new LabService(workspace.Root);
        var grants = service.Catalog.List("grant");
        Assert.True(grants.Rows.Count >= 2, "Scratch grant pair should be written into catalog.");
    }
}
