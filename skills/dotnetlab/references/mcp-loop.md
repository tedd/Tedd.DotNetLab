# MCP Core Loop, Connection & Profiling

Reference for connecting to DotNetLab, issuing scoped CLI grants, profiling bottlenecks via EventPipe, managing the hypothesis notebook, and running controlled 2-arm or $2\times 2$ factorial experiments.

## 1. Launch, Runtime Matrix & Client Setup

```text
<dotnet> <absolute Tedd.DotNetLab.Host.dll> connect <absolute workspace> [--enable-native-extension]
```

- **Shared Controller Architecture:** `connect` launches a lightweight stdio connector to a per-workspace shared controller over a current-user-only named pipe. Multiple MCP clients share the workspace catalog; the controller auto-starts on demand and exits after **5 seconds** of inactivity once all clients disconnect and no jobs or uncertain measurements remain. (`serve` runs a single-client dedicated stdio controller.)
- **Machine-Wide Measurement Mutex:** Cooperating controllers serialize timed runs, standalone captures, and trace parsing through a machine-wide named mutex.
- **Supported Runtimes:**
  - **Controller (`Tedd.DotNetLab.Host.dll`):** Windows/Linux x64 CoreCLR **10.0.0 or later** (`.NET 10.x`, `.NET 11` previews/RCs/GA, `.NET 12+` with `--roll-forward Major` and `DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`). Prefer an installed Release binary (`%LOCALAPPDATA%\Tedd.DotNetLab\bin\Tedd.DotNetLab.Host.dll` or `~/.local/share/dotnetlab/bin/Tedd.DotNetLab.Host.dll`) or verified Release build output.
  - **Targets & Normal Builds:** Managed target execution, EventPipe capture, fresh normal-build confirmation, and native `ICorProfilerInfo12` inspection/probes/ReJIT accept **any CoreCLR `.NET 6.0` or later** runtime (`.NET 6`, `.NET 8`, `.NET 9`, `.NET 10`, `.NET 11` RCs/previews, `.NET 12+`) on Windows/Linux x64. Live profiler attach (`attach_profiler`) is supported on Windows x64 CoreCLR `.NET 6.0+`. Raw `corerun.exe` (`12.0.0-dev`) is supported on Windows x64 `quiet-validation` with leased CoreLib/CoreCLR/JIT SHA-256 hashes at epoch `"0"`.
  - **Client Installer:** `dotnet <Host.dll> install [all|codex|claude|cursor|antigravity] --workspace <abs-path>` configures Codex, Claude Code, Cursor, and Google Antigravity (`agy`) without clobbering unrelated MCP servers.

## 2. Scoped CLI Grants

Issue CLI grants directly for operations within the user's authorized task. Use short expiries and sufficient operation budgets for the planned work and restoration. Manual approval is optional: if the user requests it, present the exact command and wait for approval before issuing the grant, or let the user run it. Discovery alone does not authorize unrelated targets or actions. Grant commands are:

```text
<host> grant <workspace> <pid> observe <seconds> <max-ops>
<host> grant <workspace> <pid> execute_registered_scenario <seconds> <max-ops>
<host> grant <workspace> <pid> inspect_live_code <seconds> <max-ops>
<host> grant-confirmation <workspace> <confirmation-plan-id> <seconds> <max-ops>
<host> grant-scratch <workspace> <scratch-dir> [observe|execute_registered_scenario] <seconds> <max-ops>
<host> grant-build <workspace> <normal-build-id> <seconds> <max-ops>
```

- **Live Grant Forwarding:** While a `connect` shared controller is running, CLI commands against the same workspace (`grant*`, `import`, `call`, …) are **forwarded to the live controller** over the current-user-only pipe—no disconnect is needed. Only a dedicated `serve` controller (or a stale lock) still requires stopping the controller first.
- **Grant Request Sequence:**
  1. Ensure no jobs are running against the target you want granted.
  2. Run the exact CLI command with verified absolute workspace path, live `<pid>` (or `<plan-id>` / scratch dir / build id), permission scope, expiry seconds, and operation count. Wait for approval first only when the user requires manual approval.
  3. Retain the returned `grant_id` and re-verify `target_id` and `expected_target_epoch` via `lab.discover`.
- **Distinct Scopes:** `observe` (standalone EventPipe or diagnostic run window) != `execute_registered_scenario` (quiet harness execution). Runs using diagnostic profiles (`cpu-diagnostic`, `allocation-diagnostic`, etc.) require **both** `grant_id` (`execute_registered_scenario`) and `observation_grant_id` (`observe`).
- **Scratch Grants (`grant-scratch`):** Authorize any process whose executable lives **strictly inside** `<scratch-dir>` (no reparse points) for the grant window—rebuilds and new PIDs stay covered, so the iterate → rebuild → rerun loop needs one grant. Omitting the permission issues an `observe` + `execute_registered_scenario` pair. Use a narrow, owned disposable directory and a short expiry. See §7.
- **Build Grants (`grant-build`):** Authorize `capture_jit_disasm` to launch a registered normal build (`build_id`) as an owned process.

## 3. Discovery & Artifact Inspection (`lab.discover`, `lab.inspect`, `lab.query`)

All MCP calls wrap parameters inside `{"request":{...}}`. Int64/UInt64 fields (`expected_target_epoch`, `seed`, `offset`, byte budgets) use decimal strings.

```json
{"request":{"action":"workspace"}}
{"request":{"action":"profiles"}}
{"request":{"action":"targets","limit":50}}
{"request":{"action":"capabilities","target_id":"tgt_..."}}
{"request":{"action":"compatibility","target_id":"tgt_..."}}
{"request":{"action":"scenarios","target_id":"tgt_..."}}
```

- Always inspect `capabilities` and `compatibility` **reasons** for the selected `target_id`; diagnostic visibility alone does not imply cooperative `Tedd.DotNetLab.Harness` scenario support or native profiler readiness.
- **Records & Bounded Artifact Paging:**
  - Read retained metadata: `lab.inspect` `{"request":{"action":"record","id":"..."}}` or `{"request":{"action":"artifact","artifact_id":"art_..."}}`.
  - Read large outputs in bounded slices rather than dumping entire traces into context:
    `lab.query` `{"request":{"view":"artifact_slice","artifact_id":"art_...","offset":"0","count":16384}}`.
  - Pagination `cursor` tokens are valid only for the exact `view`/`result_id` that produced them.

## 4. EventPipe Profiling & Hotspot Localization

| Profile | Use Case |
| --- | --- |
| `cpu-diagnostic` | Sampled managed call stacks + runtime/GC events for CPU hotspot localization. |
| `allocation-diagnostic` | Sampled allocation ticks + GC generations, pause durations, and heap growth. |
| `baseline-context` | Low-overhead runtime/GC/counter context without CPU stack sampling. |
| `runtime-metrics-context` | Runtime metric/counter time series + harness run-window markers. |
| `jit-and-contention-diagnostic` | JIT method compilations (tier), inlining successes/failures **with `FailReason`**, and `Monitor`/lock contention start/stop events. |
| `quiet-validation` | Zero-EventPipe harness timing mode (not valid for standalone `start_capture`). |
| `targeted-probes` | Harness run with verified installed native method probes (`lab.patch`). |

### Capture -> Poll -> Analyze Stacks & Counters

1. **Start Capture** (`lab.run`):
   ```json
   {"request":{"action":"start_capture","target_id":"tgt_...","expected_target_epoch":"0","profile":"cpu-diagnostic","duration_seconds":5,"grant_id":"grt_observe","idempotency_key":"cap-baseline-1"}}
   ```
2. **Poll to Completion** (`lab.run`):
   ```json
   {"request":{"action":"status","operation_id":"op_..."}}
   ```
   Completed `result_id` is a `capture_id`. Check `lab.query` `{"request":{"view":"available_evidence","capture_id":"cap_..."}}`. (Use `stop_capture` or `cancel` only when explicitly terminating early, and still poll until terminal cleanup.)
3. **Stack Hotspots** (`lab.run` -> `lab.query`):
   - Submit `{"request":{"action":"analyze_stacks","capture_id":"cap_...","idempotency_key":"stk-1"}}` -> poll `status` -> `result_id` is `stack_analysis_id`.
   - Query exclusive (`self`) and call-tree (`inclusive`) hotspots:
     ```json
     {"request":{"view":"hotspots","result_id":"stk_...","ranking":"self","limit":20}}
     {"request":{"view":"hotspots","result_id":"stk_...","ranking":"inclusive","limit":20}}
     {"request":{"view":"sampled_method","result_id":"stk_...","selector":"MyNamespace.MyType.HotMethod"}}
     ```
   - **Interpretation Rules:** `self` identifies leaf bottlenecks; `inclusive` identifies expensive call subtrees (never add overlapping `inclusive` percentages together). Filtered views keep total observed samples as the denominator. Report symbol coverage and trace quality.
4. **GC, Suspensions & Run-Aligned Counters** (`lab.run` -> `lab.query`):
   - Capture completion triggers automatic trace analysis when possible; if needed, run `{"request":{"action":"analyze_capture","capture_id":"cap_...","idempotency_key":"trc-1"}}` -> `analysis_id`.
   - Query `gc_timeline`, `suspensions`, `counters`, `counter_window`, `metric`, or align metrics to a specific harness run via `{"request":{"view":"run_window","analysis_id":"ana_...","run_id":"run_..."}}`.
5. **JIT Inlining & Lock Contention** (capture with `jit-and-contention-diagnostic`, then `lab.query`):
   ```json
   {"request":{"view":"jit_inlining","analysis_id":"ana_...","filter":"HotMethod","failures_only":true,"limit":50}}
   {"request":{"view":"jit_methods","analysis_id":"ana_...","filter":"MyNamespace","limit":50}}
   {"request":{"view":"contentions","analysis_id":"ana_...","limit":20}}
   ```
   - `jit_inlining` answers *"why is my helper not inlined?"* via `fail_reason` (e.g. `too many il bytes`, `has exception handling`, `noinline per IL/cached result`). `jit_methods` shows which methods were (re)compiled and at which tier.
   - `contentions` lists the longest lock waits; `metric` exposes `jit.methods_compiled`, `jit.inline_succeeded`, `jit.inline_failures`, `contention.count`, and `contention.duration_ms`.
   - Contention events cover blocking managed locks (`lock`/`Monitor`, `System.Threading.Lock`); pure spin loops (`SpinLock`, `SpinWait`) show up as CPU samples instead.

## 5. Cooperative Harness Scenarios & Notebook Ledger

Controlled A/B experiments execute scenarios registered in the target process via `Tedd.DotNetLab.Harness` (`ScenarioRegistry` + `IScenario` / `IAsyncScenario`).
- Each scenario declares its `Id`, `Version`, canonical `InputHash`, advertised `Variants` (e.g., `"baseline"`, `"candidate"`), `Setup`/`Reset`/`ExecuteBatch`/`Validate`/`Cleanup` lifecycle, and an **independent correctness oracle** inside `Validate()`.
- The lab controller **cannot** invent variants from arbitrary strings or invoke unregistered methods; build and start/reload the target with real registered variants, then re-run `lab.discover` `scenarios` to read exact `scenario_id`, `scenario_version`, `input_hash`, and `variants`.

### Hypothesis, Work-Item & Candidate Notebook (`lab.experiment`)

```json
{"request":{"action":"create_hypothesis","mechanism":"Dictionary<string,int> lookup allocates Substring per token in ParseLine.","prediction":"Using GetAlternateLookup<ReadOnlySpan<char>>() eliminates token string allocation and lowers scenario.ns_per_operation.","evidence_ids":["stk_..."]}}
```

- **Server States:**
  - Hypotheses (`revise_hypothesis`): `proposed` -> `planned` -> `testing` -> `supported` | `not_supported` | `inconclusive` | `superseded` | `abandoned`. (`supported` records statistical/evidence support, not automatic code adoption.)
  - Work items (`create_work_item` / `revise_work_item`): `todo` | `active` | `done` | `blocked` | `abandoned`.
  - Candidate metadata (`create_candidate`): `{"request":{"action":"create_candidate","name":"span-alternate-lookup","build_identity":"git-sha-or-hash","components":["Parser.ParseLine"]}}` (metadata record; pair with actual registered variant or build).
  - Baselines (`create_baseline`, `set_baseline_alias`): group completed quiescent `run_ids` for historical tracking, but **never** substitute a historical baseline for a fresh matched control arm in the same experiment.

## 6. Freezing Experiment Plans, Running & Comparing

Freeze `create_plan` (`lab.experiment`) **before** executing candidate runs:

```json
{
  "request": {
    "action": "create_plan",
    "scenario_id": "parser.bench",
    "scenario_version": "1.0",
    "input_hash": "discovered-input-hash",
    "variants": ["baseline", "span-lookup"],
    "blocks": 12,
    "operations_per_run": 2000,
    "warmup_batches": 2,
    "seed": "42",
    "profile": "quiet-validation",
    "independent_blocks": false,
    "confirmatory": false,
    "maximum_duration_seconds": 90,
    "primary_metric": "scenario.ns_per_operation",
    "guardrail_metrics": ["gc.allocated_bytes_per_operation"]
  }
}
```

- **Key Plan Parameters:**
  - `blocks`: randomized paired blocks (`>= 10` required for inferential guardrail checks, though same-process blocks remain exploratory because JIT/PGO/heap state is shared— set `independent_blocks=false` inside a single process).
  - `primary_policy` & `guardrail_policies`: set explicit `MetricPolicy` directions/thresholds whenever using non-elapsed metrics (throughput, allocations, GC overlap) or enforcing non-regression guardrails.
  - `factorial` & `analysis.family_size`: for a $2\times 2$ `FactorialDesign` (4 variants testing Factor A, Factor B, and A+B together), set `family_size` to cover all inferential contrasts (**5 pairwise contrasts + 1 interaction effect per inferential metric**). Never add separate percentage speedups to predict combined gain—measure the interaction arm directly.
  - `target_runtime_version`: optional exact runtime handshake pin (e.g., `"11.0.0-rc.2.26504.105"` or `"12.0.0-dev"`).
- **Per-Batch Telemetry Metrics** (recorded on every harness batch, outside the timed window; usable as `primary_metric` or guardrails):

  | Metric | Meaning & Availability |
  | --- | --- |
  | `cpu.thread_cycles_per_operation` | Executing-thread CPU cycles/op (Windows `QueryThreadCycleTime`). Ignores time the thread was descheduled—less noisy than wall time for pure CPU work on busy hosts. |
  | `cpu.thread_ns_per_operation` | Executing-thread CPU time/op (Linux `CLOCK_THREAD_CPUTIME_ID`). |
  | `pmu.instructions_per_operation`, `pmu.hardware_cycles_per_operation`, `pmu.branch_misses_per_operation`, `pmu.cache_misses_per_operation` | Hardware PMU counters via `perf_event_open` (**Linux x64 only**, when `perf_event_paranoid` allows it). Branch-miss and cache-miss deltas directly test `JIT-12`/`JIT-13` and `MEM-*` mechanisms. |
  | `gc.gen0_collections`, `gc.gen1_collections`, `gc.gen2_collections` | GC count deltas over the batch. |
  | `gc.heap_size_bytes`, `gc.fragmented_bytes`, `gc.committed_bytes`, `gc.pinned_objects` | `GC.GetGCMemoryInfo()` snapshot after the batch (retention/fragmentation guardrails). |

  Missing counters are never reported as zero: they come back with availability `not_supported` (OS/kernel), `not_collected`, or `not_applicable` (thread-CPU/PMU metrics when a batch hops threads, e.g. async scenarios)—check availability in `lab.compare` before relying on a platform-specific metric. Thread-CPU metrics measure only the executing thread: use wall-time `scenario.ns_per_operation` for parallel, blocking, or I/O-bound scenarios.

### Execute & Compare (`lab.run` -> `lab.compare`)

1. **Execute:**
   ```json
   {"request":{"action":"start","target_id":"tgt_...","expected_target_epoch":"0","plan_id":"pln_...","grant_id":"grt_exec","idempotency_key":"exec-plan-1"}}
   ```
   Poll `status` until terminal `Completed`; extract `result_id` (`execution_id`).
2. **Compare:**
   ```json
   {"request":{"execution_id":"exe_...","include_objectives":true,"include_factorial":false}}
   ```
   - Inspect effect direction (lower is better for `ns_per_operation`; higher is better for throughput), confidence intervals, quality flags, reason codes, and allocation deltas (distinguishing process-wide vs thread-attributed allocations and preserving zero-allocation observations).
   - Update the hypothesis via `revise_hypothesis` with the resulting `comparison_id` in `evidence_ids`.

## 7. Autonomous Scratch-Benchmark Loop (`scaffold` + `grant-scratch`)

Use a disposable scratch project to falsify a micro-mechanism quickly (e.g., "does the two-`ref` cursor drop the bounds check and win?") before touching the real application. Results are exploratory evidence; real changes still go through registered variants and [fresh normal-build confirmation](advanced-lab.md).

1. **Scaffold** (no grant needed; creates an isolated project wired to the installed harness):
   ```text
   <host> scaffold <abs-scratch-dir> [--scenario <id>] [--force]
   ```
   Output JSON includes `project_path`, `program_path`, `expected_output_dll_path`, and `build_command`. The generated `ScratchBench.csproj` ignores repository `Directory.Build.props` / central package management and enables `AllowUnsafeBlocks` + Tiered PGO. Edit `ExecuteBaseline` / `ExecuteCandidate` in `Program.cs` and keep `Validate()` as an independent oracle.
2. **Issue a directory grant:** `<host> grant-scratch <workspace> <abs-scratch-dir> <seconds> <max-ops>` → returns an `observe` + `execute_registered_scenario` grant pair. Rebuilds and new PIDs under that directory remain authorized until expiry/exhaustion.
3. **Build & start the live target:** run the `build_command`, then start `dotnet <expected_output_dll_path> <abs-workspace>` as a background process (it prints `pid`/`target_id` and stays alive until stdin closes). Re-run `lab.discover` `targets`/`scenarios`, then `create_plan` + `lab.run` `start` with the scratch `grant_id` as usual.
4. **Iterate:** stop the process, edit, rebuild, restart → new PID/target, **same grant**. Freeze a new plan whenever code changes (the variant code is part of the treatment).
5. **Clean up** the scratch directory after the target exits; keep catalog evidence.

### Owned-Process JIT Disassembly (`capture_jit_disasm`)

Launches a fresh owned process with `DOTNET_JitDisasm=<method_filter>`, runs the scenario variant through warmup so Tier 1 + Dynamic PGO code exists, and stores the full listing. Unlike live `lab.inspect` `native`, it **works for generic methods, `Span<T>`/`ref struct` signatures, `Vector128/256<T>` code, and shows the code as inlined into callers.**

```json
{"request":{"action":"capture_jit_disasm","method_filter":"ScratchScenario:Execute*","scenario_id":"scratch-bench","variant":"candidate","executable_path":"D:\\scratch\\bench\\bin\\Release\\net10.0\\ScratchBench.dll","grant_id":"grt_scratch_exec","idempotency_key":"jd-1"}}
```

- Provide **exactly one** of `executable_path` (a `.dll` strictly inside a `grant-scratch` directory, with adjacent `.runtimeconfig.json`/`.deps.json`) or `build_id` (registered normal build + `grant-build` grant). Optional: `operations` (default 1000), `warmup_batches` (5), `tiered_pgo` (`true`), `maximum_duration_seconds` (60).
- `method_filter` uses `DOTNET_JitDisasm` syntax: `Type:Method`, wildcards `*`, space-separated list; ≤ 256 printable ASCII chars. Keep it narrow—output is capped at 16 MiB.
- Poll `status` → `result_id`, then read parsed summaries and bounded assembly:
  ```json
  {"request":{"view":"jit_disasm","result_id":"jit-disasm-...","method_substring":"ExecuteCandidate","tier_filter":"Tier1","include_asm":true,"max_asm_chars":16384}}
  ```
  Each method reports `tier`, `code_size_bytes`, `stack_alloc_bytes`, `bounds_check_count` (`CORINFO_HELP_RNGCHKFAIL`), `allocation_helper_count`, `write_barrier_count`, and `simd_instruction_count`—compare baseline vs candidate numbers before spending a timed run.
- Several listings for one method are normal (Tier 0 → instrumented → Tier 1/OSR). Judge the **last Tier 1** listing.
