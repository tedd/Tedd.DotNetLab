# CLI and MCP Reference

This document covers the `Tedd.DotNetLab.Host` (`dotnetlab`) command-line interface, the human-in-the-loop authorization grant model, and the MCP tool catalog exposed to AI coding agents.

> [!NOTE]
> If you installed `Tedd.DotNetLab` via `tools/install.ps1` or `tools/install.sh`, `dotnetlab` is available directly on your `PATH`. When running from a build directory or release ZIP without the shim, replace `dotnetlab` below with `dotnet /path/to/Tedd.DotNetLab.Host.dll`.

---

## CLI Commands

### Setup and Client Configuration

```text
dotnetlab install [all|codex|claude|cursor|antigravity] [options]
dotnetlab uninstall [all|codex|claude|cursor|antigravity] [options]
```

Copies the bundled `dotnetlab` agent skill and registers the `dotnetlab` MCP server idempotently (with `--roll-forward Major` and `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`) while preserving all unrelated client settings.

| Option | Description |
| --- | --- |
| `all \| codex \| claude \| cursor \| antigravity` | Target AI client(s) to configure—accepts `agy` as an alias for `antigravity` and comma-separated lists like `cursor,antigravity` (default: `all`). |
| `--workspace <path>` | Workspace directory for `catalog.sqlite` and `artifacts/` (defaults to `%LOCALAPPDATA%\Tedd.DotNetLab\workspace` on Windows or `~/.local/share/dotnetlab/workspace` on Linux). |
| `--scope <user\|project>` | Install into user-wide config (`~/.codex`, `~/.claude`, `~/.cursor`, `~/.gemini/config`) or project-scoped config (`.codex/config.toml`, `.agents/skills`, `.agents/mcp_config.json`, `.claude/skills`, `.mcp.json`, `.cursor/skills`, `.cursor/mcp.json`). Default: `user`. |
| `--project-dir <path>` | Target repository directory when `--scope project` is selected (default: current directory). |
| `--enable-native-extension` | Appends `--enable-native-extension` to the registered MCP server arguments so `lab.patch` is available. |
| `--server-name <name>` | MCP server key and skill folder name (default: `dotnetlab`). |
| `--dotnet <executable>` | Explicit `dotnet` host path to record in MCP client configuration. |
| `--codex-home` / `--claude-home` / `--claude-config` / `--cursor-home` / `--antigravity-home` | Override user-scope client config directories/files (useful for custom home layouts or CI testing). |
| `--dry-run` | Prints the planned skill/config paths as JSON without writing files. |

### MCP Transport and Workspace Lifecycle

| Command | Description |
| --- | --- |
| `dotnetlab connect <workspace> [--enable-native-extension]` | **Recommended MCP entrypoint.** Connects stdio to the workspace's shared background controller (starting it automatically if needed). |
| `dotnetlab serve <workspace> [--enable-native-extension]` | Runs a dedicated single-client stdio MCP server with exclusive catalog ownership. |
| `dotnetlab init <workspace>` | Initializes `<workspace>` (`catalog.sqlite`, schema v5, resource policy) and prints its canonical root. |
| `dotnetlab compatibility` | Prints build pins, controller runtime state, dependency hashes, and platform feature matrix without opening a workspace. |
| `dotnetlab profiles` | Lists built-in diagnostic/capture profiles (`quiet-validation`, `baseline-context`, `cpu-diagnostic`, `allocation-diagnostic`, `runtime-metrics-context`, `targeted-probes`). |
| `dotnetlab schema <name>` | Prints a versioned JSON Schema (`measurement`, `plan`, `comparison`, `capabilities`, `error`, etc.) without opening a workspace. |
| `dotnetlab call <workspace> <tool>` | Invokes any MCP tool (`lab.discover`, `lab.inspect`, `lab.experiment`, `lab.run`, `lab.query`, `lab.compare`, `lab.patch`) from CLI, reading the JSON request object from `stdin`. |
| `dotnetlab capture <workspace> <pid> <profile> <seconds>` | Human convenience command: issues a one-shot `observe` grant and captures an EventPipe trace from `<pid>`. |
| `dotnetlab import <workspace> <path> <media-type>` | Imports a local file (such as a `.dll`, `.json`, `.diff`, or `.nettrace`) into the content-addressed workspace store and prints its `Artifact` record. |
| `dotnetlab backup <workspace>` | Creates a verified consistent SQLite backup of the workspace catalog. |
| `dotnetlab checkpoint <workspace> [--truncate]` | Checkpoints the SQLite WAL journal (optionally truncating the WAL file). |

---

## Human-Issued Security Grants

`Tedd.DotNetLab` enforces a strict **human-in-the-loop capability boundary**: MCP tools can discover processes, inspect stored records, and prepare immutable experiment/confirmation/attachment plans, **but an AI agent cannot self-authorize attaching to a live process, executing code, mutating IL, launching builds, or deleting/exporting evidence.**

### Issuing Grants While Connected

CLI commands against a workspace with a running shared `connect` controller forward over its current-user-only named pipe. Issue `grant*` commands while MCP clients remain connected; the controller serializes catalog writes. The agent revalidates `(PID, start_time_utc_ticks, epoch)` before using the grant. A dedicated `serve` process does not expose this forwarding endpoint and must release its workspace before another CLI process opens it.

### Grant Commands

```bash
# 1. Live process observation (EventPipe CPU, allocation, GC, counters)
dotnetlab grant <workspace> <pid> observe <seconds> <max-operations>

# 2. Execute registered cooperative benchmark scenarios in a running harness
dotnetlab grant <workspace> <pid> execute_registered_scenario <seconds> <max-operations>

# 3. Copy and disassemble JIT-compiled native machine code from a profiled target
dotnetlab grant <workspace> <pid> inspect_live_code <seconds> <max-operations>

# 4. Authorize a frozen fresh-process normal-build confirmation plan
dotnetlab grant-confirmation <workspace> <confirmation-plan-id> <seconds> <max-operations>

# 5. Authorize one live Windows CoreCLR profiler attachment for a prepared plan
dotnetlab grant-profiler-attach <workspace> <attach-plan-id> <seconds> 1

# 6. Authorize bounded live CIL ReJIT mutation & restoration for a registered candidate
dotnetlab grant-cil <workspace> <cil-candidate-id> <seconds> <max-operations>

# 7. Authorize evidence bundle export (add --include-raw-traces to permit raw .nettrace export)
dotnetlab grant-export <workspace> <export-plan-id> <seconds> <max-operations> [--include-raw-traces]

# 8. Authorize unpinned artifact deletion or explicit pin release
dotnetlab grant-deletion <workspace> <deletion-plan-id> <seconds> <max-operations>
dotnetlab grant-release <workspace> <release-plan-id> <seconds> <max-operations>
```

---

## MCP Tool Catalog

Every MCP tool accepts a single top-level argument `{"request": { ... }}` using `action` (or `view` for `lab.query`) as the polymorphic discriminator. All 64-bit integers (`epoch`, `revision`, `seed`, `start_time_utc_ticks`, byte offsets) are serialized as canonical decimal strings to prevent IEEE-754 precision loss.

### 1. `lab.discover` — Environment, Targets, and Capabilities

| `action` | Purpose |
| --- | --- |
| `workspace` | Returns workspace identity, schema version, and active `WorkspacePolicyRevision` resource limits. |
| `compatibility` | Returns build pins, controller runtime compatibility, dependency hashes, and platform matrix (optionally including retained observations for `target_id`). |
| `profiles` | Lists diagnostic/capture profiles, EventPipe provider configurations, and data classes. |
| `targets` | Discovers local `.NET` processes with accessible diagnostic pipes and binds `(target_id, pid, start_time_utc_ticks)`. |
| `capabilities` | Evaluates live capabilities (`eventpipe`, `registered_scenarios`, `native_inspection`, `rejit`, `timing_probes`) on `target_id`. |
| `scenarios` | Performs a cooperative harness handshake with `target_id`, returning registered scenario IDs, versions, input hashes, variants, and runtime footprint. |
| `profiler` | Queries the loaded CoreCLR native profiler handshake (`ICorProfilerInfo12`, startup vs. attach, inliner handling, active epoch). |

### 2. `lab.inspect` — Records, Stored Assemblies, and Live Disassembly

| `action` | Purpose |
| --- | --- |
| `record` / `artifact` | Reads an immutable/versioned catalog record or artifact metadata descriptor. |
| `methods` | Lists managed methods, tokens, MVIDs, and signatures in an imported `.dll` PE image. |
| `il` | Disassembles stored CIL instructions and exception clauses for a method token in an imported `.dll`. |
| `native` | Captures a verified snapshot of JIT-compiled x64 machine code tiers from a live profiled target (requires `inspect_live_code` grant). |
| `offline_native` | Decodes x64 disassembly (`Iced`) from a previously captured native code snapshot artifact. |
| `native_diff` | Computes structured x64 instruction diffs between original and candidate native snapshots. |

### 3. `lab.experiment` — Notebook Ledger, Preregistered Plans, and Freezing

| `action` | Purpose |
| --- | --- |
| `create_hypothesis` / `revise_hypothesis` | Records falsifiable mechanism claims and quantitative predictions in the experiment ledger. |
| `create_work_item` / `revise_work_item` | Tracks investigation questions and DAG dependencies (rejecting cycles). |
| `create_candidate` / `record_decision` | Registers optimization candidates and records evidence-backed adoption/rejection decisions. |
| `create_plan` | Freezes a randomized complete block experiment plan (2-arm A/B, multi-variant, or $2 \times 2$ `FactorialDesign`, primary metric, guardrails, warmup, seed, and optional `target_runtime_version`). |
| `create_quantile_plan` | Freezes a cluster-bootstrapped tail-latency quantile plan (e.g., p50, p95, p99) over paired runs or observation sets. |
| `create_individual_latency_plan` | Freezes a per-operation individual latency capture plan. |
| `create_baseline` / `set_baseline_alias` | Seals control runs into an immutable baseline fingerprint and binds named baseline aliases. |
| `register_normal_build` | Validates and pins a flat managed `.dll` + `.deps.json` + `.runtimeconfig.json` build bundle for fresh-process confirmation. |
| `create_confirmation_plan` | Freezes a confirmatory holdout plan binding baseline & candidate normal builds, source diff/manifest, equivalence rationale, and prior exploratory evidence. |
| `prepare_profiler_attachment` | Freezes target/runtime/binary SHA-256 identity for one-shot Windows live profiler attachment. |
| `register_cil_candidate` | Validates a replacement CIL method body against metadata/signature/local/branch safety contracts. |
| `register_compiled_candidate` | Extracts a selected method from an imported rebuilt assembly and remaps its CIL references into the live target's existing metadata; accepts `compiled_artifact_id`, `method_token`, `inspection_id`, `base_il_hash`, and `components`. Does not execute or load the candidate assembly. |
| `create_compilation_control` / `create_native_trial` / `create_native_series` | Freezes baseline ReJIT compilation controls and paired native ReJIT trials/multi-session series. |

### 4. `lab.run` — Asynchronous Execution, Capture, and Lifecycle Jobs

| `action` | Purpose |
| --- | --- |
| `start_capture` / `stop_capture` | Starts or stops a bounded EventPipe diagnostic trace capture on a target process. |
| `analyze_capture` / `analyze_stacks` | Launches isolated `TraceWorker` parsing for GC/counter timelines or inclusive/exclusive CPU & allocation call-stack hotspots. |
| `start` | Executes a frozen `ExperimentPlan` against a cooperative target harness. |
| `individual_latencies` | Executes a frozen `IndividualLatencyPlan` and retains the per-operation latency vector. |
| `compare_quantiles` | Runs deterministic cluster bootstrap inference over registered quantile blocks/observations. |
| `start_confirmation` | Launches fresh, diagnostics-disabled OS processes per randomized block arm to confirm a candidate in normal builds. |
| `attach_profiler` / `reconcile_profiler_attachment` | Executes or reconciles a granted live Windows profiler attachment. |
| `compare_native_series` / `register_native_series_results` | Evaluates multi-session native ReJIT trial series. |
| `status` / `cancel` | Polls read-only operation state or requests cooperative/process cancellation. |

### 5. `lab.query` — Evidence Views and Bounded Slice Retrieval

Supported `view` values:
- **Hotspots & Traces:** `hotspots` (inclusive/exclusive rankings), `sampled_method`, `metric`, `gc_timeline`, `suspensions`, `counters`, `counter_window`, `run_window`, `available_evidence`.
- **Latencies & Native:** `individual_latency_slice`, `verify_native_trial`, `native_series_comparison`.
- **Catalog & Chunks:** `record`, `history`, `artifact_slice` (reads chunked oversized MCP responses or artifacts), `controller_resources`, `operation_policy`.

Evidence administration is available through `dotnetlab call <workspace> <tool>`, with a JSON request on stdin. These actions are excluded from connected MCP schemas:

| CLI tool | Actions / views |
| --- | --- |
| `lab.experiment` | `prepare_export`, `prepare_bundle_import`, `prepare_deletion`, `prepare_retention`, `pin_evidence`, `prepare_reference_release` |
| `lab.run` | `start_export`, `import_bundle`, `analyze_imported_capture`, `start_deletion`, `release_references`, `recover_artifacts`, `backup_catalog`, `checkpoint_catalog` |
| `lab.query` | `artifact_retention`, `artifact_references`, `artifact_recovery`, `verify_catalog_backup`, `verify_export`, `verify_bundle_import`, `imported_members` |

Existing grant requirements apply to administrative operations. For example:

```powershell
'{"action":"backup_catalog","idempotency_key":"backup-1"}' | dotnetlab call <workspace> lab.run
```

### 6. `lab.compare` — Statistical Inference and Promotion Gates

Evaluates a completed `execution_id`:
- Standard exploratory/confirmatory paired block designs (`CompareRequest(ExecutionId)`): log-ratio Welch/paired $t$-intervals, Holm-Bonferroni family-wise error adjustment, TOST equivalence, and primary + guardrail `IncludeObjectives: true` Pareto vectors.
- $2 \times 2$ factorial designs (`IncludeFactorial: true`): main effects $A$, $B$, and interaction contrast $AB$ (detecting synergy vs. redundancy/interference).
- Fresh normal-build confirmation (`NormalBuildConfirmation: true`): verifies 100% session uniqueness, build hashes, quiet runtime invariants, and frozen exploratory provenance before setting `promotion_eligible: true`.

### 7. `lab.patch` — Optional Live Native Probes and CIL ReJIT (`--enable-native-extension`)

| `action` | Purpose |
| --- | --- |
| `prepare_probe` / `flush_probes` | Instruments qualified startup targets with low-overhead synchronous invocation/elapsed probes and flushes aggregated counters. |
| `preflight` | Verifies CIL replacement safety, caller inlining closure (up to 31 callers), and memory budgets without mutating execution. |
| `apply` | Applies a granted CIL ReJIT transaction, runs untimed correctness validation, advances target epoch, and records compilation state. |
| `restore_original` / `recover_interrupted` | Reverts a mutated method and its inliners back to original IL and verifies post-restore correctness. |
| `prepare_original_control` / `run_compilation_control` / `run_native_trial` | Runs ReJIT-of-original-IL compilation controls so ReJIT tiering mechanics are separated from candidate code changes. |

---

## Contract JSON Schemas (`schemas/api/v1`)

Machine-readable JSON Schemas (Draft 2020-12) for plans, measurements, capabilities, comparisons, and errors live under [`schemas/api/v1`](../schemas/api/v1). Regenerate them after contract changes via `tests/export-contract-schemas.ps1`, or print an individual schema with `dotnetlab schema <name>`.
