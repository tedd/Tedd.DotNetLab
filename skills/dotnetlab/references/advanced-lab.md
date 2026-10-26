# Advanced Lab Workflows: Confirmation, Tail Quantiles & Recovery

Reference for fresh normal-build source confirmation, individual per-operation latency & cluster-bootstrapped quantile (`p50`/`p95`/`p99`) experiments, CLI fallback execution, and catalog/artifact recovery.

## 1. Fresh Normal-Build Confirmation & Promotion

Same-process harness blocks and live CIL ReJIT trials share process history (Tiered PGO profiles, inlining decisions, heap layout). To establish **source-promotion eligibility** for a supported candidate, validate it across independent fresh processes built from normal managed IL.

### Qualification Contract (`NormalBuildService`)
- **Supported Runtimes:** Windows or Linux x64 CoreCLR **`.NET 6.0` or later** (`.NET 6`, `.NET 8`, `.NET 9`, `.NET 10`, `.NET 11` previews/RCs/GA, `.NET 12+`) targeting `Microsoft.NETCore.App` with standard `rollForward` policies (`Disable`, `Patch`, `LatestPatch`, `Minor`, `LatestMinor`, `Major`, `LatestMajor`).
- **Bundle Shape:** Flat file manifest (`3` to `64` files, `<= 128 MiB` total, no subdirectories or `..`), containing pure IL `.dll` assemblies (`CorFlags.ILOnly`, no mixed-mode/native headers or netmodules) plus **only** the entry assembly's `<Entry>.runtimeconfig.json` (`<= 1 MiB`) and `<Entry>.deps.json`.
- **Closure & Runtime Config:** Complete managed dependency closure including `Tedd.DotNetLab.Harness.dll`, `Tedd.DotNetLab.Contracts.dll`, and `Tedd.DotNetLab.Transport.Local.dll` (plus framework assemblies present in the resolved `Microsoft.NETCore.App` runtime directory). Allowed `configProperties` booleans: `System.GC.Server`, `System.GC.Concurrent`, `System.Runtime.TieredCompilation`, `QuickJit`, `QuickJitForLoops`, `TieredPGO`, `MetadataUpdater.IsSupported`, `EnableUnsafeBinaryFormatterSerialization`. Native, RID-specific, and satellite resource assets are excluded.
- **Controlled Treatment:** Baseline and candidate builds must have **distinct entry assemblies** (reflecting the isolated C# source change) while keeping all dependency DLLs and `.runtimeconfig.json` settings identical. (Runtime upgrades, GC config changes, Native AOT, or R2R comparisons require an external benchmark rig rather than entry-assembly confirmation.)

### Step-by-Step Confirmation Workflow

1. **Import Artifacts (CLI):** Build baseline and candidate Release outputs into clean staging directories under the designated workspace/scratch root, plus a unified diff patch file. Import all files into the workspace catalog (shared `connect` clients can remain connected; dedicated `serve` must release the workspace):
   ```text
   <host> import <workspace> <path-to-file.dll> application/vnd.microsoft.portable-executable
   <host> import <workspace> <path-to-file.json> application/json
   <host> import <workspace> <path-to-diff.patch> text/plain
   ```
2. **Register Normal Builds (`lab.experiment`):**
   ```json
   {"request":{"action":"register_normal_build","entry_assembly":"MyBench.dll","files":[{"name":"MyBench.dll","artifact_id":"art_..."},{"name":"MyBench.deps.json","artifact_id":"art_..."},{"name":"MyBench.runtimeconfig.json","artifact_id":"art_..."},{"name":"Tedd.DotNetLab.Harness.dll","artifact_id":"art_..."},{"name":"Tedd.DotNetLab.Contracts.dll","artifact_id":"art_..."},{"name":"Tedd.DotNetLab.Transport.Local.dll","artifact_id":"art_..."}]}}
   ```
   *(Optional JIT Inspection)*: To inspect emitted Tier 1 x64 assembly of a registered build before running confirmation blocks, issue `<host> grant-build <workspace> <build-id> <seconds> <max-ops>` and launch `capture_jit_disasm` with `build_id`.
3. **Freeze Confirmation Plan (`lab.experiment`):**
   Call `create_confirmation_plan` with `selected_candidate_id`, `source_patch_artifact_id`, `source_equivalence_rationale`, `baseline_build_id`, `candidate_build_id`, `baseline_variant`, `candidate_variant`, `exploratory_evidence_ids`, and nested `experiment` (`fresh_normal_build=true`, `confirmatory=true`, `independent_blocks=true`, `profile="quiet-validation"`, `blocks >= 10`, frozen `primary_policy` and `guardrail_policies`, and **no** `compilation_control_id`).
4. **Confirmation Grant & Launch:**
   Verify startup/cleanup have no unauthorized external/DB side effects, then issue the grant within the authorized task (obtain approval first if the user requires manual approval):
   ```text
   <host> grant-confirmation <workspace> <confirmation-plan-id> <seconds> <max-ops>
   ```
   Call `lab.run`:
   ```json
   {"request":{"action":"start_confirmation","plan_id":"cpl_...","grant_id":"grt_conf","idempotency_key":"confirm-1"}}
   ```
   Poll `status` until all fresh baseline/candidate processes join and complete validation.
5. **Evaluate & Record Decision:**
   - Call `lab.compare`: `{"request":{"execution_id":"exe_...","normal_build_confirmation":true,"include_objectives":true}}`.
   - Promotion eligibility requires a **useful primary improvement** AND passing **every mandatory guardrail**.
   - Record manual adoption or rejection via `lab.experiment`:
     `{"request":{"action":"record_decision","comparison_id":"cmp_...","decision":"adopt","rationale":"..."}}`. (Never relabel a failed performance gate as a "simplification win" inside the performance verdict; record pure simplifications separately.)

## 2. Individual Operation Latency & Tail Quantiles (`p50` / `p95` / `p99`)

Aggregate batch means (`ExecuteBatch(N)`) hide tail spikes and cannot establish `p95`/`p99` request or frame latency. Use individual-latency plans and paired cluster-bootstrapped quantiles when tail behavior is the objective or guardrail.

### Per-Operation Timing (`individual_latencies`)
1. Freeze `lab.experiment` `create_individual_latency_plan` (`scenario_id`, `scenario_version`, `input_hash`, `variant`, `operations` `<= 10000`, `warmup_batches`, optional `target_runtime_version`).
2. Run `lab.run` `individual_latencies` (`target_id`, `expected_target_epoch`, `plan_id`, `grant_id`, `idempotency_key`) -> poll `status`. Each observation calls `ExecuteBatch(1)` / `ExecuteBatchAsync(1)` and records elapsed duration (including timer/await boundary overhead).
3. Read bounded slices via `lab.query`:
   ```json
   {"request":{"view":"individual_latency_slice","run_id":"run_...","offset":0,"count":1000}}
   ```
   Limits: **`<= 10,000` observations per run**, **`<= 1,000` per slice**.

### Paired Cluster-Bootstrapped Quantiles (`compare_quantiles`)
1. **Freeze Before Collection (`create_quantile_plan`):** Prespecify the `QuantilePlanRequest` binding both individual arm plans, paired cluster/session count (`>= 10` independent process clusters required for inferential claims), per-arm observation count, target quantile (e.g., `0.99`), bootstrap resamples (default `10,000`; `<= 100,000` total observations; `<= 50,000,000` observation-resample ops), `seed`, independence justification, and absolute thresholds.
2. **Tail Sample Budget Rule:** Ensure `>= 5` expected observations in **each tail** per cluster/pool (e.g., `p99` requires substantially more observations than `p50`).
3. **Execute & Register All Pairs:** Run every declared cluster in the returned randomized arm order (one process session per cluster; matching epoch/process within a paired block, distinct PIDs across clusters; no native-profiler taint). Register **every** run via `register_quantile_harness_runs` (never drop slow sessions or substitute failed slots) -> `lab.run` `compare_quantiles` (`dataset_id`, `idempotency_key`).
4. **Interpretation:** Whole paired clusters are resampled together to preserve within-session correlation. Fewer than 10 independent clusters yields descriptive-only output. Quantile comparisons remain exploratory with respect to `normal_build_confirmation` promotion gates.

## 3. CLI Driver Fallback (`dotnetlab call`)

When MCP stdio is unavailable or an outer host lock must wrap a multi-step measurement script, invoke `LabTools` directly over CLI stdin. **Note:** CLI stdin takes the **inner** JSON object directly (do **not** wrap in `{"request":...}`):

```powershell
'{"action":"profiles"}' | dotnet $HostDll call $Workspace lab.discover
```

CLI `call` enforces the exact same scoped grants, schemas, idempotency keys, and safety contracts as MCP.

## 4. Error Handling, Idempotency & Catalog Maintenance

- **`catalog_busy`:** Transient SQLite read/status contention. Wait briefly and retry **only that `status` or `query` read**—never restart a timed workload or expand run count.
- **Lost Response / Timeout:** Transport timeout does **not** prove non-submission. Query `lab.query` `{"request":{"view":"history","kind":"operation","limit":20}}` or `operation_policy` before replaying; intentional replay must reuse the exact `idempotency_key` and identical payload.
- **Target Restart / Epoch Drift:** Any target restart or rebuild changes PID or increments epoch; re-run `lab.discover` (`targets`, `scenarios`) and bind fresh plans/grants.
- **Catalog & Artifact Lifecycle:** `pin_evidence` protects critical artifacts; `backup_catalog`, `checkpoint_catalog`, `recover_artifacts`, and grant-authorized `prepare_export`/`start_export`, `prepare_deletion`/`start_deletion`, and `prepare_reference_release`/`release_references` are explicit administrative actions—never delete catalog files manually from disk.
