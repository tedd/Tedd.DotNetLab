using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

bool ownedHarness = args is ["--harness", _];
if (ownedHarness) args = args[1..];
if (args is ["--source-proof", string assembly, string source])
{
    Console.WriteLine(Wire.Serialize(SourceProof.Inspect(assembly, source)));
    return;
}
if (args is ["--verify"])
{
    Console.WriteLine(Wire.Serialize(new { checks = await Oracle.Verify(), correct = true }));
    return;
}
if (args.Length != 1) throw new ArgumentException("Supply the isolated catalog directory or --verify.");
var registry = new ScenarioRegistry();
foreach (string name in Workload.Names) registry.Register(new Workload(name));
registry.Register(new CachedWorkload());
using var stop = new CancellationTokenSource();
var host = new HarnessHost(registry, args[0]);
if (ownedHarness)
{
    await host.RunOwnedAsync();
    return;
}
var listener = host.ListenAsync(stop.Token);
Console.WriteLine(Wire.Serialize(new { pid = Environment.ProcessId, target = host.Target }));
Console.Out.Flush();
_ = Task.Run(async () => { await Console.In.ReadLineAsync(); stop.Cancel(); });
try { await listener; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }

public sealed class Workload(string name) : ISynchronousScenario
{
    public static readonly string[] Names = ["sum-squares", "csv", "membership", "byte-scan", "join", "deduplicate", "prefix", "no-op", "incorrect", "regression"];
    private readonly int[] values = Enumerable.Range(0, 384).Select(x => x % 71 - 35).ToArray();
    private readonly byte[] bytes = Enumerable.Range(0, 4097).Select(x => (byte)(x % 17)).ToArray();
    private readonly string[] queries = Enumerable.Range(0, 384).Select(x => "key-" + (x % 499)).ToArray();
    private readonly string[] allowed = Enumerable.Range(0, 256).Select(x => "key-" + x).ToArray();
    private readonly string[] fields = Enumerable.Range(0, 192).Select(x => "field-" + x).ToArray();
    private readonly string csv = string.Join(",", Enumerable.Range(-128, 256));
    private string variant = "baseline";
    private long checksum, expected, completed;
    private CancellationToken cancellation;
    public ScenarioDescriptor Describe() => new(name, "1", Wire.Hash(new { name, fixture = "client-eval-v1", n = 384 }),
        ResetLevel.LogicalState, ThreadAffinity.Any, false, "One complete transformation with consumed output", ["baseline", "candidate"]);
    public void Prepare(ScenarioContext context)
    {
        variant = context.Variant; cancellation = context.Cancellation;
        expected = Invoke(false);
    }
    public void Reset(ResetContext context) { checksum = completed = 0; }
    private long Invoke(bool candidate) => name switch
    {
        "sum-squares" => candidate ? Candidate.SumSquares(values) : Baseline.SumSquares(values),
        "csv" => candidate ? Candidate.Csv(csv) : Baseline.Csv(csv),
        "membership" => candidate ? Candidate.Membership(queries, allowed) : Baseline.Membership(queries, allowed),
        "byte-scan" => candidate ? Candidate.CountByte(bytes, 3) : Baseline.CountByte(bytes, 3),
        "join" => (candidate ? Candidate.Join(fields) : Baseline.Join(fields)).Length,
        "deduplicate" => (candidate ? Candidate.Deduplicate(values) : Baseline.Deduplicate(values)).Sum(x => (long)x),
        "prefix" => (candidate ? Candidate.Prefix(values) : Baseline.Prefix(values)).Sum(),
        "incorrect" => candidate ? -1 : 42,
        "regression" => candidate ? Slow() : Baseline.SumSquares(values),
        _ => Baseline.SumSquares(values)
    };
    private long Slow() { Thread.SpinWait(10000); return Baseline.SumSquares(values); }
    public long ExecuteBatch(int operationCount)
    {
        for (int i = 0; i < operationCount; i++)
        { cancellation.ThrowIfCancellationRequested(); checksum = unchecked(checksum + Invoke(variant == "candidate")); completed++; }
        return operationCount;
    }
    public ValidationResult Validate() => new(checksum == unchecked(completed * expected), "Client transformation checksum differs.");
    public void Cleanup() { }
}
public sealed class CachedWorkload : IAsynchronousScenario
{
    private readonly int[] cached = Enumerable.Range(-32, 64).ToArray();
    private string variant = "baseline";
    private long sum, completed;
    public ScenarioDescriptor Describe() => new("async-cache", "1", Wire.Hash(new { n = 64, fixture = "client-eval-v1" }),
        ResetLevel.LogicalState, ThreadAffinity.Any, true, "One fully awaited cached sum; cancellation remains observable", ["baseline", "candidate"]);
    public ValueTask PrepareAsync(ScenarioContext context) { variant = context.Variant; return ValueTask.CompletedTask; }
    public ValueTask ResetAsync(ResetContext context) { sum = completed = 0; return ValueTask.CompletedTask; }
    public async ValueTask<long> ExecuteBatchAsync(int operationCount, CancellationToken cancellation)
    {
        for (int i = 0; i < operationCount; i++)
        { sum += variant == "candidate" ? await Candidate.CachedSum(cached, cancellation) : await Baseline.CachedSum(cached, cancellation); completed++; }
        return operationCount;
    }
    public ValueTask<ValidationResult> ValidateAsync() => ValueTask.FromResult(new ValidationResult(sum == -32 * completed));
    public ValueTask CleanupAsync() => ValueTask.CompletedTask;
}
