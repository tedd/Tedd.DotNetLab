using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Tedd.DotNetLab.Artifacts;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

namespace Tedd.DotNetLab.Application;

public record JitDisasmMethodSummary(
    int Index,
    string MethodSignature,
    string Tier,
    int CodeSizeBytes,
    int StackAllocBytes,
    int StackReferenceCount,
    int BoundsCheckCount,
    int AllocationHelperCount,
    int WriteBarrierCount,
    int SimdInstructionCount,
    int TextOffset,
    int TextLength);

public record JitDisasmResult(
    string Id,
    string MethodFilter,
    string ScenarioId,
    string Variant,
    string RuntimeVersion,
    string Architecture,
    bool TieredPgo,
    string ArtifactId,
    int MethodCount,
    IReadOnlyList<JitDisasmMethodSummary> Methods,
    DateTimeOffset CreatedAt,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? BuildId = null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? EntryAssemblyHash = null);

public static class JitDisasmParser
{
    private const string HeaderPrefix = "; Assembly listing for method ";
    private static readonly Regex SubRspRegex = new(@"\bsub\s+rsp,\s*(?:0x([0-9A-Fa-f]+)|([0-9]+))\b", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CodeBytesRegex = new(@";\s*Total bytes of code:?\s*([0-9]+)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex SimdRegRegex = new(@"\b(?:[xyz]mm(?:[0-9]|[12][0-9]|3[01]))\b", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static IReadOnlyList<JitDisasmMethodSummary> Parse(string text)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var starts = new List<int>();
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int idx = text.IndexOf(HeaderPrefix, searchFrom, StringComparison.Ordinal);
            if (idx < 0) break;
            if (idx == 0 || text[idx - 1] is '\n' or '\r') starts.Add(idx);
            searchFrom = idx + HeaderPrefix.Length;
            if (starts.Count >= 512) break;
        }

        var results = new List<JitDisasmMethodSummary>(starts.Count);
        for (int i = 0; i < starts.Count; i++)
        {
            int blockStart = starts[i];
            int blockEnd = i + 1 < starts.Count ? starts[i + 1] : text.Length;
            while (blockEnd > blockStart && text[blockEnd - 1] is '\r' or '\n' or ' ') blockEnd--;
            string block = text[blockStart..blockEnd];
            results.Add(ParseBlock(i, block, blockStart));
        }
        return results;
    }

    private static JitDisasmMethodSummary ParseBlock(int index, string block, int offset)
    {
        int firstLineEnd = block.IndexOfAny(['\r', '\n']);
        string headerLine = (firstLineEnd >= 0 ? block[..firstLineEnd] : block)[HeaderPrefix.Length..].Trim();
        string signature = headerLine;
        string tier = "Unknown";

        int lastOpenParen = headerLine.LastIndexOf(" (", StringComparison.Ordinal);
        if (lastOpenParen > 0 && headerLine.EndsWith(')'))
        {
            string candidateTier = headerLine[(lastOpenParen + 2)..^1].Trim();
            if (candidateTier.StartsWith("Tier", StringComparison.OrdinalIgnoreCase) ||
                candidateTier.Contains("FullOpts", StringComparison.OrdinalIgnoreCase) ||
                candidateTier.Contains("MinOpts", StringComparison.OrdinalIgnoreCase) ||
                candidateTier.Contains("OSR", StringComparison.OrdinalIgnoreCase) ||
                candidateTier.Contains("Instrumented", StringComparison.OrdinalIgnoreCase))
            {
                tier = candidateTier;
                signature = headerLine[..lastOpenParen].Trim();
            }
        }

        int codeBytes = 0;
        int stackAllocBytes = 0;
        int stackRefs = 0;
        int boundsChecks = 0;
        int allocHelpers = 0;
        int writeBarriers = 0;
        int simdInstructions = 0;

        foreach (string rawLine in block.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith(';'))
            {
                if (tier == "Unknown")
                {
                    if (line.Contains("Tier-1", StringComparison.OrdinalIgnoreCase)) tier = "Tier-1";
                    else if (line.Contains("Tier-0", StringComparison.OrdinalIgnoreCase)) tier = "Tier-0";
                    else if (line.Contains("FullOpts", StringComparison.OrdinalIgnoreCase)) tier = "FullOpts";
                    else if (line.Contains("MinOpts", StringComparison.OrdinalIgnoreCase)) tier = "MinOpts";
                }
                var bytesMatch = CodeBytesRegex.Match(line);
                if (bytesMatch.Success && int.TryParse(bytesMatch.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out int cb))
                    codeBytes = cb;
                continue;
            }

            if (line.Length == 0 || line.EndsWith(':')) continue;

            var subRsp = SubRspRegex.Match(line);
            if (subRsp.Success)
            {
                int imm = subRsp.Groups[1].Success
                    ? int.Parse(subRsp.Groups[1].Value, NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                    : int.Parse(subRsp.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture);
                if (imm > stackAllocBytes) stackAllocBytes = imm;
            }

            if (line.Contains("[rsp", StringComparison.OrdinalIgnoreCase) || line.Contains("[rbp", StringComparison.OrdinalIgnoreCase))
                stackRefs++;
            if (line.Contains("CORINFO_HELP_RNGCHKFAIL", StringComparison.Ordinal))
                boundsChecks++;
            if (line.Contains("CORINFO_HELP_NEW", StringComparison.Ordinal) || line.Contains("CORINFO_HELP_BOX", StringComparison.Ordinal))
                allocHelpers++;
            if (line.Contains("CORINFO_HELP_ASSIGN_REF", StringComparison.Ordinal) || line.Contains("CORINFO_HELP_CHECKED_ASSIGN_REF", StringComparison.Ordinal))
                writeBarriers++;
            if (SimdRegRegex.IsMatch(line))
                simdInstructions++;
        }

        return new JitDisasmMethodSummary(
            index, signature, tier, codeBytes, stackAllocBytes,
            stackRefs, boundsChecks, allocHelpers, writeBarriers, simdInstructions,
            offset, block.Length);
    }
}

public sealed partial class LabService
{
    public Operation StartJitDisasm(string methodFilter, string scenarioId, string variant, string grantId, string idempotencyKey,
        string? buildId = null, string? executablePath = null, int operations = 1000, int warmupBatches = 5,
        bool tieredPgo = true, int maximumDurationSeconds = 60)
    {
        if (!RuntimeSupport.ControllerSupported)
            throw new LabException("capability_unsupported", "JIT disassembly requires a compatible Windows/Linux x64 controller runtime.");
        ValidateJitMethodFilter(methodFilter);
        if (string.IsNullOrWhiteSpace(scenarioId) || scenarioId.Length > 256 ||
            string.IsNullOrWhiteSpace(variant) || variant.Length > 256 ||
            string.IsNullOrWhiteSpace(idempotencyKey) || idempotencyKey.Length > 128)
            throw new LabException("invalid_request", "Invalid scenario, variant, or idempotency key.");
        if (operations is < 1 or > 10_000_000 || warmupBatches is < 0 or > 1000 ||
            maximumDurationSeconds < 1 || maximumDurationSeconds > Math.Min(300, Limits.MaximumDurationSeconds))
            throw new LabException("invalid_plan", "JIT disassembly budget or duration is outside supported bounds.");
        if ((buildId is not null) == (executablePath is not null))
            throw new LabException("invalid_request", "Provide exactly one of build_id (registered NormalBuild) or executable_path (scratch DLL).");

        var grant = Record<Grant>(grantId, "grant");
        NormalBuild? build = null;
        string? verifiedScratchDll = null;
        TargetIdentity leaseTarget;

        if (buildId is not null)
        {
            build = Record<NormalBuild>(buildId, "normal_build");
            AuthorizeBuildDisasmGrant(grant, build);
            leaseTarget = new("jit-disasm-" + build.Id, 0, 0, "jit-disasm-" + build.Id, build.RuntimeVersion, "X64");
        }
        else
        {
            verifiedScratchDll = AuthorizeScratchExecutableDisasm(grant, executablePath!);
            leaseTarget = new("jit-disasm-" + grant.TargetId, 0, 0, "jit-disasm-" + grant.TargetId, RuntimeSupport.Current, "X64");
        }

        string requestHash = Wire.Hash(new
        {
            methodFilter, scenarioId, variant, grantId, buildId, executablePath = verifiedScratchDll,
            operations, warmupBatches, tieredPgo, maximumDurationSeconds
        });

        return Start("jit_disasm", leaseTarget, idempotencyKey, requestHash, async (op, cancellation) =>
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            deadline.CancelAfter(TimeSpan.FromSeconds(maximumDurationSeconds));
            using var measurementPermit = await activityScheduler.EnterMeasurementAsync(deadline.Token);

            if (build is not null) AuthorizeBuildDisasmGrant(grant, build);
            else verifiedScratchDll = AuthorizeScratchExecutableDisasm(grant, verifiedScratchDll!);
            Catalog.UseGrant(grant);

            string? stagedDir = null;
            string workDir = Path.Combine(Catalog.Root, "incoming", op.Id, "jit-work");
            VerifyDirectoryAncestors(Path.Combine(Catalog.Root, "incoming"));
            Directory.CreateDirectory(workDir);
            VerifyDirectoryAncestors(workDir);

            try
            {
                string entryDllPath;
                string workingDir;
                if (build is not null)
                {
                    stagedDir = await StageBuildAsync(build, Wire.Id("normal-session"), deadline.Token);
                    workingDir = stagedDir;
                    entryDllPath = Path.Combine(stagedDir, build.EntryAssembly);
                }
                else
                {
                    entryDllPath = verifiedScratchDll!;
                    workingDir = Path.GetDirectoryName(entryDllPath)!;
                }

                string configPath = Path.ChangeExtension(entryDllPath, ".runtimeconfig.json");
                var (runtimeVersion, rollForward) = ReadRuntimeConfig(configPath);
                var start = RuntimeSupport.StartInfo(runtimeVersion, rollForward, out string resolvedRuntime);
                start.WorkingDirectory = workingDir;
                start.RedirectStandardInput = true;
                start.RedirectStandardOutput = true;
                start.RedirectStandardError = true;
                start.ArgumentList.Add(entryDllPath);
                start.ArgumentList.Add("--harness");
                start.ArgumentList.Add(Catalog.Root);
                start.Environment.Clear();
                foreach (string envKey in new[] { "SystemRoot", "WINDIR", "ComSpec" })
                    if (Environment.GetEnvironmentVariable(envKey) is { } val) start.Environment[envKey] = val;

                string tmpDir = Path.Combine(workDir, "tmp");
                Directory.CreateDirectory(tmpDir);
                string disasmFile = Path.Combine(workDir, "jit.dasm");
                start.Environment["TEMP"] = tmpDir;
                start.Environment["TMP"] = tmpDir;
                if (OperatingSystem.IsLinux())
                {
                    string ipc = Path.GetFullPath(Path.GetTempPath());
                    if (Encoding.UTF8.GetByteCount(ipc) <= 48) start.Environment["TMPDIR"] = ipc;
                }
                start.Environment["DOTNET_EnableDiagnostics"] = "0";
                start.Environment["CORECLR_ENABLE_PROFILING"] = "0";
                start.Environment["DOTNET_ENABLE_PROFILING"] = "0";
                start.Environment["DOTNET_MULTILEVEL_LOOKUP"] = "0";
                start.Environment["DOTNET_JitDisasm"] = methodFilter;
                start.Environment["DOTNET_JitDisasmSummary"] = "1";
                start.Environment["DOTNET_JitStdOutFile"] = disasmFile;
                start.Environment["DOTNET_TieredCompilation"] = "1";
                start.Environment["DOTNET_TieredPGO"] = tieredPgo ? "1" : "0";
                start.Environment["DOTNET_TC_QuickJitForLoops"] = "1";
                start.Environment["DOTNET_TC_CallCountingDelayMs"] = "0";
                start.Environment["DOTNETLAB_JIT_SETTLE_MS"] = "1";

                using var process = new Process { StartInfo = start };
                using var outputCancel = new CancellationTokenSource();
                var fallbackStdout = new StringBuilder();
                Task? stdoutDrain = null, stderrDrain = null;
                bool started = false;
                HarnessHandshake? handshake = null;

                try
                {
                    if (!process.Start()) throw new LabException("process_start_failed", "Owned JIT disassembly process could not start.");
                    started = true;
                    stderrDrain = DrainOutputAsync(process.StandardError, outputCancel.Token);
                    using var startup = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    startup.CancelAfter(TimeSpan.FromSeconds(15));

                    var ready = await ReadTargetReadinessAsync(process.StandardOutput, fallbackStdout, startup.Token);
                    long ticks = ProcessIdentity.StartTimeUtcTicks(process);
                    string expectedId = "target-" + Wire.Hash(new { pid = process.Id, start = ticks })[..24];
                    if (ready.ProcessId != process.Id || ready.StartTimeUtcTicks != ticks || ready.Id != expectedId)
                        throw new LabException("target_identity_changed", "Owned JIT disassembly process readiness differs from kernel process identity.");

                    stdoutDrain = DrainCaptureAsync(process.StandardOutput, fallbackStdout, outputCancel.Token);
                    handshake = await harness.HandshakeAsync(ready, startup.Token);
                    var scenario = handshake.Scenarios.SingleOrDefault(s => s.Id == scenarioId)
                        ?? throw new LabException("scenario_not_found", "Target does not register the requested scenario.");
                    if (!scenario.Variants.Contains(variant))
                        throw new LabException("invalid_plan", "Target scenario does not register the requested variant.");

                    var childGrant = new Grant(grant.Id + "-disasm", Catalog.Root, ready.Id, ready.StartTimeUtcTicks,
                        Permission.ExecuteRegisteredScenario, grant.ExpiresAt, 1);
                    var runReq = new RunRequest(op.Id, Wire.Id("disasm-run"), op.Id + "-block-0", scenario.Id, variant,
                        operations, warmupBatches, true, !scenario.IsAsync, 0, ready, childGrant,
                        DateTimeOffset.UtcNow.AddSeconds(maximumDurationSeconds));

                    var measurement = await ExecuteMeasuredAsync(runReq, deadline.Token, measurementPermit);
                    if (!measurement.Correct || !measurement.Quiescent)
                        throw new LabException("scenario_failed", measurement.Failure ?? "Scenario validation failed during JIT disassembly.");

                    await process.StandardInput.WriteLineAsync("shutdown");
                    process.StandardInput.Close();
                    await process.WaitForExitAsync(deadline.Token).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                    await Task.WhenAll(stdoutDrain, stderrDrain).WaitAsync(TimeSpan.FromSeconds(5), deadline.Token);
                }
                finally
                {
                    try { if (started && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { }
                    outputCancel.Cancel();
                    foreach (var t in new[] { stdoutDrain, stderrDrain })
                        if (t is not null) { try { await t; } catch (Exception e) when (e is OperationCanceledException or IOException or LabException) { } }
                }

                string disasmText = "";
                if (File.Exists(disasmFile))
                {
                    var info = new FileInfo(disasmFile);
                    if (info.Length > 16_777_216)
                        throw new LabException("resource_limit", "JIT disassembly output exceeds the 16 MiB budget; narrow method_filter.");
                    disasmText = await File.ReadAllTextAsync(disasmFile, Encoding.UTF8, deadline.Token);
                }
                if (string.IsNullOrWhiteSpace(disasmText) && fallbackStdout.Length > 0)
                    disasmText = fallbackStdout.ToString();

                string incoming = Artifacts.CreateIncoming(op.Id);
                await File.WriteAllTextAsync(incoming, disasmText, new UTF8Encoding(false), deadline.Token);
                var artifact = await Artifacts.SealAsync(incoming, "text/plain", "coreclr-jit-disasm/1", true, 16_777_216, deadline.Token);
                Upsert(artifact.Id, "artifact", artifact);
                Catalog.Pin(artifact.Id, "jit_disasm");

                var methods = JitDisasmParser.Parse(disasmText);
                var result = new JitDisasmResult(
                    Wire.Id("jit-disasm"), methodFilter, scenarioId, variant,
                    resolvedRuntime, "X64", tieredPgo, artifact.Id,
                    methods.Count, methods, DateTimeOffset.UtcNow,
                    BuildId: build?.Id,
                    EntryAssemblyHash: handshake?.Runtime?.EntryAssemblyHash);
                Catalog.Put(result.Id, "jit_disasm_result", result);
                Catalog.Transition(op.Id, OperationState.Completed, result.Id);
                return true;
            }
            finally
            {
                if (stagedDir is not null) await RemoveOwnedLaunchDirectoryAsync(stagedDir);
                if (Directory.Exists(workDir)) await OwnedDirectoryCleanup.DeleteAsync(Catalog.Root, workDir);
            }
        });
    }

    public async Task<object> QueryJitDisasmAsync(string resultId, string? methodSubstring = null, string? tierFilter = null,
        bool includeAsm = true, int maxAsmChars = 32768)
    {
        if (maxAsmChars is < 256 or > 49152)
            throw new LabException("resource_limit", "max_asm_chars must be within [256, 49152].");
        var result = Record<JitDisasmResult>(resultId, "jit_disasm_result");
        var artifact = Record<Artifact>(result.ArtifactId, "artifact");
        string fullText = includeAsm
            ? await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(artifact), Encoding.UTF8)
            : "";

        var filtered = result.Methods.Where(m =>
            (string.IsNullOrEmpty(methodSubstring) || m.MethodSignature.Contains(methodSubstring, StringComparison.OrdinalIgnoreCase)) &&
            (string.IsNullOrEmpty(tierFilter) || m.Tier.Contains(tierFilter, StringComparison.OrdinalIgnoreCase))).ToArray();

        int remainingChars = maxAsmChars;
        var items = new List<object>(filtered.Length);
        foreach (var m in filtered)
        {
            string? asm = null;
            bool truncated = false;
            if (includeAsm && m.TextOffset >= 0 && m.TextOffset + m.TextLength <= fullText.Length)
            {
                if (remainingChars <= 0)
                {
                    truncated = true;
                }
                else
                {
                    int take = Math.Min(m.TextLength, remainingChars);
                    asm = fullText.Substring(m.TextOffset, take);
                    truncated = take < m.TextLength;
                    remainingChars -= take;
                }
            }
            items.Add(new
            {
                summary = m,
                asm,
                asm_truncated = truncated
            });
        }

        return new
        {
            result_id = result.Id,
            method_filter = result.MethodFilter,
            scenario_id = result.ScenarioId,
            variant = result.Variant,
            runtime_version = result.RuntimeVersion,
            tiered_pgo = result.TieredPgo,
            artifact_id = result.ArtifactId,
            total_methods = result.MethodCount,
            matched_methods = filtered.Length,
            methods = items
        };
    }

    private static void ValidateJitMethodFilter(string filter)
    {
        if (string.IsNullOrWhiteSpace(filter) || filter.Length > 256 ||
            filter.Any(c => c < 0x20 || c > 0x7E || c == ';'))
        {
            throw new LabException("invalid_request", "method_filter must be 1..256 printable ASCII characters without semicolons.");
        }
    }

    private void AuthorizeBuildDisasmGrant(Grant grant, NormalBuild build)
    {
        if (grant.Workspace != Catalog.Root || grant.Permission != Permission.LaunchRegisteredBuild ||
            grant.ExpiresAt <= DateTimeOffset.UtcNow || grant.MaximumOperations <= 0)
            throw new LabException("permission_denied", "Build launch grant is expired, exhausted, or invalid.");

        if (grant.TargetId == build.Id && grant.ContentHash == build.ContentHash) return;

        if (grant.TargetId.StartsWith("confirm-plan-", StringComparison.Ordinal))
        {
            var plan = Record<ConfirmationPlan>(grant.TargetId, "confirmation_plan");
            if (grant.ContentHash == plan.ContentHash && (plan.BaselineBuildId == build.Id || plan.CandidateBuildId == build.Id))
                return;
        }
        throw new LabException("permission_denied", "Grant does not authorize launching this registered build.");
    }

    private string AuthorizeScratchExecutableDisasm(Grant grant, string executablePath)
    {
        if (!IsScratchGrant(grant) || grant.Workspace != Catalog.Root ||
            grant.Permission != Permission.ExecuteRegisteredScenario ||
            grant.ExpiresAt <= DateTimeOffset.UtcNow || grant.MaximumOperations <= 0 ||
            string.IsNullOrEmpty(grant.ContentHash))
        {
            throw new LabException("permission_denied", "JIT disassembly on an executable_path requires an active scratch-directory execute grant.");
        }
        string scratchPrefix = NormalizeScratchPrefix(grant.ContentHash);
        if (!IsPathStrictlyUnderScratch(executablePath, scratchPrefix) ||
            !executablePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(executablePath))
        {
            throw new LabException("permission_denied", "executable_path must be an existing .dll strictly inside the granted scratch directory.");
        }
        string full = Path.GetFullPath(executablePath);
        string config = Path.ChangeExtension(full, ".runtimeconfig.json");
        string deps = Path.ChangeExtension(full, ".deps.json");
        if (!File.Exists(config) || !File.Exists(deps) ||
            !IsPathStrictlyUnderScratch(config, scratchPrefix) || !IsPathStrictlyUnderScratch(deps, scratchPrefix))
        {
            throw new LabException("invalid_build", "Scratch executable requires adjacent .runtimeconfig.json and .deps.json files.");
        }
        return full;
    }

    private static (string RuntimeVersion, string RollForward) ReadRuntimeConfig(string configPath)
    {
        if (new FileInfo(configPath).Length > 1048576)
            throw new LabException("resource_limit", "Runtime configuration exceeds 1 MiB.");
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(configPath), new JsonDocumentOptions { MaxDepth = 32 });
            var options = doc.RootElement.GetProperty("runtimeOptions");
            string rollForward = options.TryGetProperty("rollForward", out var rf) ? rf.GetString() ?? "Minor" : "Minor";
            if (options.TryGetProperty("framework", out var fw) && fw.TryGetProperty("version", out var v) && v.GetString() is { } ver)
                return (ver, rollForward);
            if (options.TryGetProperty("frameworks", out var fws))
            {
                foreach (var item in fws.EnumerateArray())
                {
                    if (item.TryGetProperty("name", out var name) && name.GetString() == "Microsoft.NETCore.App" &&
                        item.TryGetProperty("version", out var iv) && iv.GetString() is { } iver)
                        return (iver, rollForward);
                }
            }
            return (RuntimeSupport.Current, rollForward);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException)
        {
            throw new LabException("invalid_build", "Target runtimeconfig.json is malformed.");
        }
    }

    private static async Task<TargetIdentity> ReadTargetReadinessAsync(StreamReader reader, StringBuilder preReadinessLines, CancellationToken cancellation)
    {
        while (true)
        {
            string line = await BoundedLineAsync(reader, cancellation);
            if (line.StartsWith("{\"id\":\"target-", StringComparison.Ordinal))
                return Wire.Deserialize<TargetIdentity>(line);
            if (preReadinessLines.Length + line.Length < 4 * 1024 * 1024)
            {
                preReadinessLines.AppendLine(line);
            }
        }
    }

    private static async Task DrainCaptureAsync(StreamReader reader, StringBuilder captured, CancellationToken cancellation)
    {
        char[] buffer = new char[4096];
        long total = 0;
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellation)) > 0)
        {
            total += count;
            if (total > 16_777_216) throw new LabException("resource_limit", "Owned process stdout exceeds 16 MiB.");
            if (captured.Length + count <= 16_777_216) captured.Append(buffer, 0, count);
        }
    }
}
