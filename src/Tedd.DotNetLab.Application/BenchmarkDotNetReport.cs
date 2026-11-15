using System.Text.Json;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record BenchmarkDotNetStatistics(string Name, string DisplayInfo, string Parameters, int Samples, int Launches,
    double MeanNanoseconds, double MedianNanoseconds, double StandardDeviationNanoseconds, double StandardErrorNanoseconds,
    JsonElement ConfidenceInterval, double? AllocatedBytesPerOperation, long? Gen0Collections, long? Gen1Collections,
    long? Gen2Collections, long? MemoryTotalOperations);

public record BenchmarkDotNetReport(string ArtifactId, JsonElement HostEnvironment,
    IReadOnlyList<BenchmarkDotNetStatistics> Benchmarks);

public static class BenchmarkDotNetReportReader
{
    public static BenchmarkDotNetReport Read(string json, string artifactId)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
            var root = document.RootElement;
            var host = root.GetProperty("HostEnvironmentInfo");
            if (host.ValueKind != JsonValueKind.Object || !host.TryGetProperty("BenchmarkDotNetVersion", out _))
                throw Invalid();
            var rows = root.GetProperty("Benchmarks");
            if (rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is < 1 or > 1000) throw Invalid();
            var benchmarks = new List<BenchmarkDotNetStatistics>();
            foreach (var row in rows.EnumerateArray())
            {
                var stats = row.GetProperty("Statistics");
                if (stats.ValueKind != JsonValueKind.Object) throw Invalid();
                int count = stats.GetProperty("N").GetInt32();
                if (count < 1) throw Invalid();
                var measurements = row.GetProperty("Measurements");
                if (measurements.ValueKind != JsonValueKind.Array || measurements.GetArrayLength() is < 1 or > 100000)
                    throw Invalid();
                var launches = new HashSet<int>();
                int results = 0;
                foreach (var measurement in measurements.EnumerateArray())
                {
                    if (measurement.GetProperty("IterationMode").GetString() != "Workload" ||
                        measurement.GetProperty("IterationStage").GetString() != "Result") continue;
                    if (measurement.GetProperty("Operations").GetInt64() < 1) throw Invalid();
                    _ = Number(measurement, "Nanoseconds");
                    launches.Add(measurement.GetProperty("LaunchIndex").GetInt32());
                    results++;
                }
                if (results != count || launches.Count == 0) throw Invalid();
                var interval = stats.GetProperty("ConfidenceInterval");
                if (interval.ValueKind != JsonValueKind.Object) throw Invalid();
                bool hasMemory = row.TryGetProperty("Memory", out var memory) && memory.ValueKind == JsonValueKind.Object;
                long? totalOperations = hasMemory ? memory.GetProperty("TotalOperations").GetInt64() : null;
                if (totalOperations is <= 0) throw Invalid();
                benchmarks.Add(new(
                    row.GetProperty("FullName").GetString() ?? throw Invalid(),
                    row.GetProperty("DisplayInfo").GetString() ?? throw Invalid(),
                    row.GetProperty("Parameters").GetString() ?? "", count, launches.Count,
                    Number(stats, "Mean"), Number(stats, "Median"), Number(stats, "StandardDeviation"), Number(stats, "StandardError"),
                    interval.Clone(), hasMemory && memory.GetProperty("BytesAllocatedPerOperation").ValueKind == JsonValueKind.Number
                        ? Number(memory, "BytesAllocatedPerOperation") : null,
                    hasMemory ? Counter(memory, "Gen0Collections") : null,
                    hasMemory ? Counter(memory, "Gen1Collections") : null,
                    hasMemory ? Counter(memory, "Gen2Collections") : null, totalOperations));
            }
            return new(artifactId, host.Clone(), benchmarks);
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw Invalid();
        }
    }

    private static double Number(JsonElement value, string name)
    {
        double number = value.GetProperty(name).GetDouble();
        return double.IsFinite(number) && number >= 0 ? number : throw Invalid();
    }
    private static long Counter(JsonElement value, string name)
    {
        long number = value.GetProperty(name).GetInt64();
        return number >= 0 ? number : throw Invalid();
    }
    private static LabException Invalid() => new("invalid_benchmark_report",
        "BenchmarkDotNet full JSON must contain successful statistics and matching workload result measurements for every benchmark.");
}
