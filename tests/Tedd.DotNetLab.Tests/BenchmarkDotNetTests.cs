using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class BenchmarkDotNetTests
{
    private const string Report = """
        {"HostEnvironmentInfo":{"BenchmarkDotNetVersion":"0.15.8","RuntimeVersion":".NET 10.0"},
         "Benchmarks":[{"FullName":"Bench.Sum","DisplayInfo":"Bench.Sum: Default","Parameters":"Length=1024",
          "Statistics":{"N":2,"Mean":15,"Median":15,"StandardDeviation":7.071,"StandardError":5,
            "ConfidenceInterval":{"Lower":1,"Upper":29,"Level":99.9}},
          "Memory":{"Gen0Collections":1,"Gen1Collections":0,"Gen2Collections":0,"TotalOperations":1000,"BytesAllocatedPerOperation":24},
          "Measurements":[
            {"IterationMode":"Workload","IterationStage":"Pilot","LaunchIndex":1,"Operations":1,"Nanoseconds":100},
            {"IterationMode":"Workload","IterationStage":"Result","LaunchIndex":1,"Operations":100,"Nanoseconds":1000},
            {"IterationMode":"Workload","IterationStage":"Result","LaunchIndex":2,"Operations":100,"Nanoseconds":2000}]}]}
        """;

    [Fact]
    public void FullReport_PreservesLaunchesStatisticsAndAllocationDenominator()
    {
        var report = BenchmarkDotNetReportReader.Read(Report, "artifact");
        var row = Assert.Single(report.Benchmarks);
        Assert.Equal(2, row.Samples);
        Assert.Equal(2, row.Launches);
        Assert.Equal(15, row.MeanNanoseconds);
        Assert.Equal(24, row.AllocatedBytesPerOperation);
        Assert.Equal(1000, row.MemoryTotalOperations);
        Assert.Equal(1, row.Gen0Collections);
        Assert.Equal(29, row.ConfidenceInterval.GetProperty("Upper").GetInt32());
        Assert.Equal("0.15.8", report.HostEnvironment.GetProperty("BenchmarkDotNetVersion").GetString());
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("missing_samples")]
    [InlineData("zero_operations")]
    [InlineData("negative_mean")]
    [InlineData("infinity")]
    public void FullReport_RejectsUnusableResults(string defect)
    {
        var root = JsonNode.Parse(Report)!;
        var row = root["Benchmarks"]![0]!;
        switch (defect)
        {
            case "failed": row["Statistics"] = null; break;
            case "missing_samples": row["Measurements"]!.AsArray().RemoveAt(2); break;
            case "zero_operations": row["Measurements"]![1]!["Operations"] = 0; break;
            case "negative_mean": row["Statistics"]!["Mean"] = -1; break;
            case "infinity": row["Statistics"]!["Mean"] = "Infinity"; break;
        }
        Assert.Equal("invalid_benchmark_report", Assert.Throws<LabException>(() =>
            BenchmarkDotNetReportReader.Read(root.ToJsonString(), "artifact")).Code);
    }

    [Fact]
    public void Scaffold_RequiresKnownEngineAndPreservesExistingFiles()
    {
        using var workspace = new TestWorkspace();
        string directory = Path.Combine(workspace.Root, "benchmark");
        Assert.Equal("invalid_request", Assert.Throws<LabException>(() => ScaffoldService.Create(directory, engine: "unknown")).Code);
        Assert.False(Directory.Exists(directory));
        var scaffold = ScaffoldService.Create(directory, engine: "benchmarkdotnet");
        Assert.Equal("benchmarkdotnet", scaffold.Engine);
        Assert.NotNull(scaffold.RunCommand);
        File.WriteAllText(scaffold.ProgramPath, "user code");
        Assert.Equal("invalid_path", Assert.Throws<LabException>(() => ScaffoldService.Create(directory, engine: "benchmarkdotnet")).Code);
        Assert.Equal("user code", File.ReadAllText(scaffold.ProgramPath));
    }

    [Fact]
    public async Task BenchmarkRun_RejectsOutOfGrantPathsBeforeLaunching()
    {
        using var workspace = new TestWorkspace();
        await using var service = new LabService(workspace.Root, collectControllerResources: false);
        string scratch = Path.Combine(workspace.Root, "scratch");
        Directory.CreateDirectory(scratch);
        var grant = service.IssueScratchGrantPair(scratch, 120, 2).ExecuteGrant;
        var tools = new LabTools(service);
        var response = await tools.Run(new StartBenchmarkDotNet(Path.Combine(workspace.Root, "outside.dll"), grant.Id, "outside"));
        Assert.Equal("permission_denied", response.Code);
        Assert.Equal("invalid_plan", (await tools.Run(new StartBenchmarkDotNet("x", grant.Id, "bad", LaunchCount: 0))).Code);
        Assert.Equal("invalid_request", (await tools.Run(new StartBenchmarkDotNet("x", grant.Id, "bad-filter", Filter: "@args.rsp"))).Code);
    }

    [Fact]
    public async Task BenchmarkDotNet_EndToEnd_RetainsFullReportsAndFailedRunOutput()
    {
        using var workspace = new TestWorkspace();
        string scratch = Path.Combine(workspace.Root, "benchmark");
        var scaffold = ScaffoldService.Create(scratch, engine: "benchmarkdotnet");
        var build = new ProcessStartInfo("dotnet") { WorkingDirectory = scratch, RedirectStandardOutput = true,
            RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (string argument in new[] { "build", scaffold.ProjectPath, "-c", "Release", "-p:UseSharedCompilation=false", "-nodeReuse:false" })
            build.ArgumentList.Add(argument);
        using (var process = Process.Start(build)!)
        {
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3)); }
            finally { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
            Assert.True(process.ExitCode == 0, await stdout + await stderr);
        }

        await using var service = new LabService(Path.Combine(workspace.Root, "catalog"), collectControllerResources: false);
        var grant = service.IssueScratchGrantPair(scratch, 600, 4).ExecuteGrant;
        var tools = new LabTools(service);
        var request = new StartBenchmarkDotNet(scaffold.ExpectedOutputDllPath, grant.Id, "bdn-short", "*Baseline*", "short", 2, 240);
        var response = await tools.Run(request);
        Assert.Equal("ok", response.Status);
        var operation = response.Data!.Value.Deserialize<Operation>(Wire.Json)!;
        var done = await WaitAsync(service, operation.Id);
        Assert.True(done.ResultId is not null, Wire.Serialize(done) + "\n" + string.Join('\n',
            Directory.EnumerateFiles(workspace.Root, "stdout.log", SearchOption.AllDirectories).Select(File.ReadAllText)));
        var result = service.Catalog.Get<BenchmarkDotNetResult>(done.ResultId!);
        Assert.True(done.State == OperationState.Completed,
            $"{done.ErrorCode}: {File.ReadAllText(Path.Combine(result.EvidenceDirectory, "stdout.log"))}");
        var report = Assert.Single(result.Reports);
        var benchmark = Assert.Single(report.Benchmarks);
        Assert.Equal(2, benchmark.Launches);
        Assert.InRange(benchmark.Samples, 2, 6);
        Assert.True(benchmark.MeanNanoseconds > 0);
        Assert.NotNull(benchmark.AllocatedBytesPerOperation);
        Assert.False(result.PromotionEligible);
        foreach (string artifactId in result.ArtifactIds)
            Assert.True(File.Exists(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(artifactId))));
        string json = await File.ReadAllTextAsync(await service.Artifacts.VerifiedPathAsync(service.Catalog.Get<Artifact>(report.ArtifactId)));
        Assert.Contains("Pilot", json);
        Assert.Contains("Overhead", json);
        Assert.Contains("Warmup", json);
        Assert.Equal(operation.Id, (await tools.Run(request)).Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
        Assert.Equal("ok", (await tools.Query(new QueryHistory("benchmarkdotnet_result"))).Status);

        var failedResponse = await tools.Run(request with { IdempotencyKey = "bdn-no-match", Filter = "*DoesNotExist*" });
        var failed = await WaitAsync(service, failedResponse.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
        Assert.Equal(OperationState.Failed, failed.State);
        var failedResult = service.Catalog.Get<BenchmarkDotNetResult>(failed.ResultId!);
        Assert.NotEmpty(failedResult.ArtifactIds);
        Assert.Empty(failedResult.Reports);
        Assert.True(File.Exists(Path.Combine(failedResult.EvidenceDirectory, "stdout.log")));

        var dryResponse = await tools.Run(request with { IdempotencyKey = "bdn-dry", Job = "dry", LaunchCount = 1 });
        var dry = await WaitAsync(service, dryResponse.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
        Assert.True(dry.State == OperationState.Completed, Wire.Serialize(dry));
        var dryResult = service.Catalog.Get<BenchmarkDotNetResult>(dry.ResultId!);
        Assert.Equal(1, Assert.Single(Assert.Single(dryResult.Reports).Benchmarks).Samples);
        Assert.False(dryResult.PromotionEligible);

        var timedOutResponse = await tools.Run(request with { IdempotencyKey = "bdn-timeout", MaximumDurationSeconds = 1 });
        var timedOut = await WaitAsync(service, timedOutResponse.Data!.Value.Deserialize<Operation>(Wire.Json)!.Id);
        Assert.Equal(OperationState.Failed, timedOut.State);
        Assert.Equal("cancelled", timedOut.ErrorCode);
        Assert.NotEmpty(service.Catalog.Get<BenchmarkDotNetResult>(timedOut.ResultId!).ArtifactIds);
    }

    private static async Task<Operation> WaitAsync(LabService service, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        while (true)
        {
            var operation = service.Catalog.GetOperation(id);
            if (operation.State is OperationState.Completed or OperationState.Failed or OperationState.Tainted or OperationState.Interrupted)
                return operation;
            await Task.Delay(100, deadline.Token);
        }
    }
}
