using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record BenchmarkDotNetResult(string Id, string State, string ExecutablePath, string EntryAssemblyHash,
    string Filter, string Job, int LaunchCount, string EvidenceDirectory, IReadOnlyList<string> ArtifactIds,
    IReadOnlyList<BenchmarkDotNetReport> Reports, string? Failure = null,
    bool PromotionEligible = false,
    string SamplingUnit = "BenchmarkDotNet workload iterations within launches; not independent randomized DotNetLab blocks.");

public sealed partial class LabService
{
    public Operation StartBenchmarkDotNet(string executablePath, string grantId, string idempotencyKey,
        string filter = "*", string job = "default", int launchCount = 2, int maximumDurationSeconds = 600)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Length > 256 || filter[0] is '-' or '@' || filter.Any(char.IsControl) ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            throw new LabException("invalid_request", "A bounded benchmark filter and idempotency key are required.");
        if (job is not ("default" or "short" or "dry") || launchCount is < 1 or > 10 ||
            maximumDurationSeconds < 1 || maximumDurationSeconds > Math.Min(3600, Limits.MaximumDurationSeconds))
            throw new LabException("invalid_plan", "Invalid BenchmarkDotNet job, launch count, or duration budget.");

        var grant = Record<Grant>(grantId, "grant");
        string executable = AuthorizeScratchExecutable(grant, executablePath);
        string hash = Wire.Hash(new { executable, grantId, filter, job, launchCount, maximumDurationSeconds });
        var target = new TargetIdentity("benchmarkdotnet-" + grant.TargetId, 0, 0, "benchmarkdotnet", RuntimeSupport.Current, "X64");
        return Start("benchmarkdotnet", target, idempotencyKey, hash, async (operation, cancellation) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(maximumDurationSeconds));
            using var permit = await activityScheduler.EnterMeasurementAsync(deadline.Token);
            executable = AuthorizeScratchExecutable(grant, executable);
            Catalog.UseGrant(grant);

            string directory = Path.Combine(Catalog.Root, "benchmarks", operation.Id);
            VerifyDirectoryAncestors(directory);
            Directory.CreateDirectory(directory);
            string artifactsDirectory = Path.Combine(directory, "artifacts");
            string temporary = Path.Combine(directory, "tmp");
            Directory.CreateDirectory(temporary);
            string assemblyHash;
            using (var assembly = File.OpenRead(executable)) assemblyHash = Convert.ToHexStringLower(SHA256.HashData(assembly));
            var result = new BenchmarkDotNetResult(Wire.Id("benchmarkdotnet"), "running", executable, assemblyHash,
                filter, job, launchCount, directory, [], []);
            var artifacts = new List<string>();
            var reports = new List<BenchmarkDotNetReport>();
            string? failure = null;
            try
            {
                var (runtimeVersion, rollForward) = ReadRuntimeConfig(Path.ChangeExtension(executable, ".runtimeconfig.json"));
                var start = RuntimeSupport.StartInfo(runtimeVersion, rollForward, out _);
                start.WorkingDirectory = Path.GetDirectoryName(executable)!;
                start.RedirectStandardInput = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                foreach (string argument in new[] { executable, "--filter", filter, "--job", job, "--launchCount",
                    launchCount.ToString(CultureInfo.InvariantCulture), "--memory", "--exporters", "fulljson", "csvmeasurements",
                    "--artifacts", artifactsDirectory, "--keepFiles", "--stopOnFirstError" }) start.ArgumentList.Add(argument);
                // Preserve SDK/package discovery while preventing inherited profiler or JIT instrumentation.
                foreach (string key in start.Environment.Keys.ToArray())
                    if (key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase) ||
                        key.StartsWith("COMPlus_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("DOTNET_", StringComparison.OrdinalIgnoreCase))
                        start.Environment.Remove(key);
                start.Environment["CORECLR_ENABLE_PROFILING"] = "0";
                start.Environment["DOTNET_ENABLE_PROFILING"] = "0";
                start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                start.Environment["DOTNET_CLI_HOME"] = temporary;
                start.Environment["TEMP"] = temporary;
                start.Environment["TMP"] = temporary;
                start.Environment["TMPDIR"] = temporary;
                start.Environment["MSBUILDDISABLENODEREUSE"] = "1";

                using var process = new Process { StartInfo = start };
                bool started = false;
                using var outputCancellation = new CancellationTokenSource();
                Task? stdout = null, stderr = null;
                try
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    if (!process.Start()) throw new LabException("process_start_failed", "BenchmarkDotNet process could not start.");
                    started = true;
                    process.StandardInput.Close();
                    stdout = SaveBenchmarkOutputAsync(process.StandardOutput, Path.Combine(directory, "stdout.log"), outputCancellation.Token);
                    stderr = SaveBenchmarkOutputAsync(process.StandardError, Path.Combine(directory, "stderr.log"), outputCancellation.Token);
                    var pending = new List<Task> { process.WaitForExitAsync(deadline.Token), stdout, stderr };
                    while (pending.Count > 0)
                    {
                        var completed = await Task.WhenAny(pending).WaitAsync(deadline.Token);
                        await completed;
                        pending.Remove(completed);
                    }
                    if (process.ExitCode != 0) throw new LabException("benchmark_failed", "BenchmarkDotNet returned a failing exit code; inspect retained output.");
                }
                finally
                {
                    if (started && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                    }
                    outputCancellation.Cancel();
                    foreach (var drain in new[] { stdout, stderr })
                        if (drain is not null)
                            try { await drain; }
                            catch (Exception error) when (error is OperationCanceledException or IOException or LabException) { }
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure = error is LabException lab ? lab.Code : error is OperationCanceledException ? "cancelled" : "benchmark_failed";
            }

            // Retain partial reports and logs even when compilation, validation, or execution fails.
            try
            {
                foreach (string path in Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
                    artifacts.Add(await RetainBenchmarkFileAsync(path, operation.Id, "text/plain"));
                string results = Path.Combine(artifactsDirectory, "results");
                if (Directory.Exists(results))
                {
                    VerifyDirectoryAncestors(results);
                    int reportCount = 0;
                    foreach (string path in Directory.EnumerateFiles(results, "*-full.json").Take(101))
                    {
                        if (++reportCount > 100) throw new LabException("resource_limit", "BenchmarkDotNet produced more than 100 reports.");
                        string artifactId = await RetainBenchmarkFileAsync(path, operation.Id, "application/json");
                        artifacts.Add(artifactId);
                        try
                        {
                            var artifact = Record<Artifact>(artifactId, "artifact");
                            string retained = await Artifacts.VerifiedPathAsync(artifact);
                            reports.Add(BenchmarkDotNetReportReader.Read(await File.ReadAllTextAsync(retained), artifactId));
                        }
                        catch (LabException error) { failure ??= error.Code; }
                    }
                }
                if (reports.Count == 0) failure ??= "benchmark_report_missing";
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                failure ??= error is LabException lab ? lab.Code : "benchmark_report_failed";
            }
            result = result with { State = failure is null ? "completed" : "failed", Failure = failure,
                ArtifactIds = artifacts.ToArray(), Reports = reports.ToArray() };
            Catalog.Put(result.Id, "benchmarkdotnet_result", result);
            Catalog.Transition(operation.Id, failure is null ? OperationState.Completed : OperationState.Failed, result.Id, failure);
            return true;
        });
    }

    private async Task<string> RetainBenchmarkFileAsync(string path, string operationId, string mediaType)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0 || new FileInfo(path).Length > 16_777_216)
            throw new LabException("resource_limit", "Benchmark report must be a regular file no larger than 16 MiB.");
        string incoming = Artifacts.CreateIncoming(operationId);
        File.Copy(path, incoming, overwrite: true);
        var artifact = await Artifacts.SealAsync(incoming, mediaType, "benchmarkdotnet/1", true, 16_777_216, CancellationToken.None);
        Upsert(artifact.Id, "artifact", artifact);
        Catalog.Pin(artifact.Id, "benchmarkdotnet");
        return artifact.Id;
    }

    private static async Task SaveBenchmarkOutputAsync(StreamReader reader, string path, CancellationToken cancellation)
    {
        await using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
        char[] buffer = new char[4096];
        int count;
        long total = 0;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
        {
            if ((total += count) > 4_194_304) throw new LabException("resource_limit", "BenchmarkDotNet output exceeds 4 MiB per stream.");
            await writer.WriteAsync(buffer.AsMemory(0, count), cancellation);
        }
    }
}
