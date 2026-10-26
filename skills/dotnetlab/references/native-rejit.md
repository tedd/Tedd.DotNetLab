# Native Code Inspection, Profiler Attach, Probes & Live CIL ReJIT

Reference for verifying actual CoreCLR Tier 1 x64 machine code (`lab.inspect`), attaching the native `ICorProfilerInfo12` profiler on Windows, installing bounded method probes, and running controlled live CIL ReJIT experiments (`lab.patch`).

## 1. Qualified Scope & Capability Check

Before attempting live native inspection or `lab.patch`, query `lab.discover` (`capabilities`, `compatibility`, and `profiler` with `target_id`) and read the returned qualification reasons.
- **Supported Runtimes:** Windows and Linux x64 CoreCLR **`.NET 6.0` or later** for profiler-backed native inspection, startup probes, and ReJIT (`lab.patch` requires launching `connect`/`serve` with `--enable-native-extension`). Live profiler attachment (`attach_profiler`) is supported on **Windows x64 CoreCLR `.NET 6.0+`** (Linux targets load the profiler at process startup when configured).
- **Observer Caveat:** Loading a CLR profiler alters JIT/runtime observer state for the remainder of that process's lifetime (`quiet-validation` omits EventPipe providers, **not** an attached CLR profiler). Never treat a profiler-attached process as a pristine unprofiled baseline.

## 2. Inspecting Emitted Machine Code & IL (`lab.inspect`)

Never assume C# source or IL proves bounds-check removal, inlining, devirtualization, `CMOV` selection, or SIMD vectorization—verify the emitted Tier 1 x64 assembly!

### Stored / Offline Inspection (No Live Grant Required)
- List methods in a registered PE artifact:
  `{"request":{"action":"methods","artifact_id":"art_...","filter":"HotMethod","limit":50}}`
- Read IL bytes/instructions by MethodDef token:
  `{"request":{"action":"il","artifact_id":"art_...","method_token":"0x06000012"}}`
- Read or diff previously captured native disassembly artifacts:
  `{"request":{"action":"offline_native","artifact_id":"art_..."}}`
  `{"request":{"action":"native_diff","original_artifact_id":"art_orig","candidate_artifact_id":"art_cand","limit":100}}`

### Owned-Process JIT Disassembly (`capture_jit_disasm` — Recommended Primary Path)
For modern C# codebases using **generic methods, `Span<T>`, `readonly ref struct`, SIMD vectors (`Vector256<T>`), or inlined callers**, prefer `capture_jit_disasm` over profiler-based inspection:
- **No Native Profiler Required:** Works across Windows and Linux x64 on registered normal builds (`build_id` + `grant-build`) or scratch builds (`executable_path` + `grant-scratch`).
- **PGO & Inlining Faithful:** Warms up the scenario under `DOTNET_TieredPGO=1` and captures the actual Tier 1 assembly with caller inlining.
- **Automated Codegen Metrics:** Reports `code_size_bytes`, `stack_alloc_bytes`, `bounds_check_count` (`CORINFO_HELP_RNGCHKFAIL`), `allocation_helper_count`, `write_barrier_count`, and `simd_instruction_count` queryable via `lab.query` `jit_disasm`. See [MCP Loop §7](mcp-loop.md#owned-process-jit-disassembly-capture_jit_disasm).

### Live Process Native Disassembly (`inspect_live_code`)
1. **Qualification:** Target must have the native profiler active (`lab.discover` `profiler`), and the target method must be a **compiled non-generic method in a non-dynamic, non-collectible module** (verify module MVID, MethodDef token, and `module_load_id`). If the hot method is inlined into its caller, inspect the **optimized caller**!
2. **Grant & Inspect:** Stop/idle owned controller -> human issues:
   ```text
   <host> grant <workspace> <pid> inspect_live_code <seconds> <max-ops>
   ```
   Reconnect and call `lab.inspect`:
   ```json
   {"request":{"action":"native","target_id":"tgt_...","method_id":"mtd_...","expected_target_epoch":"0","grant_id":"grt_inspect","module_load_id":"mod_..."}}
   ```
3. **What to Check in x64 Output:**
   - Surviving range checks (`cmp` + `jae CORINFO_HELP_RNGCHKFAIL`), sign-extension (`movsxd` on `int` index), indirect/virtual calls vs inlined bodies, stack spills (`[rsp+...]`), scalar vs `xmm`/`ymm`/`zmm` registers, cross-lane shuffles, `vzeroupper`, and loop recurrence chains.
   - Note: Snapshot tier may report `unknown`, and inspecting an uncompiled method does **not** authorize invoking arbitrary target code just to trigger JIT compilation.

## 3. Windows Live Profiler Attachment (`attach_profiler`)

Only attach the profiler when live disassembly or ReJIT is explicitly needed and the target lacks startup profiler registration:
1. `lab.experiment` `{"request":{"action":"prepare_profiler_attachment","target_id":"tgt_...","expected_target_epoch":"0"}}` -> freezes target identity, runtime hash, and verified `Tedd.DotNetLab.Profiler.dll` hash into `plan_id`.
2. Stop/idle owned controller -> human runs:
   `<host> grant-profiler-attach <workspace> <attachment-plan-id> <seconds> 1`
3. Reconnect -> `lab.run` `{"request":{"action":"attach_profiler","plan_id":"att_...","grant_id":"grt_att","idempotency_key":"att-1"}}`.
4. If attachment outcome is uncertain (timeout/transport drop), call `reconcile_profiler_attachment` (`operation_id`, `grant_id`)—**never** blindly issue a second load.

## 4. Bounded Method Probes & Live CIL ReJIT (`lab.patch`)

Live CIL mutation is strictly for **rapid mechanism falsification on disposable targets** with a verified `Tedd.DotNetLab.Harness` correctness/quiescence oracle:
- **Qualified Method Shape for CIL Replacement:** Static method returning `Int32` with **`<= 4` `Int32` parameters** and bounded verified CIL body (`<= 4 KiB`), registered against a prior live `inspection_id` and `base_il_hash`. Generic methods, async state machines, struct/span signatures, and exception/P-Invoke heavy bodies are **not** ReJIT candidates—test those via normal harness variants!

### Workflow Stages

| Stage | Calls & Grants | Key Rules |
| --- | --- | --- |
| **1. Method Probe** | `lab.patch` `prepare_probe` (`inspection_id`, `base_il_hash`, `timing`, `threads`, `maximum_nesting`, `track_exceptions`) -> `register_cil_candidate` -> human `grant-cil` -> `apply` -> run with `profile="targeted-probes"` -> `flush_probes` -> `restore_original`. | Counts/inclusive wall times cover the delimited probe window **including warm-up**. Always flush and restore before any quiet timing. |
| **2. Register CIL Candidate** | Import replacement CIL body (`application/octet-stream`) -> `lab.experiment` `register_cil_candidate` (`body_artifact_id`, `inspection_id`, `base_il_hash`, `components`) -> human `<host> grant-cil <workspace> <cil-candidate-id> <seconds> <max-ops>`. | Run `lab.patch` `preflight` (`target_id`, `expected_target_epoch`, `candidate_id`, `grant_id`) before applying. |
| **3. A/A Compilation Control** | `lab.patch` `prepare_original_control` (`inspection_id`) -> `lab.experiment` `create_compilation_control` -> `lab.patch` `run_compilation_control` (`plan_id`, `mutation_grant_id`, `execution_grant_id`, `inspection_grant_id`, `idempotency_key`). | Mandatory before ReJIT A/B timing: proves ReJITing the original IL body reproduces baseline behavior/cost under identical warmup and callers. |
| **4. Matched Native Trial** | `lab.experiment` `create_native_trial` (`candidate_id`, `compilation_control_id`, `blocks`, `seed`) -> `lab.patch` `run_native_trial` (`plan_id`, `original_grant_id`, `candidate_grant_id`, `execution_grant_id`, `inspection_grant_id`, `idempotency_key`) -> verify via `lab.query` `verify_native_trial`. | Runs randomized matched blocks of original-ReJIT vs candidate-ReJIT and restores the original body upon completion. |
| **5. Multi-Session Series** | Freeze `create_native_series` (`trial_plan_ids` across distinct disposable process sessions) -> run each reserved trial -> `lab.run` `register_native_series_results` (**all** slots, no cherry-picking) -> `compare_native_series` -> `lab.query` `native_series_comparison`. | Even after `restore_original`, PGO/inlining history persists; confirm winning mechanisms via [fresh normal builds](advanced-lab.md). |

## 5. Interrupted Mutation & Taint Recovery

- Retain every `transaction_id`, `operation_id`, and `idempotency_key`. If a timeout or disconnect occurs during `apply`, `restore_original`, or `run_native_trial`, **inspect durable operation/record state first**—never assume rollback occurred or run timing on a potentially mutated target.
- Only call `lab.patch` `recover_interrupted` (`transaction_id`, `expected_target_epoch`, `grant_id`, `execution_grant_id`, `idempotency_key`) when qualified recovery evidence and human grants match; otherwise preserve workspace evidence and terminate/replace the disposable target process cleanly.
