# DotNetLab client evaluation bed

This suite evaluates the DotNetLab skill and MCP tools through real client processes. It combines deterministic workload oracles, raw stdio MCP tests, and authenticated `codex exec` optimization trials. It measures workflow completion and evidence quality separately from performance effects.

## Workloads and controls

| Scenario | Client objective | Semantic contrasts |
| --- | --- | --- |
| `sum-squares` | Remove intermediate array/iterator work | Empty arrays, signed extremes, widening, unchecked totals |
| `csv` | Eliminate token strings | Empty input, invariant whitespace, signed Int64 limits, unchecked sum |
| `membership` | Reduce repeated linear lookups | Ordinal comparisons, query multiplicity, duplicate allowed values, Unicode |
| `byte-scan` | Reduce scanning and allocation | All 256 byte values, 0/1/15/16/17/31/32/33/63/64/65 lengths and long tails |
| `join` | Reduce repeated string construction | Empty fields, embedded separators, Unicode, NUL, generated field lists |
| `deduplicate` | Remove quadratic membership work | Complete value set, no duplicates, first-occurrence order, input preservation |
| `prefix` | Replace repeated prefix summation | Empty arrays, signed inputs, initial value and recurrence at every index |
| `async-cache` | Remove needless suspension for cached data | Fully awaited result, synchronous completion permitted, empty/cancelled calls |
| `no-op` | Detect an honest null result | Identical implementations; no source changes permitted |
| `incorrect` | Reject an invalid candidate | Incorrect checksum; runs last because rejection can taint the target |
| `regression` | Retain a deliberately slower result | Same result with additional spin work |
| `connection` | Audit a client connection without measurement | Skill availability, target discovery, capabilities, compatibility, no grants or edits |

The independent oracle uses deterministic generated inputs and mathematical, recurrence, set, ordering, and formatting properties. Every candidate passes through a fresh verification project containing trusted source files. The evaluator reads portable PDB metadata to bind `Candidate.cs` to the client DLL, then looks for that DLL hash in observed MCP responses. Agent assertions alone do not establish correctness or measured code identity.

## Prerequisites

- Windows x64, Python 3.12+, stable .NET 10 SDK and runtime.
- For repository native regressions: the C++ prerequisites in [building and verification](../../docs/building-and-verification.md).
- For model trials: an installed Codex CLI supporting `exec --json --ephemeral --output-schema` and existing `codex login` authentication. The invocation follows [Codex non-interactive mode](https://learn.chatgpt.com/docs/non-interactive-mode). CLI trials consume account usage. Model trials are opt-in and never run in public CI.

The PowerShell wrapper selects an installed stable .NET 10 runtime for its test build. This avoids the repository's fixed runtime version requiring a patch absent from the test machine.

Codex trials select the `:workspace` permission profile, keep legacy `sandbox_mode` unset, and request the native Windows sandbox. Permission configuration belongs to the isolated trial, with scratch paths added explicitly; no user settings are modified. See [Codex permission profiles](https://learn.chatgpt.com/docs/permissions) and [Windows sandbox configuration](https://learn.chatgpt.com/docs/windows/windows-sandbox).

## Run

Run deterministic evaluator tests without authentication or .NET:

```powershell
python -B -m unittest discover -s tests/client-evaluation -p test_evaluate.py
```

Build and exercise real MCP tools, JIT disassembly, individual observations, and 20 fresh normal-build confirmation processes:

```powershell
./tests/client-evaluation/run.ps1
```

Include the full serial repository suite and all nine Codex scenarios:

```powershell
./tests/client-evaluation/run.ps1 -RepositoryRegression -Codex
```

For a sandboxed Windows client whose CLI cannot access the controller's grant pipe, evaluate the reference candidates with owner-issued grants:

```powershell
./tests/client-evaluation/run.ps1 -Codex -Pregrant -PreparedVariants
```

This mode assesses Codex's measurement, interpretation, error recovery and notebook use. Candidate implementations come from `ReferenceCandidate.cs.in`; it does not assess source synthesis. The launcher keeps an owner connection alive and supplies grants limited to the private fixture. The server's unattended tool approvals are configured only inside that trial; execution still requires the scoped grants, and managed restrictions still apply. Source-edit trials use the default mode, optionally with `--pregrant --warm-controller` through the Python driver.

Use a narrower model trial, or repeat trials for workflow variability:

```powershell
./tests/client-evaluation/run.ps1 -Codex -Cases 'sum-squares,csv,membership,async-cache,no-op' -TimeoutSeconds 900 -Repetitions 3
```

Use a previously built host without rebuilding:

```powershell
python -B tests/client-evaluation/evaluate.py reference --root D:/Workspaces/AI/dotnetlab-evaluation/runs --host D:/Workspaces/AI/dotnetlab-build/bin/Tedd.DotNetLab.Host/Release/net10.0/Tedd.DotNetLab.Host.dll
python -B tests/client-evaluation/evaluate.py codex --root D:/Workspaces/AI/dotnetlab-evaluation/runs --host D:/Workspaces/AI/dotnetlab-build/bin/Tedd.DotNetLab.Host/Release/net10.0/Tedd.DotNetLab.Host.dll --cases csv --timeout 900
```

`--candidate-source <path> --prepared-variants` loads an existing `Candidate.cs` snapshot instead of the reference implementation. A completed, verified prepared `sum-squares` snapshot can undergo owner-assisted fresh-process confirmation with `evaluate.py confirm --root <trial-directory> --host <host-dll>`. Source and measured assembly hashes are rechecked before the prospective 20-process holdout; the model's original outcome and score remain unchanged. Repeating confirmation for an unchanged snapshot returns the retained decision.

`--model` preserves an explicitly selected model; omission uses the installed CLI default. `--skill-mode repository`, `installed`, or `none` enables version comparison and a no-skill ablation. The no-skill prompt still supplies the same workflow requirements, so it isolates operational reference assistance rather than all performance methodology. Use multiple repetitions before inferring a difference between skill conditions. The `summarize --root <root>` mode reads retained summaries without executing workloads.

Run measurement layers serially. The machine-wide mutex serializes cooperating operations, but concurrent suites can exhaust deadlines while waiting and unrelated host activity still affects timing.

## Evidence and scoring

Each trial has its own fixture, SQLite catalog, MCP connector, Codex configuration, scratch directory, and raw JSONL transcript. The configured MCP server is required: initialization failure cannot silently turn a trial into a prose-only exercise. The driver copies the selected skill into project scope and loads no user MCP configuration. An authentication copy is removed in `finally`; it must never be included in exported evidence.

Reference tests cover tool catalogs, native-extension opt-in, concurrent clients and disconnects, immutable discovery paging, workspace and identity binding, invalid requests, grants, epoch mismatch, idempotency, scenario validation, retained comparisons, EventPipe, CLI forwarding, JIT, individual latency, registered builds, and fresh confirmation. The skill audit separately records broken references and non-executable documented requests. Known documentation defects do not become expected successful API behavior.

Codex scores record these observable gates:

1. A completed CLI turn with zero exit status and no timeout.
2. Successful MCP use and the required discovery actions.
3. A retained hypothesis and plans created before every linked execution.
4. Completed operation IDs linked to submitted starts and compared execution IDs.
5. Independent oracle success and unchanged trusted fixtures.
6. Structured output and citations to IDs actually observed, including nested objective vectors.
7. Promotion eligibility returned by the engine for any claimed confirmed improvement.
8. Matching assembly/PDB/source metadata and observed code identity.
9. An honest disposition and unchanged source for the null control.

Scores measure workflow adherence, not optimization success. A correct candidate may have inconclusive performance. Same-process blocks remain exploratory. A requested confirmation is insufficient to claim promotion. Batch timing cannot establish request p99; sampled stacks cannot establish hardware counters. The reference allocation policy is descriptive and explicitly frozen; mandatory allocation non-regression requires a prospective inferential policy suitable for the client objective.

Core scores cover discovery, prospective execution and evidence binding. Review frozen guardrails, hypothesis dispositions and interpretation separately: completing every core gate does not imply those methodological obligations were fulfilled.

`summary.json`, per-scenario comparisons, source patches, `skill-audit.json`, environment/build hashes, and transcripts support diagnosis. Exit status distinguishes infrastructure/execution failures from completed trials with low behavioral scores. A timeout or driver exception is retained rather than converted into a pass. Raw events should be reviewed before publishing: they can contain local paths and command output.

Export finalized evidence using the explicitly selected reference run:

```powershell
python -B tests/client-evaluation/collect_results.py --root $EvaluationRoot --reference-run $ReferenceRun --output tests/client-evaluation/results/local.json
```

Set `$EvaluationRoot` to the session directory containing `runs` and `regression`, and `$ReferenceRun` to the selected `reference-*` directory name. The collector retains timing/allocation observations, frozen plans, decisions, request errors, token usage and TRX failures. It excludes authentication, raw traces and unrelated process inventories, and replaces workspace paths with symbolic roots.

Outputs are restricted to dedicated subdirectories beneath `D:/Workspaces/AI` (or `/mnt/d/Workspaces/AI` for direct Python use). Paths through symlinks or junctions are rejected. The evaluated client may edit only `Candidate.cs`; its other local scripts and artifacts stay inside the case directory. The final verification project is reconstructed independently of its `bin`, `obj`, and build overlays. A source scope check rejects obvious external-write or dynamic-loading APIs before verification. This is an evaluation harness for scoped coding agents; it is not a security sandbox for hostile C#.

Preserve the summaries and candidate patches needed for review, stop owned processes, then remove the specific task-owned output directory. Never remove the workspace parent or another session's directory. The top-level task lifecycle determines whether evidence is retained or exported before cleanup.

## Coverage boundaries

The live client is Codex CLI. Other client wire compatibility is exercised at the MCP boundary and through existing installer tests; this suite does not claim to automate Claude Code, Cursor, or Antigravity. Native mutation coverage comes from the repository regression suite. No production endpoints, application databases, globally installed tools, or user MCP settings are changed.

The default model budget is finite. Trials without fresh confirmation retain exploratory results. A single trial per scenario is a usability observation, not an estimate of model reliability. For comparative evaluation, hold model, reasoning settings, runtime, machine load, workload version and budgets fixed, vary one skill condition, and retain every trial.
