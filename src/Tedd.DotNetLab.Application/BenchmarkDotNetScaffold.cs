using System.Text;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

internal static class BenchmarkDotNetScaffold
{
    public static ScaffoldResult Create(string directory, string scenarioId, bool overwrite)
    {
        string project = Path.Combine(directory, "ScratchBench.csproj");
        string program = Path.Combine(directory, "Program.cs");
        if (!overwrite && (File.Exists(project) || File.Exists(program)))
            throw new LabException("invalid_path", "Scaffold target already contains ScratchBench.csproj or Program.cs; pass --force to overwrite.");

        const string projectText = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <ImportDirectoryBuildProps>false</ImportDirectoryBuildProps>
                <ImportDirectoryBuildTargets>false</ImportDirectoryBuildTargets>
                <ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <ImplicitUsings>enable</ImplicitUsings>
                <Nullable>enable</Nullable>
                <Optimize>true</Optimize>
                <RestorePackagesWithLockFile>true</RestorePackagesWithLockFile>
              </PropertyGroup>
              <ItemGroup>
                <PackageReference Include="BenchmarkDotNet" Version="0.15.8" />
              </ItemGroup>
            </Project>
            """;
        const string programText = """
            using BenchmarkDotNet.Attributes;
            using BenchmarkDotNet.Configs;
            using BenchmarkDotNet.Exporters;
            using BenchmarkDotNet.Exporters.Csv;
            using BenchmarkDotNet.Exporters.Json;
            using BenchmarkDotNet.Running;

            var config = DefaultConfig.Instance
                .WithArtifactsPath(Path.Combine(AppContext.BaseDirectory, "BenchmarkDotNet.Artifacts"))
                .WithOptions(ConfigOptions.KeepBenchmarkFiles | ConfigOptions.StopOnFirstError);
            if (!args.Contains("--exporters") && !args.Contains("-e"))
                config = config.AddExporter(JsonExporter.Full, CsvMeasurementsExporter.Default);
            var summaries = BenchmarkSwitcher.FromAssembly(typeof(ScratchBenchmarks).Assembly).Run(args, config).ToArray();
            return summaries.Length > 0 && summaries.All(s => !s.HasCriticalValidationErrors &&
                s.Reports.Length > 0 && s.Reports.All(r => r.Success && r.ResultStatistics is not null)) ? 0 : 1;

            [MemoryDiagnoser]
            public class ScratchBenchmarks
            {
                private int[] data = null!;

                [Params(1024)]
                public int Length { get; set; }

                [GlobalSetup]
                public void Setup()
                {
                    data = Enumerable.Range(0, Length).Select(i => (i * 31 + 17) & 0xFFFF).ToArray();
                    long expected = data.Aggregate(0L, (sum, value) => sum + value);
                    if (Baseline() != expected || Candidate() != expected)
                        throw new InvalidOperationException("Benchmark correctness check failed.");
                }

                [Benchmark(Baseline = true)]
                public long Baseline()
                {
                    long sum = 0;
                    for (int i = 0; i < data.Length; i++) sum += data[i];
                    return sum;
                }

                [Benchmark]
                public long Candidate()
                {
                    ReadOnlySpan<int> values = data;
                    long sum = 0;
                    foreach (int value in values) sum += value;
                    return sum;
                }
            }
            """;
        Directory.CreateDirectory(directory);
        var encoding = new UTF8Encoding(false);
        File.WriteAllText(project, projectText + Environment.NewLine, encoding);
        File.WriteAllText(program, programText + Environment.NewLine, encoding);
        return new(Wire.Version, directory, project, program, scenarioId,
            Path.Combine(directory, "bin", "Release", "net10.0", "ScratchBench.dll"),
            $"dotnet build \"{project}\" -c Release", "benchmarkdotnet",
            $"dotnet run --project \"{project}\" -c Release -- --filter \"*\"");
    }
}
