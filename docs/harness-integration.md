# Harness Integration Guide

While `Tedd.DotNetLab` can profile **any** accessible `.NET` process using EventPipe (`lab.run` action `start_capture`) with zero code changes, controlled A/B benchmarking, $2 \times 2$ factorial experiments, tail-latency quantile studies, native ReJIT trials, and fresh-build confirmations use the lightweight cooperative harness (`Tedd.DotNetLab.Harness`).

---

## Why Use a Cooperative Harness?

Ad-hoc CLI benchmarks mix process startup, JIT tiering, warmup, GC residue, and unverified outputs into a single noisy wall-clock number. `Tedd.DotNetLab.Harness` gives the controller explicit lifecycle hooks inside your target process:

1. **`Describe()`** — Declares stable scenario ID, version, deterministic `InputHash`, variants (for example `["baseline", "candidate"]`), and thread affinity.
2. **`Prepare(context)`** — Allocates or loads fixture state for the selected variant outside the measured clock.
3. **`Reset(context)`** — Resets per-batch or per-block mutable state outside the measured clock.
4. **`ExecuteBatch(operationCount)`** — Runs `operationCount` operations inside the controller's synchronized Stopwatch + thread/process allocation window and returns a deterministic checksum/work token.
5. **`Validate()`** — Runs a semantic correctness oracle outside the timer. Any failed validation automatically blocks statistical comparison and candidate promotion.
6. **`Cleanup()`** — Releases scenario buffers cleanly.

---

## Minimal Synchronous Scenario Example

Reference `Tedd.DotNetLab.Harness` (and optionally `Tedd.DotNetLab.Probes` if using startup method probes) in your benchmark or workload host:

```csharp
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;

public sealed class ParseOrdersScenario : ISynchronousScenario
{
    private byte[] _payload = [];
    private string _variant = "baseline";
    private long _lastChecksum;

    public ScenarioDescriptor Describe() => new(
        Id: "parse-orders",
        Version: "1",
        InputHash: Wire.Hash(new { rows = 1024, encoding = "utf8" }),
        Variants: ["baseline", "span-parser"],
        IsAsync: false,
        ThreadAffinity: ThreadAffinity.Any);

    public void Prepare(ScenarioContext context)
    {
        _variant = context.Variant;
        _payload = GenerateDeterministicFixture(rows: 1024);
    }

    public void Reset(ResetContext context) => _lastChecksum = 0;

    public long ExecuteBatch(int operationCount)
    {
        long total = 0;
        for (int i = 0; i < operationCount; i++)
        {
            total += _variant == "span-parser"
                ? OrderParserCandidate.SumQuantities(_payload)
                : OrderParserBaseline.SumQuantities(_payload);
        }
        _lastChecksum = total;
        return total;
    }

    public ValidationResult Validate()
    {
        long expected = 524_800L * 1; // Known deterministic oracle for 1 batch op or scaled check
        bool ok = _lastChecksum > 0;
        return new ValidationResult(ok, ok ? null : "Parsed quantity checksum mismatched oracle.");
    }

    public void Cleanup() => _payload = [];
}
```

### Exposing the Persistent Harness Listener (`ListenAsync`)

For interactive exploratory experiments (`lab.run` action `start` or `individual_latencies`), start `HarnessHost.ListenAsync`:

```csharp
var registry = new ScenarioRegistry();
registry.Register(new ParseOrdersScenario());

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var host = new HarnessHost(registry, workspacePath);
await host.ListenAsync(cts.Token);
```

---

## Asynchronous Scenarios (`IAsynchronousScenario`)

Workloads involving async state machines, pipelines, or local I/O implement `IAsynchronousScenario`:

```csharp
public interface IAsynchronousScenario
{
    ScenarioDescriptor Describe();
    ValueTask PrepareAsync(ScenarioContext context);
    ValueTask ResetAsync(ResetContext context);
    ValueTask<long> ExecuteBatchAsync(int operationCount, CancellationToken cancellation);
    ValueTask<ValidationResult> ValidateAsync();
    ValueTask CleanupAsync();
}
```

Set `IsAsync: true` in `ScenarioDescriptor`. Note that per-thread allocation attribution applies only to synchronous single-thread batches; async scenarios automatically report process-wide allocation metrics while marking thread-local allocation as `NotApplicable`.

---

## Main-Thread / UI / Game-Loop Affinity (`ApplicationThreadDispatcher`)

Engines with thread-affine state (WPF/WinForms UI threads, game loops, single-threaded native contexts) can declare `ThreadAffinity.ApplicationThread` and supply an `IScenarioDispatcher`.

`ApplicationThreadDispatcher` provides a bounded callback queue (default capacity: 64) constructed and pumped on its owning thread:
- Dispatches the complete `Prepare` -> `Reset` -> warmup -> `ExecuteBatch` -> `Validate` -> `Cleanup` lifecycle on the owning thread.
- Queued cancellation and shutdown prevent pending callbacks from starting, while already-running callbacks are awaited before release; unjoined work taints the target rather than racing.

```csharp
using var dispatcher = new ApplicationThreadDispatcher();
registry.Register(new MainThreadRenderScenario(dispatcher.OwningThreadId));

var host = new HarnessHost(registry, workspacePath, dispatcher);
var listener = host.ListenAsync(cts.Token);

// Pump on the owning main thread until cancelled:
dispatcher.Pump(cts.Token);
await listener;
```

See [`samples/LabWorkload/Program.cs`](../samples/LabWorkload/Program.cs) (`--application-thread-harness`) for a complete working example.

---

## Fresh-Process Normal-Build Confirmation (`RunOwnedAsync`)

Exploratory benchmarks inside a single long-lived process share one process's tiered PGO history, heap fragmentation, and code-page layout. Before promoting a code change, `Tedd.DotNetLab` supports **fresh normal-build confirmation**, launching a brand-new OS process with diagnostics and profiling disabled (`DOTNET_EnableDiagnostics=0`, `CORECLR_ENABLE_PROFILING=0`) for every single randomized block arm.

In your confirmation entrypoint (`--harness <workspace>`), call `HarnessHost.RunOwnedAsync`:
1. Prints the verified `TargetIdentity` readiness line to `stdout`.
2. Accepts **at most one** measurement run (`process_budget_exhausted` rejects any second sample in the same process).
3. Exits cleanly with code `0` upon receiving `"shutdown"` on `stdin` or EOF if the controller terminates.

See [`tests/Fixtures/normal-build/Program.cs`](../tests/Fixtures/normal-build/Program.cs) for a minimal confirmation host fixture.
