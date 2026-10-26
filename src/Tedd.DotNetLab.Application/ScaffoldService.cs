using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record ScaffoldResult(
    string SchemaVersion,
    string OutputDirectory,
    string ProjectPath,
    string ProgramPath,
    string ScenarioId,
    string ExpectedOutputDllPath,
    string BuildCommand);

public static class ScaffoldService
{
    public static ScaffoldResult Create(string outputDirectory, string scenarioId = "scratch-bench", bool overwrite = false)
    {
        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            throw new LabException("invalid_path", "Scaffold output directory must be a fully qualified local path.");
        if (!Regex.IsMatch(scenarioId, @"\A[a-z0-9][a-z0-9_-]{0,63}\z"))
            throw new LabException("invalid_request", "Scenario ID must match [a-z0-9][a-z0-9_-]{0,63}.");

        string fullDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDirectory));
        string? root = Path.GetPathRoot(fullDir);
        if (string.IsNullOrEmpty(root) ||
            string.Equals(fullDir, Path.TrimEndingDirectorySeparator(root),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            Directory.GetParent(fullDir) is null)
        {
            throw new LabException("invalid_path", "Scaffold requires a non-root local directory.");
        }
        if (fullDir.StartsWith(@"\\", StringComparison.Ordinal) ||
            new DriveInfo(root).DriveType == DriveType.Network)
        {
            throw new LabException("invalid_path", "Scaffold directory must reside on a local filesystem.");
        }

        for (var dir = new DirectoryInfo(fullDir); dir is not null; dir = dir.Parent)
        {
            if (dir.Exists && (dir.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new LabException("invalid_path", "Scaffold directory cannot traverse reparse points.");
        }

        string hostDir = Path.GetFullPath(AppContext.BaseDirectory);
        string contractsDll = Path.Combine(hostDir, "Tedd.DotNetLab.Contracts.dll");
        string harnessDll = Path.Combine(hostDir, "Tedd.DotNetLab.Harness.dll");
        string transportDll = Path.Combine(hostDir, "Tedd.DotNetLab.Transport.Local.dll");
        if (!File.Exists(contractsDll) || !File.Exists(harnessDll) || !File.Exists(transportDll))
            throw new LabException("capability_unsupported", "Host binary directory is missing cooperative harness assemblies.");

        Directory.CreateDirectory(fullDir);
        string csprojPath = Path.Combine(fullDir, "ScratchBench.csproj");
        string programPath = Path.Combine(fullDir, "Program.cs");
        if (!overwrite && (File.Exists(csprojPath) || File.Exists(programPath)))
            throw new LabException("invalid_path", "Scaffold target already contains ScratchBench.csproj or Program.cs; pass --force to overwrite.");

        string escapedContracts = SecurityElement.Escape(contractsDll)!;
        string escapedHarness = SecurityElement.Escape(harnessDll)!;
        string escapedTransport = SecurityElement.Escape(transportDll)!;

        string csproj = $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
                <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <RollForward>Major</RollForward>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <Optimize>true</Optimize>
                <TieredCompilation>true</TieredCompilation>
                <TieredPGO>true</TieredPGO>
                <BaseIntermediateOutputPath>obj\</BaseIntermediateOutputPath>
                <BaseOutputPath>bin\</BaseOutputPath>
              </PropertyGroup>
              <ItemGroup>
                <Reference Include="Tedd.DotNetLab.Contracts">
                  <HintPath>{escapedContracts}</HintPath>
                  <Private>true</Private>
                </Reference>
                <Reference Include="Tedd.DotNetLab.Harness">
                  <HintPath>{escapedHarness}</HintPath>
                  <Private>true</Private>
                </Reference>
                <Reference Include="Tedd.DotNetLab.Transport.Local">
                  <HintPath>{escapedTransport}</HintPath>
                  <Private>true</Private>
                </Reference>
              </ItemGroup>
            </Project>
            """;

        string program = $$"""
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using Tedd.DotNetLab.Contracts;
            using Tedd.DotNetLab.Harness;

            var registry = new ScenarioRegistry();
            registry.Register(new ScratchScenario());

            if (args.Length == 2 && args[0] == "--harness")
            {
                var owned = new HarnessHost(registry, args[1]);
                await owned.RunOwnedAsync();
                return;
            }

            string workspace = args.Length >= 1 ? args[0] : throw new ArgumentException("Usage: ScratchBench <workspace> | ScratchBench --harness <workspace>");
            using var cts = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
            var host = new HarnessHost(registry, workspace);
            var listenTask = host.ListenAsync(cts.Token);
            Console.WriteLine(Wire.Serialize(new
            {
                pid = host.Target.ProcessId,
                target_id = host.Target.Id,
                workspace = Path.GetFullPath(workspace)
            }));
            Console.Out.Flush();
            try { await Console.In.ReadLineAsync(cts.Token); }
            catch (OperationCanceledException) { }
            cts.Cancel();
            await listenTask;

            public sealed class ScratchScenario : ISynchronousScenario
            {
                private readonly int[] data;
                private readonly long expectedSum;
                private string activeVariant = "baseline";
                private long lastSum;

                public ScratchScenario()
                {
                    data = new int[1024];
                    long sum = 0;
                    for (int i = 0; i < data.Length; i++)
                    {
                        data[i] = (i * 31 + 17) & 0xFFFF;
                        sum += data[i];
                    }
                    expectedSum = sum;
                }

                public ScenarioDescriptor Describe() => new(
                    "{{scenarioId}}",
                    "1",
                    "default-1024-int32",
                    ResetLevel.LogicalState,
                    ThreadAffinity.Any,
                    false,
                    "One full pass over the 1024-element input buffer.",
                    ["baseline", "candidate"]);

                public void Prepare(ScenarioContext context) => activeVariant = context.Variant;

                public void Reset(ResetContext context) => lastSum = 0;

                public long ExecuteBatch(int operationCount)
                {
                    long last = 0;
                    if (activeVariant == "baseline")
                    {
                        for (int op = 0; op < operationCount; op++)
                            last = ExecuteBaseline(data);
                    }
                    else
                    {
                        for (int op = 0; op < operationCount; op++)
                            last = ExecuteCandidate(data);
                    }
                    lastSum = last;
                    return operationCount;
                }

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static long ExecuteBaseline(int[] source)
                {
                    long total = 0;
                    for (int i = 0; i < source.Length; i++)
                        total += source[i];
                    return total;
                }

                [MethodImpl(MethodImplOptions.NoInlining)]
                public static long ExecuteCandidate(ReadOnlySpan<int> source)
                {
                    ref int cur = ref MemoryMarshal.GetReference(source);
                    ref int end = ref Unsafe.Add(ref cur, source.Length);
                    long total = 0;
                    while (Unsafe.IsAddressLessThan(ref cur, ref end))
                    {
                        total += cur;
                        cur = ref Unsafe.Add(ref cur, 1);
                    }
                    return total;
                }

                public ValidationResult Validate() =>
                    lastSum == expectedSum
                        ? new ValidationResult(true)
                        : new ValidationResult(false, $"Expected {expectedSum}, got {lastSum}.");

                public void Cleanup() { }
            }
            """;

        var utf8NoBom = new UTF8Encoding(false);
        File.WriteAllText(csprojPath, csproj + Environment.NewLine, utf8NoBom);
        File.WriteAllText(programPath, program + Environment.NewLine, utf8NoBom);

        string outputDll = Path.Combine(fullDir, "bin", "Release", "net10.0", "ScratchBench.dll");
        return new ScaffoldResult(
            Wire.Version,
            fullDir,
            csprojPath,
            programPath,
            scenarioId,
            outputDll,
            $"dotnet build \"{csprojPath}\" -c Release");
    }
}
