# WildcardMatch integration exercise

This driver exercises the real stdio MCP server against a CPU-only workload compiled from a supplied `Tedd.WildcardMatch` checkout. It requires Windows x64, Python, SDK 10.0.402, a built Tedd.DotNetLab host, and a .NET installation containing CoreCLR 10.0.12. It does not modify the supplied checkout or client configuration.

Use a clean checkout of wildcard revision `4f1a8b7442168698a3ee936978d504f2b3a8fe32`. The source transformation deliberately requires the original newline-scan implementation. A later optimized checkout fails that check.

Run stages sequentially, using the same arguments and a fresh dedicated scratch directory beneath `D:\Workspaces\AI`:

```powershell
$driver = 'D:\SourceCode\Tedd.DotNetLab\tests\wildcard-validation\validate.py'
$arguments = @(
    '--scratch', 'D:\Workspaces\AI\wildcard-reproduction',
    '--source', 'D:\Workspaces\AI\wildcard-source-at-baseline',
    '--host', 'D:\Workspaces\AI\dotnetlab-implementation\bin\Tedd.DotNetLab.Host\Release\net10.0\Tedd.DotNetLab.Host.dll',
    '--dotnet', 'D:\Workspaces\AI\dotnetlab-runtime\dotnet.exe'
)
foreach ($stage in 'setup','explore','diagnostic','analyze','confirm','native','extension','archive') {
    python $driver $stage @arguments
    if ($LASTEXITCODE -ne 0) { throw "Validation stage failed: $stage" }
}
```

The source checkout must already exist. Review its source and all startup, preparation, validation, and cleanup effects before executing it. The supplied workload performs local matching and independent Regex validation. Tedd.DotNetLab resolves catalog writes to `<scratch>\catalog\catalog.sqlite`; the driver uses no external database or telemetry backend.

The exercise covers six base tools, the optional seventh tool's qualification gate, scenario discovery, scoped CLI grants, quiet comparisons, diagnostic capture, runtime counters/GC, sampled stacks, baselines/aliases, individual timing, offline IL, native attachment/inspection/decoding, fresh-build confirmation, decisions, backup, export, verified reimport, artifact slicing, restart, and checkpointing. Grants run only while the task's MCP controller is stopped. Persistent workload processes and MCP controllers shut down before each stage returns.

The four scenarios share one literal-heavy pattern and cycle four fixed inputs per batch. Individual observations invoke `ExecuteBatch(1)`, so that separate diagnostic measures the first input only. They do not establish p99. The confirmation compiles the wildcard implementation into distinct entry assemblies because that is Tedd.DotNetLab's qualified treatment boundary; it does not compare two changed dependency DLLs.

Same-process exploration is descriptive. Confirmation freezes ten randomized paired fresh-process blocks per scenario, with no failed-session replacement or sample expansion. Family size is four; dense-input benefit requires a ratio below 0.99. Ordinary/newline guardrails use a ±5% equivalence margin. Assess all four scenarios jointly; a scenario's isolated promotion flag does not establish the combined decision. OS load, scheduling, thermal state, affinity, and frequency are uncontrolled; fresh process identities do not establish stochastic independence. Reserve the measurement host separately for stronger environmental control.

The driver retains raw requests, responses, schemas, plans, measurements, source snapshots, trace analysis, and a hash-verified `evidence.zip`. Artifact responses are assembled through advertised slices and SHA-256 checked. Export explicitly includes the synthetic trace and inspected build bytes. Reimport verifies integrity and retains the service's unverified-origin qualification.

Preserve required evidence and source snapshots at their permanent destination before removing the task-owned scratch directory. The research report in `Tedd.WildcardMatch/Research/2026-10-15 dotnetlab-validation` records the measured investigation and its limitations.
