# Low-Level Software Optimization: A Microarchitecture-Aware Registry

> Exported as a standalone Markdown research reference, 14 October 2026. This is a **structured Markdown edition based on the completed Deep Research report** and the nine supplied reference documents, not a byte-for-byte export of the research-panel text. Research-panel citation tokens have been replaced with direct documentary references wherever possible. The supplied reference documents are reproduced in full in the appendices.

## Executive summary

Optimization is **measured bottleneck removal**, not an arbitrary application of tricks. A productive default sequence is **remove work → improve representation and locality → expose compiler knowledge → remove dependencies and unpredictable branches → vectorize → parallelize or offload → hand-tune instructions only with evidence**. Reprofile after each material win: the system bottleneck can move.

Two limits govern expectations. In the Amdahl model, improving a fraction *f* of runtime by a factor *s* yields `speedup = 1 / ((1-f) + f/s)` when the model's assumptions hold. In the Roofline model, attainable computational throughput is bounded by `min(peak compute, sustainable bandwidth × arithmetic intensity)`. SIMD does not, by itself, accelerate an already bandwidth-saturated scan.

**Evidence standard:** Each candidate should specify a code/profile smell, limiting resource, exact transformation, invariants and portability restrictions, a falsifiable measurement, and a disposition. Record setup, retained memory, maintenance, synchronization, and consumer costs—not only the isolated kernel time.

**Potential** in this document means the magnitude of a possible improvement **if the mechanism is the measured bottleneck**: *very high* for eliminating entire classes of work/traffic, *high* for materially removing a constraint, *medium* for a conditional/local benefit, and *architecture-specific* for effects requiring exact hardware confirmation. These are not speedup promises.

## 1. Architecture and performance model

| Execution family | Key strengths | Distinct hazards |
|---|---|---|
| x86 SSE/SSE2–SSE4 | 128-bit vectors, mature instruction support | ISA-specific instruction availability and historical AVX/SSE transition effects |
| x86 AVX | VEX encoding, 256-bit floating-point forms | AVX is not AVX2; large width can raise power/thermal demand |
| x86 AVX2 | 256-bit integer and floating-point SIMD, useful shuffles/comparisons | 128-bit shuffle lane boundaries, gathers, tails, register pressure |
| x86 AVX-512 | Mask registers, predication, wider vector forms, specialised instruction subsets | Multiple independent feature bits; ZMM use and frequency tradeoffs |
| Arm NEON/AdvSimd | Fixed 128-bit AArch64 SIMD | Different type/instruction semantics; explicit tails |
| Arm SVE/SVE2 | Scalable vectors with predicate-based tail handling | Code must remain vector-length agnostic; feature-dependent subsets |
| NVIDIA CUDA | SIMT, massive thread parallelism, shared memory and tensor units | Divergence, occupancy/resource pressure, uncoalesced global loads, transfer overhead |
| AMD HIP/ROCm | Wavefront execution, local data share, architecture-specific kernels | Bank conflicts, wave resource limits, transfer/pipeline overhead |

**Bottleneck classification:** (1) excessive useful and redundant work; (2) front-end instruction-cache/decoder/uop-cache/bad speculation; (3) back-end execution ports, dependencies and register pressure; (4) caches, DRAM, memory bandwidth, page translation, NUMA; (5) sharing, synchronization and scheduler delays; (6) I/O, network, GPU upload/download and external-service latency.

## 2. Master optimization registry

The registry below is a set of **testable hypotheses**. The location, demand pattern and compiler code actually observed determine which entries apply. For deeper .NET-specific recipes and correctness requirements, consult the complete original source files in the appendices.

### A. Eliminate work, choose algorithms and representations

| ID | Look for / diagnostic clue | Candidate mechanism and concrete experiment | When it can lose / required proof |
|---|---|---|---|
| A01 | Quadratic nested loops, repeated rescans | Replace repeated O(n²) search with a hash/index/sort-and-sweep algorithm | Build cost, ordering, key semantics, adversarial inputs |
| A02 | Repeated parsing/normalization | Parse once; cache canonical representation or prepared operation | Invalidation, memory retention, one-shot requests |
| A03 | Repeated invariant calculations | Hoist loop/call invariants or cache computed metadata | Concurrency, mutable operands, aliasing |
| A04 | Fully recomputed aggregate after small changes | Maintain incremental aggregates/delta updates | Update fanout, invalidation, missed dependencies |
| A05 | Dense all-pairs work with sparse actual interactions | Sparse adjacency, spatial partitioning, inverted indexes | Sparsity changes, index construction and maintenance |
| A06 | Full sort to get a handful of results | Selection, heap/top-k, partial sort | Tie/order semantics, input distributions |
| A07 | Sorting already partly ordered data | Natural-run/adaptive sorting or incremental insertion | Mutation rate, required stability |
| A08 | Linear search repeated over read-mostly set | Direct array, sorted table, bitset, hash table, frozen dictionary | Domain density, construction, misses, memory |
| A09 | General path used for common fixed cases | Specialize by type, size, shape, mode; retain generic fallback | Code growth/I-cache, maintenance, uncommon cases |
| A10 | Repeated indirect type/interface lookup | Cache typed delegate, resolve handle/location once | Stale references, code size, mutation |
| A11 | Unneeded intermediate object/array | Stream or fuse producer→consumer work | Exception semantics, buffer ownership, backpressure |
| A12 | Repeated deserialization/format conversion | Keep stable canonical binary representation across pipeline | Interoperability, versioning, memory cost |
| A13 | Sparse predicates over large population | Iterate only active/sparse IDs or set bits | Active-set maintenance, changing density |
| A14 | Read-mostly database/index lookups | Materialize or precompute proven repeated results | Refresh and consistency cost |
| A15 | Repeated regex/automaton setup | Compile/cache patterns or parse tables | Cold start, memory, pathological patterns |
| A16 | Entire input scanned to test early satisfiability | Early exit, short-circuit ordering by selectivity/cost | Side effects, stability, adversarial order |

### M. Memory layout, allocation and locality

| ID | Look for / diagnostic clue | Candidate mechanism and concrete experiment | When it can lose / required proof |
|---|---|---|---|
| M01 | `new[]` then overwrite, repeated Gen0/GC | Reuse exclusive scratch, pool or uninitialized primitive storage | Initialisation, escape paths, clear/return, retention |
| M02 | Tiny bounded heap scratch | `stackalloc` / stack spans with capped fallback | Stack depth, uninitialized bytes, lifetime |
| M03 | `List`/dictionary repeatedly rehashes/grows | Pre-size or reserve capacity | Memory footprint, unpredictable workloads |
| M04 | Repeated copies, `ToArray`, transcoding | Views (`Span`, `Memory`), direct writer/consumer | Aliasing, lifetime, ownership |
| M05 | Pointer-chasing linked structures | Contiguous arrays or compact vectors | Insertions/removals, reference stability |
| M06 | Streaming reads one field of large records | AoS→SoA and hot/cold splitting | Whole-record random access |
| M07 | Mixed record and lane access | AoSoA / blocked column groups | Index arithmetic, fragmented tails |
| M08 | Inner loop follows wrong memory dimension | Loop interchange, row-major traversal | Loop-carried dependencies and output order |
| M09 | Cache-thrashing large reuse-rich nest | Cache tile/block; sweep dimensions | Halo overhead, cache associativity, tails |
| M10 | Huge working set with DTLB misses | Compact layout, huge pages/THP if beneficial | Compaction, fragmentation, OS policies |
| M11 | NUMA remote memory stalls | First-touch local initialization, sharding, thread/node placement | Migration, load imbalance, data sharing |
| M12 | Line-crossing hot accesses | Align/cap layout using actual allocated base | Padding grows data and may hurt TLB |
| M13 | Known future indirect load | Sweep explicit software prefetch distances | Added µops, pollution; prove valid address/lifetime |
| M14 | Simple sequential pattern | Prefer hardware-prefetch-friendly ordering | Algorithm may require random access |
| M15 | Width-mismatched load after store | Match store/load width or use register | Store-forwarding failure is microarchitecture-specific |
| M16 | False memory alias from store/load streams | Offset buffers/strides after 4 KiB-alias evidence | Additional layout footprint; CPU-specific |
| M17 | Many byte stores to fixed packed record | Pack `uint32_t` / `uint64_t` and use full-width store | Endianness, padding, tails, buffer capacity |
| M18 | Scalar memcopy/fill loop | Compare tuned `memcpy`/`memmove`, `CopyTo` first | Overlap/length semantics, size-dependent crossover |
| M19 | Large write-only output | Experiment with non-temporal stores and fences | Immediate consumer reuse, alignment, threshold |
| M20 | Serial preparation + parallel consumption | Parallel first-touch allocation layout | Locality and scheduler/NUMA interactions |
| M21 | Wide indexes/pointers inflate working set | Bounded 32-bit offsets/IDs and generation handles | Overflow, ID reuse, stale-handle validation |
| M22 | Random access loads many scattered pages | Cluster keys/objects and reduce live working set | Reordering, mutation and indirection costs |
| M23 | Repeated region-wide heap allocation | Arena/region allocator for common lifetime | Peak retention, destructor/escape semantics |
| M24 | Hot managed loops still have range guards | Bounded slice; validate highest index; inspect Tier1 | Exception/order contract, JIT already eliminates checks |
| M25 | Many repeated managed struct copies | Evaluate `in`, `ref readonly`, readonly members | Extra indirection; small by-value faster sometimes |

### C. CPU core, branches and instruction selection

| ID | Look for / diagnostic clue | Candidate mechanism and concrete experiment | When it can lose / required proof |
|---|---|---|---|
| C01 | Scalar reduction recurrence | Multiple independent accumulators | Floating-point reassociation; spills |
| C02 | Serial load→compute dependence | Software pipeline/interleave independent iterations | Register pressure, speculative wasted work |
| C03 | Short loop with high overhead | Controlled loop unroll factor | I-cache/µop cache, code-size growth |
| C04 | Branch based on invariant mode inside loop | Loop unswitch into separate loop bodies | Mode stability; code duplication |
| C05 | Cheap unpredictable predicate | Compare `if` to `cmov`, masks, conditional select | Dependency extension, eager unsafe work |
| C06 | Strongly skewed tagged data | Common-case fall-through; PGO layout | Uniform/adversarial tags, compiler reordering |
| C07 | Many opcode comparisons | `switch`, jump table, cached dispatch | Indirect target prediction and lost inlining |
| C08 | Late condition calculation | Move branch resolution earlier | Speculative effort may grow |
| C09 | Front-end stalls in oversized function | Reduce inlining/unrolling; hot/cold split; PGO/BOLT | Branch/layout/compiler dependent |
| C10 | Inefficient compare/test + branch | Review macro-fusion-eligible code | CPU-specific fusion pairs; compiler may do this |
| C11 | Redundant division/remainder | Constant division strength reduction; multiply-high/shifts | Signedness, overflow, already optimized compiler |
| C12 | Manual bit scanning | POPCNT, TZCNT, LZCNT, `BitOperations` | Zero-input semantics, precise ISA support |
| C13 | Repeated rotations/endian swaps | ROL/ROR, BSWAP, `BinaryPrimitives` | Type width and byte order |
| C14 | Bit field extraction/deposit loops | BMI1/BMI2 `BEXTR`, `PEXT`, `PDEP` | Latency/throughput varies widely by CPU |
| C15 | Arithmetic followed by add | FMA only with permitted single-rounding contract | Different FP result, NaN, signed zero |
| C16 | Nonzero subnormal FP behavior | Evaluate FTZ/DAZ only if permitted | Subnormal and zero semantics change |
| C17 | Repeated exact divide/sqrt in hot FP path | Approximate reciprocal/rsqrt + refinement | Explicit error tolerance and edge cases |
| C18 | Calls obscure callee/type facts | Devirtualization, concrete call, selective inlining | Code bloat, startup/PGO tradeoffs |
| C19 | Unpredictable direct branch order | Gather branch frequency *and transitions*, test order | Source syntax not identical to assembly |
| C20 | 4/8-way operations dominated by latency | Draw dependency DAG and schedule independent chains | Port/load bottlenecks remain |
| C21 | Partial-register/flag dependencies | Inspect byte/word writes and later wider reads | Historical issue; never generalize without target proof |
| C22 | µop-port bottleneck | Replace instruction mix only after port/counter evidence | Backend bottleneck may shift to front-end |
| C23 | Loads wait on stores | Avoid restrictive store-forwarding pattern | Cannot violate object bounds/aliasing |
| C24 | AVX/legacy-SSE mixing | Check applicable boundary and `VZEROUPPER` codegen | Generation-specific transition behavior |
| C25 | High CPU frequency drift under heavy vectors | Benchmark narrower vector width and power states | Frequency policies/platform workloads vary |

### V. SIMD and ISA specialization

| ID | Look for / diagnostic clue | Candidate mechanism and concrete experiment | When it can lose / required proof |
|---|---|---|---|
| V01 | Lane-independent scalar arithmetic | Compiler auto-vectorization; portable `Vector<T>` or explicit intrinsics | Compiler may already vectorize; tails |
| V02 | 128/256/512-bit candidate | Compare widths as independent implementations | Downclock, lane shuffles, spills, bandwidth |
| V03 | Bulk equality and filtering | SIMD compare + mask extraction + set-bit scan | Bit per byte vs bit per logical lane confusion |
| V04 | Vector body plus scalar tail | Scalar epilogue, smaller width, masked AVX-512 or SVE predication | Valid masked load semantics; no illegal overread |
| V05 | Repeated byte rearrangement | `VPSHUFB`, `VPERM`, unpack/interleave/transpose | AVX2 shuffles may be 128-bit lane-local |
| V06 | Scalar horizontal reduction overhead | Vector accumulators + optimal horizontal combine | FP order differs, reduction overhead dominates tiny n |
| V07 | Indirect but parallel indices | AVX2/AVX-512 gather and available scatter | Random latency/poor locality often still dominates |
| V08 | Fixed packed integer operations | Saturating arithmetic, min/max, widen/narrow | Signedness, saturation vs wrap |
| V09 | Sparse matching output | SIMD mask and output compaction | AVX2 compaction shuffle cost; AVX-512 compress subsets |
| V10 | Bulk flags/bitmaps | SIMD AND, XOR, AND-NOT, comparisons | Final-word tail bits and true sharing |
| V11 | Short strings/CSV/JSON scan | Byte class comparisons, masks, delimiter detection | Escape/UTF-8/error semantics |
| V12 | Cyclic polynomials/CRC/GF fields | Carry-less multiply, CRC ISA, GFNI when supported | Polynomial conventions, endianness, fallback |
| V13 | Crypto processing | Hardware AES/SHA and proven crypto libraries | Constant-time behavior and security requirements |
| V14 | Quantized dot/matrix kernels | Dot-product/VNNI/AMX or vendor library kernels | Overflow, precision, packing, permission/state handling |
| V15 | Known target CPU fleet | Function multiversioning with runtime feature detection | Feature subsets, dispatch overhead, binary size |
| V16 | Arm width-independent loop | SVE `whilelt` predicate, `svcnt*` increments | Feature support, scalable-vector ABI |
| V17 | Scalar fallback rarely benchmarked | Explicitly time scalar/SSE/NEON paths | Correctness and portability |
| V18 | Explicit AVX2 integer intrinsics | Guard `Avx2.IsSupported`, not just 256-bit vector acceleration | Unsupported ISA faults, missing OS support |

### P. Parallelism and coordination

| ID | Look for / diagnostic clue | Candidate mechanism and concrete experiment | When it can lose / required proof |
|---|---|---|---|
| P01 | Thread speedup collapses as writers grow | Remove false sharing; local state, aligned/padded output | Alignment, enlarged memory; true sharing not solved |
| P02 | Central lock on independent keys | Sharding/lock striping, disjoint partitions | Cross-shard ordering and deadlock |
| P03 | High lock waits on tiny operations | Batch work under one lock; reduce scope | Publication latency and starvation |
| P04 | Spin consumes CPU under load | Bounded spin-then-block, `SpinWait`, adaptive policies | Oversubscription and preempted owner |
| P05 | Excess tasks or queue handoffs | Coarser contiguous partitions and local batches | Imbalance, long-tail workers |
| P06 | Shared reduction or counter | Per-worker accumulators then controlled merge | Extra merge and visibility delays |
| P07 | Immutable reader snapshots useful | Copy-on-write / snapshot publication | Reclamation and old-reader retention |
| P08 | Lock-free proposal | CAS state machine, linearization, ABA/reclamation proof | Stress success alone does not prove correctness |
| P09 | CPU partition count assumed equal logical processors | Sweep 1 to physical cores to SMT + oversubscription | Bandwidth contention and co-tenant disruption |
| P10 | Data-parallel loop needs ordered output | Stable partition+merge scheme | Scheduling overhead and determinism |
| P11 | Work items have unequal cost | Work stealing or skew-aware dynamic partition | More scheduling and cache movement |
| P12 | Request queue unbounded | Bounded channels/queues and backpressure | Admission/rejection behavior and tail latency |

### G. GPU and heterogeneous acceleration

| ID | Look for / diagnostic clue | Candidate mechanism and concrete experiment | When it can lose / required proof |
|---|---|---|---|
| G01 | Warp/wave reads strided fields | Coalesced lane-contiguous SoA/global access | Record-centric use may suffer |
| G02 | Repeatedly reading shared tile from DRAM | Shared-memory/LDS tiling with reuse | Capacity, barriers, bank conflicts |
| G03 | Shared-memory bank conflicts | Padding/remapping shared tile stride | Occupancy/shared-memory footprint |
| G04 | Low useful issue due to resource limit | Tune blocks/warps, registers, shared memory | Occupancy not same as utilization |
| G05 | Excess per-thread registers / spill | Reduce live ranges or restructure kernel | More recomputation, less ILP |
| G06 | Divergent lanes | Group/partition similar predicates | Sorting or grouping cost; required ordering |
| G07 | Atomics to one address | Warp/block-local aggregation then one atomic | Publication/order semantics |
| G08 | Many tiny kernel launches | Kernel fusion, launch batching, device graphs | Register use, code size, flexibility |
| G09 | Many host↔device copies | Keep persistent device-resident data | Device memory and coherence |
| G10 | Transfers serial with independent work | Streams, async transfer and pinned host buffers | Actual copy engines, synchronization, pin budget |
| G11 | Poor math-unit utilization | Vendor BLAS/tensor cores/matrix instruction backend | Precision/layout, input shape restrictions |
| G12 | GPU kernel faster but app slower | Include launch, upload, download and CPU orchestration | End-to-end acceleration not established |

### R. Compiler, runtime and code generation

| ID | Look for / diagnostic clue | Candidate mechanism and concrete experiment | When it can lose / required proof |
|---|---|---|---|
| R01 | Debug/tier-0 benchmark | Release, optimized steady-state vs startup | Real deployment tier/settings must match |
| R02 | Cross-module small calls block optimization | LTO/ThinLTO, native whole-program optimization | Build/link time, code size |
| R03 | Representative branch/type skew | Instrumented/sample PGO, dynamic PGO on .NET | Bad training sets can regress rare workloads |
| R04 | Native hot-code layout poor | PGO and post-link layout optimizer (e.g., BOLT) | Toolchain/platform availability, profile drift |
| R05 | Compiler misses obvious vector loop | Loop remarks, alias/range/alignment facts | Facts must be semantically valid |
| R06 | Alias checks inhibit vectorization | `restrict` / `__restrict` only under proven noalias | Undefined behavior on violated promise |
| R07 | Dynamic ISA choice in every iteration | Resolve feature/dispatch once per operation | CPU compatibility and configuration stability |
| R08 | Stable feature switch checked per operation | Startup-frozen immutable field; verify JIT elimination | Not appropriate for runtime toggle semantics |
| R09 | Reflection discovery on hot path | Cache `MethodInfo.CreateDelegate` typed delegates | Initialization cost, lifetime |
| R10 | Opaque virtual/interface method | Devirtualize/guarded direct type specialization | Polymorphic workloads and code bloat |
| R11 | Excessive inlining or loop unrolling | Selective limits to restore I-cache locality | May lose constant propagation |
| R12 | Handwritten assembly without proven need | Intrinsics or optimized library instead | ABI/clobber/optimizer visibility hazards |
| R13 | JIT range checks materially survive | Validate safe bounded span; possibly managed byref | Range/lifetime proof and exceptions |
| R14 | Managed GC moving unsafe memory | Pin for all raw pointer usage or retain managed byrefs | Pin fragmentation and object relocation |

## 3. Detailed technique notes

### SIMD lanes, masks, shuffles and tails

**AVX** introduced 256-bit floating-point forms; **AVX2** expands 256-bit integer SIMD. **AVX-512** is a collection of features, often with predicated operations and lane masking. A 256-bit intrinsic call cannot be justified by `Vector256.IsHardwareAccelerated` alone: guard the exact ISA operation. In AVX2, some shuffles are constrained to 128-bit halves. A byte `MoveMask` produces one bit per byte; `PopCount` of such a mask is not automatically an entity count. Explicit SIMD must never load past an array's *logical* end merely because bytes might be mapped.

**Comparison experiment:** scalar library method → compiler auto-vectorization → portable vectors → 128-bit intrinsic → 256-bit intrinsic → 512-bit/predicated path. Check instruction sequence, load/store count, masking, cross-lane shuffle cost, register spills, tails, input sizes from zero to beyond LLC, power/frequency effects, and overall caller time.

### Independent chains and execution ports

For an instruction latency `L`, reciprocal throughput `R` and `K` independent chains, a useful idealised recurrence bound is `cycles/instruction ≥ max(R, L/K)`, and roughly `ceil(L/R)` independent chains can hide the latency. This does **not** model load latency, register pressure, cache stalls, execution-port competition or branch misses. Draw a data-dependency graph before unrolling. Multiple accumulators can improve an FP reduction but generally change summation order and bits.

### Branches and conditional moves

A 50% taken branch is not necessarily 50% mispredicted: alternating patterns may be learned while random data are difficult. Compare `if`, conditional selection, arithmetic masks, grouping, and unswitching on real and adversarial distributions. `CMOV` introduces dependencies; branchless code may compute both sides and can change exceptions or side effects. Source `?:` does not guarantee `CMOV` machine code; inspect assembly.

### Store forwarding, 4 KiB aliasing and line/page splits

Store forwarding may be inhibited by an overlapping load of a different size/offset than a preceding store. Some x86 microarchitectures can also conservatively delay loads behind stores when low address bits suggest potential 4 KiB aliasing. Cache-line/page splits can increase memory transactions. Require PMU/top-down evidence before altering layouts: blindly padding allocations trades potential stalls for a larger working set.

### Non-temporal writes and software prefetch

Use a non-temporal store only when an output is sufficiently large and will not soon be reused; otherwise bypassing cache can worsen complete producer/consumer time. Follow architecture-specific alignment/ordering requirements. Software prefetch needs a known future address with sufficient lead time and a latency-bound kernel. Compare no prefetch against several distances; a too-early prefetch evicts data and consumes bandwidth. On regular streams, hardware prefetch and better locality usually come first.

### NUMA and page translations

On NUMA systems, memory can be placed near the first worker that faults/touches a page. Serial initialization followed by remote parallel reads can inflate memory latency. Compare worker-local first touch, memory binding, shard ownership, placement and thread affinity. Huge pages can reduce TLB coverage pressure but affect fragmentation, compaction, OS policy and latency. Confirm actual page mapping and page-walk counters.

### Compiler visibility and numerical semantics

Use compiler vectorization reports before hand-writing assembly. Improve exact type, alias, alignment and constant visibility where guaranteed. An alignment `assume` or C/C++ `restrict` is a **proof obligation**, not a preference. Distinguish exact arithmetic from FMA contraction, reassociation, approximated reciprocal/sqrt and FTZ/DAZ; each may change results, underflow handling, NaN/signed-zero behavior or determinism. Numerical tolerance must come from the application specification, not from a convenient post-hoc threshold.

### GPU occupancy, coalescing and fusion

GPU throughput depends on coalesced lane accesses and enough resident work to hide stalls—not maximising occupancy by itself. More registers per thread can reduce residency; forcing residency can introduce spills. Shared/LDS tiling trades global reads for on-chip storage and barriers; bank conflicts can serialize memory access. Measure transfer-inclusive timing, kernel launch overhead, per-wave divergence, memory transactions and resource use. Kernel fusion can remove global intermediate traffic and launches but increases register use and code size.

## 4. Verification playbook

### Recommended optimization order

1. Establish the actual user-facing objective and critical path: throughput, latency tails, CPU, allocations, memory or energy.
2. Profile *inclusive/exclusive* cost and distinguish running CPU from scheduler, lock, GC, GPU and external waits.
3. Compare asymptotic algorithm alternatives and remove repeated work.
4. Eliminate allocations, redundant copies, transcoding and materialisations.
5. Improve data layout, locality, working set, NUMA and TLB behavior.
6. Expose compiler facts and review optimization reports and generated code.
7. Address material branches, serial dependencies and instruction throughput.
8. Test vectorization and independently compare vector widths.
9. Use concurrency/offload only when granularity and transfer costs permit.
10. Consider prefetch, non-temporal memory access or manual assembly only for measured residual limitations.
11. Differential-test semantics and benchmark end-to-end plus relevant counterexamples.
12. Reprofile after retention; update the coverage ledger and repeat.

### Suggested diagnostic tools

| Tool | Purpose | Caveat |
|---|---|---|
| Linux `perf stat/record/report` | CPU cycles, instructions, branch/cache events, sampled stacks | Permissions, event availability, skid and multiplexing |
| Intel VTune | Top-down front/back-end, memory/cache/NUMA, split loads and store-forward stalls | Microarchitecture-dependent event interpretation |
| AMD uProf/IBS | Zen pipelines, memory, branch, DTLB and load attribution | Feature set varies by Zen generation |
| Arm Streamline/SPE | Neoverse top-down and sampled operations | PMU/SPE platform support |
| `llvm-mca` | Static dependency and scheduling/resource estimates | Does not reproduce dynamic caches/branches |
| `objdump` / compiler disassembly | Verify actual instruction selection | Not itself execution-frequency or latency evidence |
| GCC `-fopt-info-vec-missed` | Explain missed vectorization | Version/target-specific compiler report |
| Clang `-Rpass=loop-vectorize` and `-Rpass-missed=loop-vectorize` | Inspect vectorizer decisions | Not proof of real-world benefit |
| .NET BenchmarkDotNet + disassembly | Repeatable managed microbenchmarks, allocations, generated-code inspection | Warm-up, tier, workload and deployment fidelity |
| `dotnet-trace`, PerfView, EventPipe | .NET CPU stacks, GC events, managed activity | Trace/probe overhead and async attribution |
| NVIDIA Nsight Compute | CUDA occupancy, memory, Roofline and scheduler stalls | Separate kernel-only from transfer-inclusive time |
| ROCprofiler / `rocprofv3` | AMD GPU tracing and kernel counters | Target-dependent counter sets |
| ASan, UBSan, TSan, MSan | Memory, UB, race, uninitialized-read validation | Sanitizer builds are **not** performance baselines |

Basic Linux probe (counter support varies):

```bash
perf stat -r 5 -e cycles,instructions,branches,branch-misses -- ./program
perf record -g -- ./program
perf report
```

**Benchmark contrasts:** 0/1/small/exact-vector/tail/LLC-exceeding; sequential/strided/random; none/all/random/clustered predicates; one-shot/repeated; local/remote NUMA; cache-hot/cold; single worker/physical cores/SMT/oversubscription; all supported ISA/fallback variants. Use workload-specific subsets rather than gratuitous Cartesian products. Stabilize warmup, JIT tiering, frequency, thermal load, affinity and fixtures; preserve useful observable output to prevent dead-code elimination. Report distributions, not just one favorable number.

### Candidate coverage ledger (template)

```text
Candidate ID and parent category:
Source location / caller path / workload:
Observed cost and limiting resource:
Proposed concrete transformation and API/ISA:
Mechanism and falsifying counterexample:
Correctness/alias/lifetime/ordering/numerical invariants:
Hardware/runtime/compiler/feature requirements:
Benchmark command, fixtures, seeds and build identity:
Baseline and candidate results (units, sample count, variability, p99):
Generated-code and counter evidence:
Setup/maintenance/GC/memory/synchronization/transfer costs:
State: pending | tested-retained | tested-rejected | inconclusive | not-applicable | blocked
Decision/rationale and raw artifacts:
Next source-derived adjacent hypothesis:
```

Never mark a blocked/inconclusive experiment as disproven, and never equate a faster inner loop with a faster operation without measuring the full caller.

## 5. Gap analysis against the supplied 23-category catalogue

The original catalogue already has extensive C#/NET-specific coverage of M1–M7 (allocation/memory), C1–C5 (instruction dependencies/branches/SIMD/intrinsics/stencils), S1–S5 (bitmaps/lookup/query/compression), R1–R3 (dispatch/inlining/static configuration) and T1–T4 (concurrency/scheduling). Its correctness, benchmark and coverage-ledger procedures should remain normative.

**Recommended additions**, outside or beyond its present primary categorization:

| Proposed category | Major missing/additional review surfaces |
|---|---|
| A — Algorithm and unnecessary work | Asymptotic complexity, selectivity, incremental recomputation, top-k, batching across pipeline boundaries |
| V — Advanced SIMD and specialised ISA | Width-specific dispatch, compress/expand, SIMD parsers, cryptographic/GF/CRC hardware, VNNI/AMX, SVE predication |
| H — CPU front end and microarchitecture | Macro/microfusion, store forwarding, 4 KiB aliasing, page/line splits, register pressure, µop cache |
| N — NUMA, translation and OS memory | NUMA first touch, huge-page policy, affinity, remote accesses and TLB walks |
| B — Build/compiler orchestration | Native LTO/ThinLTO, sample/instrumented PGO, binary layout, vectorizer diagnostics, native multiversioning |
| G — GPU/accelerator | Coalescing, LDS/shared memory, occupancy, divergence, register pressure, GPU transfer/launch costs |
| I — I/O and network | Syscall batching, zero-copy, async pipelines, buffer ownership and backpressure *(not extensively developed in the research report)* |
| D — Data/query systems | Selectivity, indexes, batched execution and vectorized query engines *(not extensively developed in the research report)* |

**Scope note:** Despite the broad research assignment, the completed Deep Research report is primarily a low-level CPU/memory/compiler/GPU study, not an exhaustive I/O, distributed-systems or database optimization textbook. Such subjects need distinct follow-up literature surveys for truly comprehensive coverage.

## 6. Annotated technical bibliography

Primary and official references selected from the research (URLs are directly usable in Markdown):

1. **Intel Optimization Reference Manual** — CPU cache hierarchy, front-end behavior, micro-op fusion, branches, prefetch, store forwarding, non-temporal traffic and ISA-specific recommendations. [Intel manual volumes](https://www.intel.com/content/www/us/en/developer/articles/technical/intel64-and-ia32-architectures-optimization.html).
2. **Intel Software Developer Manuals** — architectural instruction and exception semantics; consult before relying on masked loads or SIMD instruction widths. [Intel SDM](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html).
3. **Intel Intrinsics Guide** — C/C++ intrinsics to ISA forms and instruction requirements. [Intrinsics Guide](https://www.intel.com/content/www/us/en/docs/intrinsics-guide/index.html).
4. **AMD Zen 5 Software Optimization Guide** — generation-specific Zen tuning. [AMD guide](https://docs.amd.com/v/u/en-US/58455_1.00).
5. **Arm ACLE/NEON references** — language/ISA contracts for Arm vector and scalar intrinsics. [ACLE](https://arm-software.github.io/acle/main/acle.html), [NEON](https://arm-software.github.io/acle/neon_intrinsics/).
6. **Arm SVE overview** — scalable-vector and predicate model. [Arm Research](https://developer.arm.com/community/arm-research/b/articles/posts/the-arm-scalable-vector-extension-sve).
7. **GCC documentation** — optimization flags, native targets, attributes and inline assembly. [Optimization options](https://gcc.gnu.org/onlinedocs/gcc/Optimize-Options.html), [Extended asm](https://gcc.gnu.org/onlinedocs/gcc/Extended-Asm.html), [Multiversioning](https://gcc.gnu.org/onlinedocs/gcc/Function-Multiversioning.html).
8. **Clang/LLVM manuals** — autovectorization control, PGO, ThinLTO, IR semantics and static scheduling analysis. [Language extensions](https://clang.llvm.org/docs/LanguageExtensions.html), [ThinLTO](https://clang.llvm.org/docs/ThinLTO.html), [llvm-mca](https://llvm.org/docs/CommandGuide/llvm-mca.html).
9. **Microsoft optimization guidance** — MSVC optimisation and whole-program compilation, constraints on C/C++ assumptions. [Optimization best practices](https://learn.microsoft.com/en-us/cpp/build/optimization-best-practices).
10. **Intel VTune top-down** — front-end/bad speculation/back-end/retiring triage. [Intel VTune](https://www.intel.com/content/www/us/en/docs/vtune-profiler/cookbook/2025-4/top-down-microarchitecture-analysis-method.html).
11. **AMD uProf** — Zen IBS, DTLB, pipelines and cache metrics. [uProf guide](https://docs.amd.com/r/en-US/57368-uProf-user-guide).
12. **Roofline paper** — Williams, Waterman and Patterson, arithmetic intensity vs compute and memory ceilings. [Berkeley technical report](https://www2.eecs.berkeley.edu/Pubs/TechRpts/2008/EECS-2008-134.html), [DOI](https://doi.org/10.1145/1498765.1498785).
13. **Linux huge-page documentation** — page-mapping and kernel policy. [Transparent Huge Pages](https://www.kernel.org/doc/html/latest/admin-guide/mm/transhuge.html), [HugeTLB](https://cdn.kernel.org/doc/html/latest/admin-guide/mm/hugetlbpage.html).
14. **NVIDIA CUDA Best Practices** — coalescing, occupancy, shared memory, host↔device transfers and fusion. [CUDA guide](https://docs.nvidia.com/cuda/cuda-c-best-practices-guide/).
15. **NVIDIA Nsight Compute** — GPU profiling, roofline, stalls, metrics and occupancy. [Profiling Guide](https://docs.nvidia.com/nsight-compute/ProfilingGuide/).
16. **AMD HIP optimization** — coalescing, LDS, occupancy, wave divergence and AMD GPU tradeoffs. [HIP performance guide](https://rocm.docs.amd.com/projects/HIP/en/latest/how-to/performance_guidelines.html).
17. **LLVM sanitizers** — differential safety checks for memory/UB/races. [AddressSanitizer](https://clang.llvm.org/docs/AddressSanitizer.html), [UndefinedBehaviorSanitizer](https://clang.llvm.org/docs/UndefinedBehaviorSanitizer.html), [ThreadSanitizer](https://clang.llvm.org/docs/ThreadSanitizer.html).
18. **uops.info** — empirical instruction latency and throughput by microarchitecture. [uops.info](https://uops.info/).
19. **Agner Fog optimization manuals** — independently compiled microarchitecture and instruction-level guides. [Agner Fog](https://www.agner.org/optimize/).
20. **.NET runtime** — JIT/PGo, intrinsics, collection/memory safety and implementation specifics. [dotnet/runtime](https://github.com/dotnet/runtime), [hardware intrinsics](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics).

---

# Appendix: Original user-supplied optimization reference documents

The following sources are included in full to preserve the original .NET-specific recipes, examples, caveats and evidence protocol. These are source documents rather than material newly established by the deep-research report. Original relative cross-file links may refer to the former `references/` directory and are left intact.



## Original: SKILL.md

---
name: optimize-code
description: Optimize measured hot paths through repeatable benchmarks, concrete implementation hypotheses, and evidence-based retention or rejection. Use for code optimization and performance work, especially C# and .NET CPU, memory, collections, storage, and concurrency; includes explicit APIs, instructions, and implementation recipes.
---

# Optimize code

Find the limiting operation, establish a benchmark, test concrete hypotheses, and retain only demonstrated improvements. Continue until the applicable catalogue and evidence-derived adjacent opportunities have no remaining testable hypotheses within the authorized scope.

Read source before proposing a transformation. Technical names in this guide are search and reasoning cues, not evidence of a bottleneck. Adapt mechanisms to the actual language, runtime, processor, and workload. The guide is repository-independent; .NET examples do not imply that every target uses .NET or an entity component system.

## 1. Find the hot path

Read [Benchmarking](references/BENCHMARKING.md). Identify the observable objective: operation latency, throughput, frame time, CPU consumption, allocation, or memory use. Establish workload priorities and acceptable regressions before changing code.

Trace the operation through callers, preparation, lookup, iteration, updates, allocation, synchronization, publication, and external waits. Read the actual implementations, ownership rules, comments, configuration, and relevant tests. Profile a representative workload. Separate inclusive from exclusive time, elapsed from CPU time, and CPU execution from GPU, I/O, scheduler, or lock waits.

Record the measured share and suspected limiting resource: instruction throughput, dependency latency, memory latency, bandwidth, allocation/GC, contention, scheduling, or external service time. A visually complex loop is not sufficient evidence. Where evidence is unavailable, establish instrumentation before changing the algorithm.

## 2. Set up a benchmark rig

Create a reproducible baseline and independent correctness reference using the [benchmark protocol](references/BENCHMARKING.md). Use optimized builds, representative fixtures, observable outputs, and the actual deployment settings. Record source identity, commands, runtime/compiler, CPU/ISA, OS, inputs, seeds, and raw results.

Separate setup and execution only when both are measured and their total is accounted for. Include losing distributions, short inputs, tails, mutation, contention, and memory limits as relevant. Serialize timed runs on shared hardware.

Follow the host's artifact-location rules. If none exist, put transient rigs, snapshots, traces, and experimental builds under `.artifacts/ai/optimize-code/<task>/`. Promote only reusable validation or benchmark assets to durable project locations. Inspect startup and callback effects before running a rig; a performance task does not authorize external writes or production database mutations.

## 3. Select hypotheses from the catalogue

Read the complete [candidate catalogue](references/CATALOG.md). Read the detailed recipe for every category applicable to the measured code; do not select from keywords alone.

Maintain a coverage ledger. For each catalogue category record the inspected location and either concrete candidate IDs or a specific reason it does not apply. One experiment does not exhaust a category when the source supports materially different implementations.

For each candidate record:

```text
ID and catalogue category:
Source location and caller/storage path:
Observed cost and suspected mechanism:
Proposed implementation, APIs/instructions, and predicted measurable effect:
Correctness, ownership, range, lifetime, and numerical invariants:
Workload priorities, evidence threshold, change burden, and regression limits:
Baseline, commands, fixtures, and falsifying result:
State and evidence:
```

Use states `pending`, `tested-retained`, `tested-rejected`, `inconclusive`, `not-applicable`, and `blocked`. State why an experiment is inconclusive or blocked. Neither state proves that a hypothesis is false.

Rank by measured cost, plausible application benefit, and experiment cost. Prefer removing repeated work or unnecessary transfers when the evidence supports it. Several hypotheses may be queued together; isolate mechanisms in initial comparisons so a result has an interpretable cause.

For C# value types in a measured hot path, inspect struct size, call sites, field/property access, generated code, and copy frequency. Compare by-value with `in` or `ref readonly` parameters/locals/returns, and use `readonly struct` or `readonly` members where immutability is valid to prevent defensive copies through readonly references. A `ref struct` is useful for stack-scoped state or spans whose lifetime must not escape; it does not itself prevent copies, and it cannot be stored in ordinary heap objects or used across incompatible async/iterator boundaries. Measure complete callers, since small by-value structs can beat indirection and readonly access through a mutable struct may itself trigger defensive copies. Preserve mutation, aliasing, and lifetime semantics.

For byte-oriented codecs and serializers, inspect whether a packed `uint` or `ulong` can replace several scalar byte stores. On x86-64, compare a width-matched `MOV` store (`mov dword ptr [dst], reg` or `mov qword ptr [dst], reg`) with separate `mov byte ptr` stores, extraction shifts/masks, and address calculations in the actual Tier 1 caller. The register holds the packed value; access to byte subregisters is not itself the saving. Prove byte order, alignment support, complete initialization, width, tails, and destination capacity; a wider store must never cross the logical output boundary. Compare full-operation results, since packing can add instructions or register pressure. See [Memory M7](references/MEMORY.md#m7-copies-and-buffer-traffic).

Treat JIT range-check elimination and opcode dispatch as separate hypotheses from arithmetic strength reduction or reciprocal throughput. Use BenchmarkDotNet's disassembly diagnoser or `DOTNET_JitDisasm` to inspect the optimized Tier 1/OSR caller. For range checks, distinguish `CMP`/conditional jumps to a range-check failure helper from the loop-limit branch, validate the highest accessed index or bounded slice, and compare safe indexed access with a proven GC-tracked `ref` plus `Unsafe.Add` only if checks remain material. A raw pointer or native-storage candidate additionally requires pinning or explicit allocation ownership and complete cost accounting. For tag/opcode loops, record tag frequencies and transitions, then compare common-path fall-through, branch order, `switch` lowering, and rare-path placement under representative and adversarial inputs. Confirm actual `Jcc`/jump-table code and measure; source order alone cannot prove fewer branch misses. See [Memory M3/M4](references/MEMORY.md#m3-arrays-and-bounds-checks), [CPU C2](references/CPU.md#c2-branches-prediction-and-layout), and [Runtime R1](references/RUNTIME.md#r1-dispatch-and-lookup-tables).

## 4. Implement and benchmark

Implement the smallest falsifiable change. Preserve unrelated edits and the baseline. Validate correctness before timing. Check invalid inputs and required exceptions, ordering, overflow, floating-point behavior, aliasing, reference lifetimes, publication, and fallback paths as applicable.

Run baseline and candidate with the same useful work and comparable conditions. Inspect generated native code when the hypothesis concerns bounds checks, instruction selection, SIMD, dispatch, inlining, feature elimination, or dependency chains. Measure complete-operation effects and all preparation, maintenance, allocation, synchronization, and memory costs.

For unsafe access, require an explicit proof of valid ranges and lifetimes. For concurrency, require a coherent protocol before testing faster primitives. A successful stress run does not replace either proof.

## 5. Discard or retain from the evidence

Retain only correct changes with repeatable benefit beyond noise under the predefined workload priorities and regression limits. Use no universal percentage floor: weigh the credible effect against whether the change is a simplification, a small local edit, a non-obvious specialization, or a broad refactor, together with its maintenance and portability burden. Treat mixed or noisy evidence as inconclusive; improve the measurement when feasible. Revert only the candidate's own changes when rejected or unverified, preserving unrelated work and the experiment record.

Benchmark useful combinations after their individual effects are understood. Gains may overlap. Revalidate the complete caller path after integration. Preserve durable rationale for non-obvious invariants, update required documentation/changelogs, and commit cohesive completed work when the host requires it.

## 6. Repeat until hypotheses are exhausted

Reprofile after each material retained change. The bottleneck may move. Reinspect callers, consumers, neighboring loops, repeated preparation, data layout, allocation, copies, and ownership boundaries. Add concrete adjacent hypotheses to the same ledger; measure them through the same process. Do not start unrelated redesigns merely because they appear in the catalogue.

Continue while any applicable, testable candidate remains. Revisit earlier inconclusive results when new evidence makes them testable. Do not stop at the first improvement or after an arbitrary experiment count. Do not repeat a rejected experiment unless its implementation, workload, or supporting evidence has materially changed.

Completion requires a reasoned disposition for every relevant category and discovered candidate. If authorization, hardware, missing fixtures, or an explicit resource limit prevents testing, preserve the pending work and report partial completion with the specific constraint. Do not describe that condition as exhausted optimization potential.

## Deliver the result

Report retained changes with before/after units, variability, representative workload coverage, correctness evidence, and practical limitations. Link the rig, commands, and raw evidence. Include rejected/inconclusive candidates and the final coverage ledger. Distinguish kernel improvement from application improvement and executed measurements from predictions.

Use [Sources](references/SOURCES.md) to verify API, ISA, and compiler contracts. Historical measurements supply hypotheses, not expected speedups. This skill does not require delegation; if independent work is delegated, assign explicit ownership and keep timed benchmarks serialized.



## Original: CATALOG.md

# Candidate catalogue

Read every row after profiling. Match it to actual source and measurements, then read the linked implementation recipe. Record a reason when a category does not apply. These are alternatives to test, not universal replacements.

| ID | Source patterns and technical cues | Concrete experiment and recipe |
|---|---|---|
| M1 | Allocations immediately overwritten; `new T[]`, GC zeroing, LOH, allocation rate | Compare reusable storage, `ArrayPool<T>`, `GC.AllocateUninitializedArray<T>`, and explicitly owned `NativeMemory.Alloc`; charge lifetime costs. [Memory M1](MEMORY.md#m1-allocation-and-initialization) |
| M2 | Small temporary buffers; `stackalloc`, `Span<T>`, `SkipLocalsInit` | Use bounded stack storage outside loops with a larger-buffer fallback; prove initialization. [Memory M2](MEMORY.md#m2-stack-storage) |
| M3 | Repeated field/indexer loads and range checks; `CMP`/`Jcc` to a throw helper; `T[,]`, `T[,,]`, `T[][]`, `T[]` | Inspect optimized JIT range checks; capture one array, use a bounded span, prevalidate fixed highest indexes, flatten rectangular data, and traverse the contiguous dimension. [Memory M3](MEMORY.md#m3-arrays-and-bounds-checks) |
| M4 | Material bounds overhead remains; `MemoryMarshal`, `Unsafe.Add`, `Unsafe.AsPointer`, `fixed` | Compare GC-tracked byref arithmetic with the safe loop; use raw pointers only with explicit pin/allocation ownership. [Memory M4](MEMORY.md#m4-managed-references-and-raw-pointers) |
| M5 | Object graphs, pointer chasing, cold fields, sparse useful bytes | Compare AoS, SoA, AoSoA, hot/cold fields, ECS columns, preallocated buffers, and tiled iteration. [Memory M5](MEMORY.md#m5-layout-and-working-set) |
| M6 | Expensive location search, cache/TLB misses, known future indirect addresses | Test location tables and compact handles first; for demonstrable latency-bound indirect access, sweep guarded `Sse.Prefetch0`/`PREFETCHT0` look-ahead distances and charge address work, bandwidth, and pollution. [Memory M6](MEMORY.md#m6-addressing-prefetch-and-alignment) |
| M7 | Intermediate arrays, serialization copies, scalar copy/fill loops, fixed-width records | Remove avoidable copies first; then compare overlap-correct `Span<T>.CopyTo`/`Buffer.MemoryCopy`, runtime block operations, fused passes, packed stores, and carefully justified manual SIMD or non-temporal copies. [Memory M7](MEMORY.md#m7-copies-and-buffer-traffic) |
| C1 | Recurrences and unused execution capacity; latency, reciprocal throughput, `VMULSD` | Draw the dependency DAG; compare independent accumulators, modest unrolling, and interleaved pure work. [CPU C1](CPU.md#c1-dependency-latency-and-throughput) |
| C2 | Unpredictable cheap predicates, skewed opcode tags, repeated mode tests, `Jcc`, `CMOVcc`, `CSEL` | Test unswitching, grouping, opcode branch order/fall-through, `?:`, masks, early condition resolution, and PGO under contrasting distributions. [CPU C2](CPU.md#c2-branches-prediction-and-layout) |
| C3 | Scalar bulk arithmetic, comparison, reductions, shuffles; 128-bit versus 256-bit width | Compare scalar/library/portable vector code with explicit 256-bit AVX floating-point and AVX2 integer paths, plus 128-bit and scalar fallbacks; inspect lane crossings, tails, spills, memory traffic, and frequency effects. [CPU C3](CPU.md#c3-simd) |
| C4 | Manual bit loops, repeated division, byte swapping, missing intrinsic lowering | Compare `BitOperations`, `BinaryPrimitives`, `Math/MathF`, and supported ISA intrinsics; inspect `POPCNT`, `TZCNT`, `LZCNT`, `BSWAP`, `FMA`. [CPU C4](CPU.md#c4-intrinsics-and-strength-reduction) |
| C5 | 1D/2D/3D neighborhood loops, image filters, convolutions, cellular automata, finite-difference or stencil updates | Compare boundary splitting, rolling windows, cache tiles, halos, separable passes, SIMD interiors, temporal blocking, and disjoint parallel tiles while preserving update dependencies. [CPU C5](CPU.md#c5-stencil-and-neighborhood-kernels) |
| S1 | Boolean arrays, flag sets, component signatures, dense membership and bulk changes | Compare single-word bitmasks, `BitArray`, packed `ulong[]`, branchless mask tests, set-bit iteration, SIMD AND/OR/XOR, dirty masks, and a published backbuffer. [Storage S1](STORAGE.md#s1-bitmaps-and-bulk-updates) |
| S2 | Repeated hash/probe/equality work, duplicate lookups | Compare direct indexing, sorted arrays, bitsets, cached handles, `TryGetValue`, and measured byref updates. [Storage S2](STORAGE.md#s2-lookups-and-hashing) |
| S3 | Read-mostly long-lived mappings; `Dictionary`, `HashSet`, immutable keys | Build and reuse `FrozenDictionary<TKey,TValue>`/`FrozenSet<T>`; include build cost and actual comparer behavior. [Storage S3](STORAGE.md#s3-frozen-collections) |
| S4 | Repeated query preparation, signature matching, column resolution | Cache normalized queries, archetype matches, and column mappings with complete invalidation. [Storage S4](STORAGE.md#s4-storage-and-query-preparation) |
| S5 | Long runs, repeated metadata, sparse occupancy, sorted IDs | Compare arrays, bitsets, ranges, RLE, delta encoding, and skip indexes under fragmentation and mutation. [Storage S5](STORAGE.md#s5-compression-and-runs) |
| R1 | Stable lookup/dispatch decisions, opcode chains, reflection or delegate work in loops | Compare ordered tag tests, LUTs, `switch`, jump tables, cached typed delegates, and direct specialization. [Runtime R1](RUNTIME.md#r1-dispatch-and-lookup-tables) |
| R2 | Opaque tiny accessors, virtual calls, boxing, large struct copies | Inspect caller assembly; test devirtualization, concrete calls, `ref readonly`/`in`, and selective inlining. [Runtime R2](RUNTIME.md#r2-inlining-specialization-and-copies) |
| R3 | Process-wide startup configuration checked per operation | Capture primitive `static readonly` flags before hot compilation; verify disabled-path elimination in Tier 1. [Runtime R3](RUNTIME.md#r3-startup-frozen-feature-flags) |
| T1 | Independent variables written by different cores; poor scaling and coherence traffic | Confirm false sharing with controlled layout or counters, then compare thread-local accumulation, publication batching, disjoint cache-line strides, padding, and verified base alignment. [Concurrency T1](CONCURRENCY.md#t1-false-sharing) |
| T2 | Shared mutation, repeated coordination, borrowed references | Compare exclusive ownership, partitions, snapshots, batching, and valid unsynchronized internal paths. [Concurrency T2](CONCURRENCY.md#t2-ownership-and-publication) |
| T3 | Lock acquisition, retries, spin CPU, wakeups, writer starvation | Compare the actual lock, `System.Threading.Lock`, `ReaderWriterLockSlim`, sharding, and bounded `SpinWait`. [Concurrency T3](CONCURRENCY.md#t3-locks-and-waiting) |
| T4 | CPU work with independent partitions, tiny tasks, oversubscription, queue delay, blocking I/O | Compare sequential execution with bounded multithreading across a degree-of-parallelism sweep, coarse partitions, thread-local state, merge cost, bounded queues, batching, and suitable asynchronous I/O. [Concurrency T4](CONCURRENCY.md#t4-scheduling-batching-and-io) |

After a retained change, reconsider the table against the new profile. Add source-specific candidates such as eliminating repeated parsing, reducing algorithmic complexity, reusing a prepared result, or avoiding a producer/consumer format conversion. Specify the exact work removed, invalidation contract, and counterexample workload.



## Original: CPU.md

# CPU dependencies, branches, and instructions

## C1 Dependency latency and throughput

**Look for:** a recurrence such as `sum += x` or `r = r * a`, dependent address generation, repeated flag dependencies, aliasing, or calls that hide independent work. Search dependency DAG, critical path, RAW/read-after-write, WAR, WAW, register renaming, merge dependency, instruction-level parallelism (ILP), out-of-order execution, superscalar issue, execution ports, reorder buffer, and software pipelining.

Latency is the delay until a dependent instruction can consume a result. Throughput is instructions per cycle; reciprocal throughput is cycles per instruction for an independent sustained stream. A reciprocal throughput of 0.5 means two instructions per cycle, not a result in half a cycle.

For an idealized operation with latency `L`, reciprocal throughput `R`, and `K` independent chains:

```text
cycles per instruction >= max(R, L / K)
chains required to reach the execution-rate bound >= ceil(L / R)
```

The register-register `VMULSD` example on Skylake has 4-cycle latency and 0.5-cycle reciprocal throughput. Two independent multiplies can start each cycle in the idealized model; a chain starts at cycles 0, 4, 8. Eight independent chains are needed to hide that recurrence latency at the stated issue rate. Verify exact operand dependency, instruction form, CPU, and memory-versus-register operands in [uops.info](https://uops.info/html-instr/VMULSD_XMM_XMM_XMM.html). Loads, stores, front-end supply, and other port demand constrain a real loop.

**Implement:** draw the dataflow, then compare modest unrolling, multiple independent accumulators, interleaved independent calculations, and invariant hoisting. Compare direct scalar access separately from interleaving so removing an accessor is not mislabeled an ILP benefit. Identical numerical values in distinct registers can be independent; different names that depend on prior results are not.

Inspect `VMULSD`, `VMULSS`, `VMULPS`, `VADDSS`, FMA, spill loads/stores, code size, and execution-port demand. Source rearrangement can emit identical code. More accumulators can exceed the register budget. Starting future records can waste work before early exits.

Preserve checked/unchecked integer behavior and each required floating-point association. Reassociation, multiple floating-point accumulators, and fused multiply-add can change rounding, cancellation, NaN, infinity, signed zero, subnormal behavior, determinism, and branch outcomes. Do not invent a numerical tolerance to accept a faster result. Reject if dependency latency was not limiting or caller-level benefit disappears.

## C2 Branches, prediction, and layout

**Look for:** material branch misses, cheap conditionally selected values, a loop-invariant mode test, heterogeneous records, and long dependencies that delay branch resolution. A 50% taken frequency is not a 50% miss rate: alternating and clustered sequences can be predictable.

**Test in distinct experiments:**

1. Hoist an invariant check or perform loop unswitching: select one processing loop before iteration. Prove the mode remains stable for that complete operation.
2. Group data by type or predicate, or maintain separate collections and process them in separate loops. Include grouping, storage, maintenance, and required ordering costs.
3. Compare `if`, conditional `?:`, and explicit masks for cheap already-available values. Inspect x86 `CMP`, `TEST`, `Jcc`, `CMOVcc`, `SETcc` and Arm `CSEL`, `CSET`, `CSETM`. C# syntax does not promise branchless native code.
4. Shorten the condition's dependency chain so a misprediction can be discovered earlier. A lower penalty can matter even if the miss count is unchanged.
5. Compare representative PGO, hot/cold splitting, common-path fall-through, and smaller hot code. Source function order does not guarantee machine-code placement.

For bytecode, image codecs, and tagged parsers, record opcode frequency and transitions on both representative and adversarial inputs. Compare ordered `if`/`else` tests, common-case fall-through, rare full-payload/error paths, and `switch` lowering as separate candidates. Inspect the Tier 1 `CMP`/`TEST` plus `Jcc` sequence or jump table, layout, instruction count, and code size. A gain may come from fewer comparisons or shorter dependencies without any reduction in branch-miss count; do not infer mispredictions from elapsed time. Data layout and PGO can change the result, so preserve a cold/uniform or alternating-tag counterexample.

```csharp
static uint SelectByMask(bool condition, uint a, uint b)
{
    uint mask = unchecked(0u - (condition ? 1u : 0u));
    return (a & mask) | (b & ~mask);
}
```

Also inspect `b ^ ((a ^ b) & mask)`, conditional increment `count += condition ? 1 : 0`, and SIMD comparison/select. These are hypotheses; the compiler may produce identical code or restore a branch. Conditional moves create data dependencies and can be slower than a correctly predicted branch. Computing both alternatives increases instructions and memory traffic.

Preserve short-circuit evaluation. `condition ? f() : g()` is not equivalent to evaluating both and combining the answers. Check effects, exceptions, division by zero, invalid memory, and aliasing. Floating-point `x * 0` does not suppress NaN or infinity. Integer absolute-value identities such as `(x ^ signMask) - signMask` need defined width, shift, and overflow behavior; the minimum signed value still has no positive same-width representation. C# checked/unchecked semantics and native C/C++ undefined behavior are different contracts.

An approximate tradeoff is `extra branchless cost < miss probability * effective miss penalty`. It is a diagnostic model, not a universal threshold; overlap and dependencies matter. Measure elapsed time, cycles, total instructions, and absolute misses under none/all, sparse/dense, random half, alternating, clustered, changing, and early/late-exit inputs.

For native compilers, inspect C++20 `[[likely]]`/`[[unlikely]]`, GCC/Clang `__builtin_expect`, `__builtin_expect_with_probability`, and Clang `__builtin_unpredictable`. These describe compiler expectations, not future CPU outcomes. Ordinary edge-count PGO does not necessarily measure hardware misses. An assumption or `__builtin_unreachable` is not a likelihood hint. LLVM can convert conditional moves back to branches; LLVM BOLT can use profiles to reorder final native binaries. These are not portable C# APIs. [GCC builtins](https://gcc.gnu.org/onlinedocs/gcc/Other-Builtins.html), [Clang builtins](https://clang.llvm.org/docs/LanguageExtensions.html), [BOLT](https://llvm.org/docs/BOLT/index.html)

Treat x86 2E/3E branch prefixes and Redwood Cove's documented 3E taken-hint behavior as narrow architecture-specific cues. Verify encoding, vendor, predictor state, and code-size effects before use; the documented case concerns absent predictor history, with dynamic prediction taking precedence. Do not insert blanket alignment or hint prefixes. [GCC implementation note](https://gcc.gnu.org/pipermail/gcc-cvs/2024-July/405854.html)

## C3 SIMD

**Look for:** independent lane-wise arithmetic, signature comparisons, ID search, reductions, bulk predicates, byte transformations, and regular memory access. Inspect existing library vectorization before writing intrinsics.

| Family | Explicit search names | Relevant operations/instructions |
|---|---|---|
| Portable .NET | `System.Numerics.Vector<T>`, `Vector.IsHardwareAccelerated`, `Vector128`, `Vector256`, `Vector512`, `IsHardwareAccelerated`, `Count` | `LoadUnsafe`, `StoreUnsafe`, `Equals`, `ConditionalSelect`, `ExtractMostSignificantBits` where supported |
| x86 SSE | `Sse`, `Sse2`, `Sse3`, `Ssse3`, `Sse41`, `Sse42`, `IsSupported`; 128-bit vectors | `ADDPS`, packed arithmetic, comparisons and shuffles |
| x86 AVX | `Avx`, `Avx2`, `Avx512F`, `Avx512BW`, `Fma`; 256-bit and supported 512-bit forms | `VADDPS`, `VMULPS`, `VFMADD231PS`, `VMOVUPS`, `VMOVAPS`, `VCMPPS`, `VPAND`, `VPOR`, `VPXOR`, `VSHUFPS`, `VPERMPS`, `VPSHUFB` |
| Arm | `AdvSimd`, `AdvSimd.Arm64`, NEON; SVE/SVE2 only where hardware and runtime expose them | 128-bit NEON lanes; scalable SVE vector lengths and predicates |

Terminology matters: AVX is the x86 extension that introduced 256-bit floating-point vector forms; AVX2 extends 256-bit integer operations. “AVX-256” is best treated as shorthand for a 256-bit AVX vector form or `.NET Vector256<T>`, not as a separate ISA. Guard explicit floating-point AVX code with `Avx.IsSupported` and explicit integer AVX2 code with `Avx2.IsSupported`; `Vector256.IsHardwareAccelerated` does not authorize calling an unsupported x86 intrinsic.

**Implement:** define lane meaning, choose a vector width, process complete vectors, and finish with a safe scalar tail. Use `Avx2.IsSupported`, `AdvSimd.IsSupported`, or the relevant ISA check for ISA-specific operations; generic vector acceleration does not prove a specific instruction is available. Retain and explicitly test a scalar fallback. Use target-supported APIs rather than assuming every vector class supports every element type or operation.

Run width and API families as separate hypotheses: scalar/runtime library, portable `Vector<T>` or `Vector256<T>`, explicit 128-bit intrinsics, and explicit 256-bit AVX/AVX2. Inspect whether the JIT already vectorized the loop. Compare elements per iteration, instruction count, load/store throughput, alignment, lane-local versus cross-128-bit-lane shuffles, horizontal reductions, gather/scatter, mask extraction, register spills, code size, and any frequency or transition effects on supported targets. A 256-bit path can lose to 128-bit code when the kernel is memory-bound, shuffle-heavy, tail-dominated, register-constrained, or downclock-sensitive.

For AVX2 integer kernels, consider packed add/subtract, saturating arithmetic, min/max where supported, compare/equality masks, AND/OR/XOR/AND-NOT, byte shuffles, and widening/narrowing only when the exact signedness and saturation contract matches. For AVX floating-point kernels, preserve NaN, infinity, signed-zero, rounding, association, and FMA semantics. AVX-512 is a family with separate feature subsets and must be evaluated independently. Wider vectors are not automatically faster. [Runtime vectorization guidance](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/vectorization-guidelines.md)

For filtering, distinguish `Avx2.CompareEqual`, `Avx2.MoveMask`, `Avx.MoveMask`, `VPCMPEQ`/`VPCMPEQD`, `VPMOVMSKB`, and `VBLENDVPS`. A byte MoveMask yields one bit per byte; an equal 32-bit lane can contribute four set bits. A float MoveMask yields one bit per float lane. State how the chosen mask maps to entities before using `PopCount` or `TrailingZeroCount`. Guard an empty mask before converting its bit index to an address.

Test zero length, smaller than a vector, exact multiples, partial tails, first/last lane, no/all matches, signed/unsigned comparisons, insufficient destination capacity, and legal overlap. Never read beyond the logical buffer because allocation padding appears accessible. Verify exact masked-load fault semantics before relying on them. Charge compaction/output and layout-conversion costs. See [bitmap recipes](STORAGE.md#s1-bitmaps-and-bulk-updates).

## C4 Intrinsics and strength reduction

**Look for:** loops counting/scanning bits, software rotation or byte swaps, repeated expensive scalar operations, and generated instructions that fail to exploit supported hardware.

| Candidate API or technique | Instruction/mechanism cues | Proof and comparison |
|---|---|---|
| `BitOperations.PopCount` | `POPCNT`; vector `VPOPCNT*` where supported | Check word width and actual target lowering; compare whole loop. |
| `BitOperations.TrailingZeroCount`, `LeadingZeroCount`, `Log2` | `TZCNT`, `BSF`, `LZCNT`, `BSR` | Verify zero-input API semantics; no-match is not a valid index. |
| `BitOperations.RotateLeft`, `RotateRight` | `ROL`, `ROR`, `RORX` | Preserve width and shift semantics; compiler may recognize ordinary idioms. |
| `Bmi1`, `Bmi2`, `Popcnt`, `Lzcnt` intrinsics | `ANDN`, `BLSR`, `BEXTR`, `PDEP`, `PEXT` | Guard the exact ISA; throughput differs greatly by CPU and form. |
| `BinaryPrimitives.ReverseEndianness` | `BSWAP`, Arm `REV` | Preserve element width, signed interpretation, and byte order. |
| `Math.FusedMultiplyAdd`, `MathF.FusedMultiplyAdd`, `Single.FusedMultiplyAdd`, `Fma` | scalar/vector FMA | Require the permitted single-rounding contract. |
| `Math.Abs`, `Math.Min`, `Math.Max`, `Math.Clamp` and `MathF` equivalents | scalar/vector min/max/abs and conditional selection | Check minimum signed integer, NaN, signed zero, and saturation semantics. |
| Constant division/remainder, multiply/add addresses | `DIV`, `IDIV`, multiply-high, shifts, masks, `LEA`, `IMUL` | Inspect compiler strength reduction first; masks replace remainder only for appropriate power-of-two unsigned domains. |

Use intrinsic classes when the target lacks a suitable high-level lowering and the operation is demonstrably hot. Verify feature support, scalar fallback, input range, and overflow. Approximate reciprocal/reciprocal-square-root instructions such as `RCPPS`/`RSQRTPS` require an existing error contract and appropriate refinement; do not substitute them for exact division or square root solely because they are faster. See [.NET intrinsics](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics?view=net-10.0) and [BitOperations](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.bitoperations?view=net-10.0).

## C5 Stencil and neighborhood kernels

**Look for:** repeated updates that read a fixed neighborhood around each output element: image convolution, blur/sharpen filters, cellular automata, finite-difference solvers, grid physics, and 1D/2D/3D stencils. Determine whether each step reads an immutable input and writes a distinct output, performs an order-dependent in-place update, or iterates through multiple time steps; these contracts determine which transformations are legal.

Test mechanisms separately:

1. Split boundaries from the interior so the dominant loop has constant valid offsets and no per-cell edge branch. Preserve the exact boundary policy: clamp, wrap, mirror, constant halo, or excluded edge.
2. Hoist row/slice bases and use rolling neighborhood references or line buffers to reduce address calculation and reloads. Charge line-buffer fill and rotation.
3. Tile the interior so the active rows, planes, and output tile fit a measured cache budget. Sweep tile width/height/depth rather than assuming a universal size; include halo bytes and conflict/TLB behavior.
4. Compare direct 2D kernels with separable horizontal/vertical passes only when the coefficients are mathematically separable and intermediate rounding/precision semantics are acceptable.
5. Vectorize contiguous interior lanes with `Vector<T>`, 128-bit operations, or 256-bit AVX/AVX2 as appropriate. Handle left/right boundaries and vector tails without reading outside the logical allocation.
6. Consider temporal blocking across multiple steps only when intermediate visibility and update order permit it. Account for expanded halos, synchronization boundaries, and numerical drift.
7. Partition rows, planes, or tiles across workers only after proving disjoint writes and controlled input lifetimes. Include load balance, merge/barrier cost, NUMA placement, and false sharing at adjacent output boundaries.

Benchmark representative grid shapes, narrow dimensions, tiny grids, large cache-exceeding grids, boundary-heavy cases, one versus many time steps, and coefficient/data distributions. Report bytes transferred per output, arithmetic intensity, cache/TLB misses where available, bandwidth, vector utilization, scaling, and end-to-end setup. Differential-test every boundary cell and randomized interiors against an independent scalar reference. Preserve overflow, saturation, floating-point association, determinism, and in-place dependency semantics.



## Original: MEMORY.md

# Allocation, access, and memory layout

## M1 Allocation and initialization

**Look for:** `new T[]` followed by a full overwrite, temporary arrays in loops, repeated capacity growth, allocation rate, Gen0/Gen1/Gen2, LOH/POH, zero-fill traffic, and retained pools. Trace every read and escape before removing initialization.

**Implement candidates separately:**

- Retain an exclusively owned scratch buffer; use `Array.Empty<T>()`, `List<T>.EnsureCapacity`, or `Dictionary<TKey,TValue>.EnsureCapacity` where repeated allocation/growth is measured.
- Compare `GC.AllocateUninitializedArray<T>(length)` with `new T[length]`. It permits skipping zeroing; it does not guarantee it. Reference-containing element types still require GC-safe initialization. Initialize every logically exposed element before reading or publishing it; partial writes and exception paths must not expose residual contents. Measure allocation plus initialization plus use. [API contract](https://learn.microsoft.com/en-us/dotnet/api/system.gc.allocateuninitializedarray?view=net-10.0)
- Compare `ArrayPool<T>.Shared.Rent`/`Return` or `MemoryPool<T>`/`IMemoryOwner<T>` for repeated temporary storage. A rented array can exceed the requested length and contain prior data. Operate on the logical slice, return exactly once after the last borrower, and measure required clearing, retention, and contention.
- Compare native allocation only with explicit ownership. Pair `NativeMemory.Alloc` with `NativeMemory.Free`, or `NativeMemory.AlignedAlloc` with `NativeMemory.AlignedFree`; validate size multiplication and alignment. `AllocZeroed` is the zero-initialized alternative. Never place managed object references in arbitrary native storage. [NativeMemory](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory?view=net-10.0)

```csharp
// Illustrative buffer lifetime; benchmark the application's actual size and work.
unsafe static int NativeRoundTrip()
{
    const int length = 4096;
    byte* buffer = (byte*)System.Runtime.InteropServices.NativeMemory.Alloc((nuint)length);
    try
    {
        Span<byte> bytes = new(buffer, length);
        bytes.Fill(7); // Every exposed byte is initialized before it is read.
        return bytes[0] + bytes[length - 1];
    }
    finally
    {
        System.Runtime.InteropServices.NativeMemory.Free(buffer);
    }
}
```

For long-lived native ownership, define disposal/finalization with an appropriate owner such as `SafeHandle`. Assess balanced `GC.AddMemoryPressure`/`GC.RemoveMemoryPressure` for substantial native allocations held by managed objects; those calls neither allocate nor free memory. Compare native bytes as well as managed allocation counters. Reject when zeroing was not limiting or ownership/pool costs exceed savings.

## M2 Stack storage

**Look for:** small bounded temporary buffers whose lifetime stays within a synchronous call.

Use `Span<T> scratch = stackalloc T[count]` for unmanaged elements only after proving a conservative byte bound. Select a stack budget appropriate to call depth and target; use a pool or heap fallback for larger inputs. Allocate once outside repeated loops and reuse the span. `stackalloc` storage is not guaranteed initialized, cannot escape its stack lifetime, and can exhaust the stack. `SkipLocalsInit` is a separate code-generation experiment with broader initialization obligations; do not enable it as a blanket allocation fix. [stackalloc contract](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/stackalloc)

Test zero/maximum sizes, fallback boundaries, deep calling contexts, and partial writes. Measure stack initialization, pool rental/return, and full-operation cost. Stack storage is not automatically cheaper for large buffers.

## M3 Arrays and bounds checks

**Look for:** repeated field loads, a loop bounded by a different object, surviving `CMP`/`JAE` and throw helpers, rectangular `T[,]`/`T[,,]`, or jagged `T[][]` row lookups.

Capture the array reference once and use that same array's length. A local reference is not a copy of the elements. Use `Span<T>`/`ReadOnlySpan<T>` and `Slice`/`AsSpan` to validate one logical range and iterate its length. Inspect generated code before concluding that checks were removed.

```csharp
static int SumRange(int[] source, int start, int count)
{
    ReadOnlySpan<int> items = source.AsSpan(start, count);
    int sum = 0;
    for (int i = 0; i < items.Length; i++)
        sum = unchecked(sum + items[i]);
    return sum;
}

static int SumPrefix(int[] source)
{
    if (source.Length < 4)
        throw new ArgumentException("At least four elements are required.", nameof(source));
    return unchecked(source[0] + source[1] + source[2] + source[3]);
}
```

For fixed accesses, validating the highest index first, or accessing it first when semantics permit, can establish bounds for lower nonnegative indexes. It is not a rule to reverse every loop. Reordering observable reads, writes, or exceptions may change behavior. Prove `start + count`, `row * stride`, and highest-index arithmetic cannot overflow; subtraction-based validation or a bounded slice can avoid an overflowing endpoint.

Use optimized caller disassembly to distinguish an explicit loop-limit `CMP`/`Jcc` from a repeated `CMP`/`Jcc` to a range-check failure helper (often `CORINFO_HELP_RNGCHKFAIL` on CoreCLR). Check Tier 1 or OSR code under deployment settings: Tier 0, ReadyToRun, and a standalone callee can show different checks. A dominating validation of the highest accessed element or a slice of the same storage can permit JIT range analysis to eliminate lower-index checks, but confirm the actual generated code. The JIT may already coalesce these checks on the target runtime.

Compare rectangular arrays against `T[]` with `index = y * width + x` or `index = (z * height + y) * width + x`. Validate dimensions and coordinate ranges, then hoist row/slice offsets outside the inner loop and traverse `x` contiguously. A valid flattened offset alone does not prove that each coordinate was valid. For jagged arrays, capture one row and iterate its own length. Jagged storage has separate allocations/indirections; choose it for actual ragged data, not by assuming it is faster.

Modern JIT behavior can differ from the historical examples in [Faster C# array access](https://blog.tedd.no/2020/06/01/faster-c-array-access/) and [Why C# multidimensional arrays are slow](https://blog.tedd.no/2021/10/07/why-c-multidimensional-arrays-are-slow/). Reproduce on the target; preserve required null/range/exception semantics.

## M4 Managed references and raw pointers

If safe forms leave material overhead, compare a small internal helper using `MemoryMarshal.GetArrayDataReference` or `MemoryMarshal.GetReference` and the managed-reference overload of `Unsafe.Add`. Offsets count elements; `Unsafe.AddByteOffset` counts bytes. Preserve valid byrefs throughout arithmetic, not merely at the final dereference.

```csharp
static int SumByRef(int[] source)
{
    int sum = 0;
    ref int first = ref System.Runtime.InteropServices.MemoryMarshal.GetArrayDataReference(source);
    for (int i = 0; i < source.Length; i++)
        sum = unchecked(sum + System.Runtime.CompilerServices.Unsafe.Add(ref first, i));
    return sum;
}
```

The empty-array reference must not be dereferenced. The loop above uses only existing elements. `Unsafe.AsPointer` produces an untracked pointer and does not pin storage. `Unsafe.Read`, `Unsafe.AsRef`, `Unsafe.ReadUnaligned`, and `Unsafe.WriteUnaligned` do not establish bounds or lifetime. Raw pointers into movable managed storage need a visible `fixed`/`GCHandle` pin for their complete use; `GC.KeepAlive` prevents early collection, not relocation. Keep native allocation ownership distinct from pinning managed objects.

`CollectionsMarshal.AsSpan(List<T>)` exposes list storage: exclude additions, removals, `EnsureCapacity`, `TrimExcess`, and all other backing-array replacement while the span is live, including callbacks and concurrency. A local `List<T>` reference freezes neither `Count` nor storage.

Test empty/invalid ranges, highest indexes, overflow, tails, mutation exclusion, and required exceptions against the safe reference. Document lifetime/range proofs; GC stress is supplementary. The [2022 reference-arithmetic article](https://blog.tedd.no/2022/03/07/unsafe-reference-arithmetic-in-net/) motivates comparison, but its unpinned-pointer assumption conflicts with [Microsoft's unsafe-code contract](https://learn.microsoft.com/en-us/dotnet/standard/unsafe-code/best-practices). Do not copy that assumption or runtime object-header offsets.

## M5 Layout and working set

**Look for:** pointer chasing, linked object graphs, many small allocations, cache lines loaded for a few hot fields, wide structs, and unused fields in a streaming pass.

Draw each load from logical ID to payload. Compare array of structures (AoS), structure of arrays (SoA), array of structures of arrays (AoSoA), and hot/cold field separation. For an ECS, inspect actual archetype/chunk, sparse-set, dense component pool, and entity-to-row organization before proposing columns. Group data by the fields processed together; an ECS name alone does not establish locality.

Preallocate contiguous buffers where repeated growth or relocation is measured. Tile/block loops to reuse a bounded working set; preserve cross-element dependencies. Compare bytes loaded/stored per useful result, cache/TLB behavior, memory bandwidth, and allocation. Include random single-record access, scatter/gather, structural movement, and unused capacity; a streaming SoA win can lose for whole-record lookups.

## M6 Addressing, prefetch, and alignment

Compare entity-to-row tables, component-to-storage indexes, cached column offsets, chunk handles, and arena-relative offsets against repeated searches. Define invalidation for resize, relocation, deletion, schema changes, ID reuse, and generation wrap. A `uint`/32-bit index is not a native pointer: never truncate addresses. Specify capacity, sentinel values, scale/base, overflow, and stale-handle detection.

For known future indirect addresses, establish that the loop is latency-bound, the future address is available early enough, and ordinary access ordering or cache blocking cannot expose equivalent locality. Hardware prefetch usually handles regular linear streams; software prefetch is a stronger candidate for predictable pointer chasing, indexed gathers, or strides it does not learn.

Compare no prefetch against a sweep of look-ahead distances—for example one, two, four, and eight iterations or cache lines—using supported operations such as guarded `Sse.Prefetch0` (`PREFETCHT0`). Too short fails to hide latency; too long can evict the line before use. Compare temporal hints with a non-temporal hint only when reuse policy supports it. Charge future-address calculation, added µops, code size, outstanding misses, bandwidth, and cache pollution. Record useful-prefetch rate or cache-miss evidence where available, not elapsed time alone.

`Sse.Prefetch0` accepts a raw pointer. Guard `Sse.IsSupported`, keep the address inside a live owned allocation, and pin managed storage for the entire address derivation and intrinsic call. Do not rely on prefetch fault suppression to make an invalid, null, stale, or out-of-range address acceptable. Retain a no-prefetch fallback and document the tuned target because the useful distance and hint are microarchitecture- and workload-dependent.

Test alignment separately. `StructLayout`, `LayoutKind.Explicit`, `FieldOffset`, and padding describe layout but do not automatically align the allocation base. Use verified allocation/access contracts when an intrinsic requires alignment. `Unsafe.SizeOf<T>()` reports a managed type's size; `Marshal.SizeOf<T>()` describes its unmanaged representation and is not a universal substitute. Test legal unaligned and cache-line-crossing cases. Include pinning, fragmentation, and extra footprint.

## M7 Copies and buffer traffic

**Look for:** `ToArray`, `ToList`, repeated UTF encoding, concatenation, boxing, intermediate serialization buffers, and scalar copy/fill loops.

Compare slicing with `Span<T>`/`ReadOnlySpan<T>` instead of copying; use `Memory<T>`/`ReadOnlyMemory<T>` for lifetimes that cannot stay in a stack-only view. When a copy is required, compare library `CopyTo`, `SequenceEqual`, `Fill`, `Clear`, `Array.Copy`, `Buffer.BlockCopy`, and `Buffer.MemoryCopy` where type and lifetime semantics match. They may already use tuned runtime implementations such as unrolled/vectorized loops or string moves.

Treat block copy as a size- and semantics-dependent experiment. First define element versus byte count, overlap direction, source/destination lifetime, initialization, destination capacity, alignment, and whether the destination is consumed immediately. `Span<T>.CopyTo` and `Buffer.MemoryCopy` define overlap-safe behavior; `Unsafe.CopyBlock`/`CopyBlockUnaligned` do not supply general `memmove` semantics. Compare fixed tiny sizes, medium blocks, and cache-exceeding streaming sizes under aligned/unaligned, hot/cold, overlapping/non-overlapping, and immediately reused/write-only destinations. Inspect emitted calls or `REP MOVSB`/SIMD code rather than assuming a manual AVX2 loop is superior.

Manual 128/256-bit copies are justified only after the runtime primitive loses for a stable size/alignment/use pattern. Prove scalar prefix/tail handling and legal access width. Non-temporal stores are a separate hypothesis for sufficiently large write-only destinations that will not be read soon; include alignment, ordering/fence obligations, cache pollution, memory bandwidth, and the crossover size. Measure the complete producer-consumer path, not copy bandwidth in isolation.

Use `BinaryPrimitives.Read*Endian`/`Write*Endian` for explicit byte order and `IBufferWriter<T>` for direct output when the producer can safely target destination storage. Fuse passes only after checking overlap, aliasing, exception/partial-write behavior, and increased register pressure. Measure producer-to-consumer bytes and ownership costs, not only the isolated copy.

For fixed-width byte records such as RGBA pixels, compare a packed `uint` representation and one full-width output store with four byte stores. On x86-64, the useful instruction-level hypothesis is one `mov dword ptr [dst], reg` (or, for two suitable records, `mov qword ptr [dst], reg`) instead of multiple `mov byte ptr [dst+n], reg8` stores plus byte extraction and address generation. The integer register already holds the packed bit pattern; the existence of byte subregisters does not make extraction or four stores free. Inspect the actual JIT code for shifts, masks, store count, spills, and bounds branches. `MemoryMarshal.Cast<byte,uint>` or endian-aware `BinaryPrimitives` may expose this layout, subject to their alignment/platform contracts. Prove byte order, complete writes, record boundaries, tails, and legal access width; use a correct big-endian path where needed. A 64-bit store helps only when its packing cost and alignment/tail constraints are justified by measurement. This scalar packing experiment is separate from SIMD and reciprocal arithmetic.



## Original: RUNTIME.md

# Dispatch, compiler visibility, and frozen configuration

## R1 Dispatch and lookup tables

**Look for:** long repeated `if` chains over bounded operations, repeated reflection, delegate creation, invariant type selection, and indirect calls in a tight loop.

Distinguish three mechanisms:

| Table contents | Mechanism | Concrete comparison |
|---|---|---|
| Precomputed values | Lookup table (LUT) | Array indexing versus repeated arithmetic/conditional assignments; include table construction and cache footprint. |
| Code addresses, function pointers, delegates | Dispatch table / jump table | `switch`, `if` chain, cached `Action`/`Func`, supported `delegate*`, and direct typed calls under actual target distributions. |
| Data locations | Indirection/location table | Entity-to-row or chunk/column mapping; follow [addressing](MEMORY.md#m6-addressing-prefetch-and-alignment). |

Validate an operation index before indexing. Preserve default/error behavior, call order, and side effects. Build tables once only when their inputs are stable. A function-pointer table retains an indirect call and target-prediction cost. A `switch` may already lower to a jump table, tree, or comparisons. Computed goto, such as GNU C `goto *targets[index]`, is a native compiler extension, not a C# facility.

For opcode dispatch, distinguish rearranging direct conditional branches from replacing them with a lookup or indirect jump. Test the observed tag distribution and a losing distribution; keep payload-length checks and malformed-input behavior equivalent. Inspect the emitted comparison order, fall-through blocks, branch targets, and any jump table after Tier 1/dynamic PGO. Source-order changes may be neutralized or reversed by the JIT, and faster dispatch alone does not establish fewer hardware branch misses.

Cache reflection discovery with `MethodInfo.CreateDelegate` and retain the typed delegate when repeated reflection is measured. Avoid per-call closures or new delegates. Compare monomorphic, few-target, many-target, and skewed dispatch. Account for inlining lost through indirect calls, instruction-cache footprint, security/runtime constraints, and setup. A value LUT can introduce memory latency; a dispatch table is not inherently branchless or faster.

## R2 Inlining, specialization, and copies

**Look for:** surviving interface/virtual/accessor calls, delegate invocation, boxing/unboxing, closure allocation, defensive struct copies, and generic runtime resolution in caller assembly.

Compare normal JIT decisions against concrete typed calls, sealed or otherwise provably monomorphic targets, suitable generic constraints, known component combinations, and smaller visible helpers. Inspect devirtualization, guarded devirtualization, constant propagation, common-subexpression elimination, loop invariants, bounds-check elimination, and vectorization enabled by call visibility.

For large structs, compare by-value, `ref`, `in`, and `ref readonly` forms. Use readonly members where valid to avoid defensive copies; inspect the actual caller. Tiny copies may be cheaper than an extra indirection, and `in` alone does not guarantee no copy. Preserve aliasing, ref escape, mutation, and lifetime contracts.

Test `MethodImplOptions.AggressiveInlining` only when ordinary decisions leave a measured problem. `NoInlining` can isolate a diagnostic experiment. Avoid blanket `AggressiveOptimization`: it bypasses the first tier and can forgo dynamic-PGO benefits. [MethodImplOptions](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.methodimploptions?view=net-10.0)

Account for Tier0/Tier1, OSR, dynamic PGO, ReadyToRun, JIT startup, Native AOT code size, generic specialization/monomorphization, register pressure, and instruction-cache growth. Native `always_inline` and LTO are related compiler controls, not substitutes for .NET contracts. Validate startup and steady-state deployment settings separately. [Dynamic PGO design](https://github.com/dotnet/runtime/blob/main/docs/design/features/DynamicPgo.md)

## R3 Startup-frozen feature flags

**Look for:** a process-wide startup choice still loaded or branched on during every hot operation, including disabled diagnostics that construct arguments before checking the flag.

Capture the primitive once in a non-generic `static readonly` field. Configure before any path initializes it. Use an explicit static constructor to avoid `beforefieldinit` timing, and initialize that holder before binding/invoking hot entry points. An optimizing CoreCLR JIT can fold an initialized `initonly` field and remove its dependent code; this is a behavior to verify, not a C# guarantee.

```csharp
internal static class StartupFeatures
{
    internal static readonly bool DiagnosticsEnabled;

    static StartupFeatures()
    {
        DiagnosticsEnabled = AppContext.TryGetSwitch("Example.Diagnostics", out bool value)
            && value;
    }
}

public static class Kernel
{
    public static void Initialize() =>
        System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(
            typeof(StartupFeatures).TypeHandle);

    public static int Compute(int value)
    {
        if (StartupFeatures.DiagnosticsEnabled)
            Console.WriteLine($"Processing {value}");
        return unchecked(value * 2);
    }
}
```

The host calls `AppContext.SetSwitch("Example.Diagnostics", enabled)` before `Kernel.Initialize`, then starts processing. For dynamic DLL loading: configure switches, `Assembly.LoadFrom`, discover the type/methods once, invoke `Initialize` through `CreateDelegate<Action>`, bind the hot method with `CreateDelegate<Func<int,int>>`, then reuse that delegate. Loading the assembly does not initialize every type.

Keep the holder non-generic to avoid shared-generic static-field resolution complications. `RuntimeHelpers.RunClassConstructor` completes one type initializer; it does not compile the hot method, rerun the initializer, or force existing native code to recompile. Do not invoke hot consumers from inside the flag initializer.

Place all feature-only argument construction, clock reads, allocation, registration, buffer work, and calls inside the guard. A hot call to `AppContext.TryGetSwitch` reads mutable switch state and is not the same constant-folding opportunity. A `static readonly Options` reference does not freeze the object's fields.

Freeze once. Later switch changes do not update the captured field. Do not apply this to per-instance or runtime-toggle semantics. AppContext switch names are process-wide configuration keys in CoreCLR; separately configurable plugin instances need another contract. Native AOT cannot JIT-specialize on a startup-selected value and does not support the same dynamic managed-DLL loading model.

**Verify in separate processes:** test both true and false behavior; inspect actual optimized native code with `DOTNET_JitDisasm`, BenchmarkDotNet disassembly, or the runtime's supported dump. In the disabled path, look for the absence of the flag load, conditional branch, argument preparation, and feature calls. Inspect an inlining caller if there is no standalone method body. IL still containing the guard is expected.

For an isolated diagnostic check, `DOTNET_ReadyToRun=0` and `DOTNET_TieredCompilation=0` can simplify inspecting optimized JIT output after explicit initialization. Restrict these to the test process and restore deployment settings for acceptance. In deployment, Tier0/ReadyToRun may retain a check that Tier1 later removes. Keep evidence for the actual steady-state tier and both configurations. [JIT diagnostics](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/viewing-jit-dumps.md), [static-field optimization contract](https://learn.microsoft.com/en-us/dotnet/standard/unsafe-code/best-practices#3-internal-implementation-details-of-the-runtime-and-libraries)



## Original: STORAGE.md

# Bitmaps, lookup structures, queries, and compression

## S1 Bitmaps and bulk updates

**Look for:** `bool[]` or per-item flags, component signatures, occupancy, dirty sets, repeated membership tests, many independent comparisons, and bulk state changes.

Compare `System.Collections.BitArray`, packed `ulong[]`, and sparse indexes under actual density and mutation. For word storage, `wordIndex = index >> 6`, `bitIndex = index & 63`, and `mask = 1UL << bitIndex` identify a bit after logical-range validation. Use word AND/OR/XOR/AND-NOT for intersection, union, toggles, and removal.

For at most 32 or 64 stable flags, compare a single `uint`/`ulong` bitmask with separate booleans, sets, or repeated predicates. Define each bit symbolically; test `flags & required`, equality-to-mask, zero/nonzero membership, set (`|`), clear (`& ~`), toggle (`^`), and changed-bit (`old ^ next`) operations as distinct semantic transformations. A bitmask can remove branches or compress working state, but it can also add packing/unpacking and obscure invariants. Do not replace short-circuiting operations with eager mask construction when skipped work, side effects, exceptions, or memory safety matter.

Inspect whether the compiler already forms `TEST`, `BT`, `AND`, `OR`, `XOR`, conditional moves, or vector masks. Compare sparse lists when few bits are set and direct booleans when the domain is tiny or mutations dominate. Document unused/reserved bits, serialization/versioning, signedness, shift width, and complement masking. Packed flags sharing one word are one synchronization unit: concurrent point updates require a valid atomic/ownership protocol and can create true sharing.

For signatures, require `(present & required) == required` and `(present & excluded) == 0` for every logical word. Optional terms and value predicates require their own semantics. To iterate set bits, first require `word != 0`, obtain `BitOperations.TrailingZeroCount(word)`, then use `word &= word - 1`. `BitOperations.PopCount` counts bits; whether a bit represents an entity depends on the encoding.

```csharp
static bool Matches(ulong present, ulong required, ulong excluded) =>
    (present & required) == required && (present & excluded) == 0;

static ulong LogicalTailMask(int usedBits)
{
    if ((uint)usedBits > 64u)
        throw new ArgumentOutOfRangeException(nameof(usedBits));
    return usedBits == 64 ? ulong.MaxValue : (1UL << usedBits) - 1UL;
}
```

The explicit 64 case avoids C#'s masked shift-count behavior. Keep unused final-word bits clear or mask them consistently, especially after NOT/complement, equality, and population count. Define logical length independently of buffer capacity.

For many words, compare scalar loops against `Vector128/256/512` and `Avx2.And`, `Avx2.Or`, `Avx2.Xor`, `Avx2.CompareEqual`, or `AdvSimd` operations. Include packing, mask extraction, tails, and output compaction. Follow the [lane/mask rules](CPU.md#c3-simd); byte-mask bits must not be counted as entity matches.

For repeated bulk changes, test a backbuffer: compute new state into exclusively owned storage, form `changed = old ^ next`, iterate changed bits, then publish at the existing visibility boundary. Define initialization, swap ownership, reader lifetime, reclamation, and failure behavior. An atomic reference swap alone does not make it safe to overwrite a buffer still held by readers. Charge both buffers, clearing/copying, publication, and any delayed visibility.

Test empty, sparse/dense, no/all matches, first/last bit, partial words, duplicate updates, wide signatures, and vector tails. Point updates in the same word can cause lost updates or true sharing; packed representation does not supply synchronization.

## S2 Lookups and hashing

**Look for:** `ContainsKey` followed by an indexer, repeated hashes, expensive equality, bucket/probe chains, tombstones, rehashing, sparse IDs, and repeated resolution of the same object.

Separate hash computation, probe traversal, full key equality, payload access, and synchronization. Compare:

- `TryGetValue` against duplicate dictionary lookups.
- A direct array for dense bounded IDs; include initialization, sparse-range waste, and resize cost.
- A sorted array with binary search for stable sets, or a linear scan for genuinely small sets.
- A bitset for membership-only domains.
- A retained handle/location with generation validation for repeated access.
- Canonicalized immutable keys and cached hashes when normalization has an amortized benefit.

For measured read-modify-write duplication, consider `CollectionsMarshal.GetValueRefOrAddDefault` or `GetValueRefOrNullRef` on a `Dictionary<TKey,TValue>`. Check `Unsafe.IsNullRef` before using a possibly absent reference. Do not add/remove entries or resize the dictionary while a returned reference is in use. Initialize a newly inserted value, preserve failure semantics, and maintain the existing synchronization contract. A ref return is not thread safety. [CollectionsMarshal](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.collectionsmarshal?view=net-10.0)

Preserve comparer, culture, ordering, duplicate, and query semantics. Hash equality is not key equality; retain full equality on collisions. An immutable wrapper does not make a mutable key safe. Test hit/miss ratio, hot-key skew, key cardinality/range, collision cases, deletion/reinsertion, growth, and working set. Include construction and maintenance in the decision.

## S3 Frozen collections

**Look for:** long-lived `Dictionary<TKey,TValue>`/`HashSet<T>` instances that are built once and read repeatedly.

```csharp
static System.Collections.Frozen.FrozenDictionary<string, int> BuildCodes()
{
    var source = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        ["small"] = 10,
        ["medium"] = 20,
        ["large"] = 30
    };
    return System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(
        source, StringComparer.OrdinalIgnoreCase);
}
```

Create `FrozenDictionary<TKey,TValue>` with `ToFrozenDictionary`, or `FrozenSet<T>` with `ToFrozenSet`, outside repeated lookup. Preserve the explicit comparer. Compare against ordinary dictionaries, sorted/direct arrays, and small scans using the actual keys, hits/misses, cardinality, and runtime.

Frozen collections are available from .NET 8. Construction may select specialized layouts, including string-key strategies, and can cost more than ordinary collection construction. Measure build cost, lookup saving, retained bytes, and actual reuse before selecting it. Keys should be trusted when expensive construction behavior matters. Frozen membership does not deeply freeze objects stored as values. `ImmutableDictionary` serves persistent modified versions; it is not an interchangeable performance target. [FrozenDictionary contract](https://learn.microsoft.com/en-us/dotnet/api/system.collections.frozen.frozendictionary-2?view=net-10.0)

For reloadable configuration, compare rebuilding and publishing a complete frozen snapshot. Charge old-reader retention, reconstruction, and publication. Do not rebuild a frozen table on every lookup or quietly remove required mutations.

## S4 Storage and query preparation

**Look for:** repeated normalization, component signature matching, archetype enumeration, column discovery, general entity lookups inside dense iteration, and invariant per-entity checks.

Map the actual archetype/chunk, sparse-set, dense pool, or hybrid storage from entity ID to component row. Measure query construction, preparation, matching, iteration, writes, and structural movement separately.

Compare cached normalized requirements/exclusion masks, canonical query keys, prepared query plans, matching archetypes, component-to-column maps, and selection from the rarest required component. Cache complete key equality even when hashes match. Optional components must not yield an invalid column access.

Distinguish cached storage groups from cached entity results. Group membership can survive entity movements while an entity-result cache may depend on membership, values, enable state, relationships, and publication epochs. List dependencies and invalidation for storage creation/destruction, schema/layout changes, relocation, ID reuse, and relevant value changes. Cached indexes or references cannot outlive their version/ownership contract.

Benchmark one-shot versus repeated queries and low/high structural churn. Charge preparation, maintenance fanout, memory, empty groups, and fragmentation. Test required/excluded/optional terms, equivalent key orderings, stale handles, immediate visibility, and transitions. [Flecs query documentation](https://www.flecs.dev/flecs/Queries.html) supplies an archetype-cache model; adopting Flecs is not required.

## S5 Compression and runs

**Look for:** measured long repeated metadata/occupancy runs, contiguous match ranges, sorted IDs, and bandwidth-limited scans.

Measure run-length and value distributions before selecting an encoding. Compare uncompressed arrays, bitmaps, sparse lists, interval/range lists, run-length encoding (RLE), and delta encoding where order permits. For RLE, store a value and run length; define split/merge rules for updates. Compare direct processing of encoded ranges against decompression to a scratch buffer.

Include headers, lengths, alignment, retained capacity, skip indexes, prefix counts, decompression buffers, encode/decode cost, random lookup, and mutation amplification. Sorting to manufacture runs costs time and can change observable ordering.

Test all-equal, long-run, clustered, alternating, fragmented, and random inputs. Round-trip and differential-test zero/invalid run lengths, cumulative overflow, truncated input, first/last element, adjacent equal runs, and output capacity. Retain compression only when the prioritized complete operations improve within predefined memory/latency limits. Add adaptive representation thresholds only if their measurement and switching costs are justified.



## Original: CONCURRENCY.md

# Ownership, coherence, synchronization, and scheduling

## T1 False sharing

**Look for:** independently owned counters/flags/queue cursors on different cores, throughput that degrades as workers increase, and cache-coherence traffic despite disjoint logical data.

False sharing occurs when distinct variables share a coherence line and at least one core writes. It includes writer/writer and writer/reader interference. Two readers alone do not cause the same invalidation mechanism. Actual writes to the same variable or bitmap word are true sharing; padding does not remove that synchronization requirement.

Compare thread-local accumulation followed by a permitted merge, less frequent publication, and cache-line padding/alignment. Inspect `StructLayout`, `LayoutKind.Explicit`, `FieldOffset`, array stride, and the allocation base. A padded type does not automatically make its base cache-line aligned. Use the relevant target's coherence-line size, not an unexplained universal constant, and prove the actual written byte ranges occupy distinct lines under the supported allocation contract.

Test alternatives separately: per-worker state followed by a merge, batched publication, chunk/range partitioning that keeps neighboring writes on one worker, an array stride that places hot write fields on distinct lines, and explicit padding/alignment. Keep read-mostly metadata away from frequently written cursors when readers otherwise invalidate with writers. Padding increases footprint and can worsen cache/TLB behavior, so retain it only with a measured scaling benefit.

Measure unpadded versus padded layouts with identical useful work and a realistic multiworker harness. Sweep one worker through physical-core and SMT counts; include one writer/one reader, writer/writer, NUMA where applicable, affinity/migration, memory footprint, TLB/cache capacity, and merge cost. Use coherence/HITM or cache-to-cache counters when available, or a controlled field-offset experiment when they are not. Do not attribute every scalability problem to false sharing without counter or controlled-layout evidence; lock contention, memory bandwidth, true sharing, scheduler migration, and load imbalance can produce similar curves.

## T2 Ownership and publication

Before changing primitives, enumerate readers, writers, scheduler/threads, callbacks/reentrancy, mutable storage, reference-returning APIs, and existing synchronization. Specify immediate visibility/read-your-writes, publication points, and the complete lifetime of each reference, span, index, or snapshot.

Compare disjoint partitions, thread confinement, exclusive leases, thread-local accumulation with a controlled merge, immutable snapshots, copy-on-write, and deferred command/structural-change buffers where delayed visibility is permitted. Prove disjointness including neighboring updates and shared metadata. Charge acquisition, partition setup, skew, merge, copy, publication, retained versions, and reclamation.

An unsynchronized internal path is valid only under single-threaded non-reentrant use, exclusive ownership for the whole lifetime, or one caller-supplied external synchronization protocol used by every conflicting access. It is not a general lock-free algorithm. Do not provide a Boolean that bypasses protection while other callers concurrently mutate the same state.

For snapshots/backbuffers, specify reader retention and when an old buffer can be reused. `Volatile.Read`, `Volatile.Write`, and `Interlocked.Exchange` can participate in publication; they do not independently solve ownership, reclamation, or atomic compound changes. A readonly view does not prevent another owner from changing its backing storage.

Test ownership transfer, stale references, resize/relocation, callback reentrancy, canceled merges, disposal, reader lag, and failure during publication. Preserve required ordering and visibility. Stress tests supplement a stated protocol and invariants.

## T3 Locks and waiting

**Look for:** acquisition time, wait time, hold-time tails, spin CPU, retries, context switches, queueing, and starvation. Read the actual custom or "slim lock" source before choosing a baseline; its name does not identify its semantics.

Record state representation, recursion, upgrade rules, reader concurrency, wakeups, exception behavior, and memory ordering. Compare narrower scopes, batching, sharding/lock striping, `Monitor`, `ReaderWriterLockSlim`, and `System.Threading.Lock` on supported targets while preserving equivalent useful work and visibility. For shards, define stable assignment, resize behavior, and cross-shard lock order.

`System.Threading.Lock` is mutual exclusion, not concurrent-reader locking. Its typed C# `lock`/`EnterScope` protocol differs from `Monitor` locking through another static type. Do not mix protocols on the same object. References returned under a lock must not outlive the protection required to keep their storage valid. [C# lock semantics](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock)

Compare blocking, fixed bounded spinning, `SpinWait.SpinOnce`, `NextSpinWillYield`, and a measured adaptive spin-then-block policy. `SpinWait` is a meaningful baseline; an empty busy loop is not. Search `PAUSE`, yield, park/unpark, event, condition variable, owner preemption, fairness, and oversubscription. [SpinWait guidance](https://learn.microsoft.com/en-us/dotnet/standard/threading/spinwait)

Base adaptation on observed wait/hold time, contention, or retries. Define sample interval, hard spin/CPU bound, backoff, EWMA/hysteresis, and fallback. Keep observation overhead in the result. Use one acquisition protocol with a correct predicate recheck/parking transition; avoid lost wakeups. A short accessor does not imply that the owner will promptly release the lock.

For custom atomics or lock-free candidates, specify the state machine, `Interlocked.CompareExchange`/CAS loop, linearization point, acquire/release publication, progress guarantee, ABA exposure, retry behavior, and safe reuse via an appropriate epoch/hazard-pointer or other reclamation protocol. `volatile` is neither an atomic read-modify-write nor a complete concurrency algorithm. Start this complexity only for a remaining measured problem.

Use sustained tests across one/many cores, reader/writer ratios, short/long holds, skew, owner preemption, changing contention, and oversubscription. Report throughput, total CPU, retries, context switches where available, per-thread progress, fairness, writer starvation, and p50/p95/p99/max complete-operation latency. Uncontended acquisition nanoseconds and `ThreadingDiagnoser` alone do not establish a better lock.

## T4 Scheduling, batching, and I/O

**Look for:** `Task.Run` per tiny item, fine-grained `Parallel.For`, repeated queue handoffs, blocking waits on asynchronous work, small I/O calls, and more runnable workers than useful execution capacity.

Compare coarse contiguous range partitioning, worker-local batches, `ParallelOptions.MaxDegreeOfParallelism`, bounded `Channel<T>`/producer-consumer queues, and fewer handoffs. Preserve cancellation, ordering, backpressure, error propagation, and required publication latency. Include load imbalance, queue memory, and batch fill delay; throughput can improve while p99 latency worsens.

For CPU-bound multithreading, begin with a correct sequential baseline and prove that iterations or partitions have disjoint writes and stable read inputs. Compare `Parallel.For`/`Parallel.ForEach`, explicit range partitioning, or an existing scheduler using coarse partitions and partition-local state. Sweep degree of parallelism from one worker through physical cores and, separately, SMT capacity instead of assuming `Environment.ProcessorCount` is optimal. Avoid nested parallelism unless the outer layer cannot occupy the machine and the combined worker bound is explicit.

Measure strong scaling for a fixed workload and, when relevant, weak scaling as work grows with workers. Report speedup, parallel efficiency, wall time, total CPU, allocation, scheduler/queue delay, barriers, merge/reduction cost, imbalance, memory bandwidth, NUMA effects, and tail latency. Amdahl's serial fraction, bandwidth saturation, synchronization, false sharing, and cache-capacity pressure usually bound scaling before worker count does. Include small workloads where launch and partition overhead dominate.

Preserve exception aggregation, cancellation, deterministic ordering where required, synchronization context/thread affinity, random-number ownership, and reproducibility. Use thread/partition-local accumulation and a defined merge rather than shared writes in the inner loop. Treat a parallel implementation as a maintainability and resource-consumption tradeoff: a speedup that monopolizes the host, harms co-tenants, or regresses latency under realistic concurrency may require a bounded default or user decision.

For I/O-limited paths, compare existing supported bulk APIs, buffer reuse, and `ReadAsync`/`WriteAsync` where asynchronous waiting avoids occupying workers. `async` does not accelerate CPU arithmetic or the external device. `ValueTask`/`ValueTask<T>` is a separate allocation experiment for suitable completion patterns; preserve its consumption contract rather than replacing every `Task` mechanically.

Measure enqueue-to-completion and request tails as well as active kernel time. Inspect thread-pool starvation, context switches, external latency, and service limits. Use verified isolated fixtures for mutating I/O; never run a benchmark against an unverified database or external write target to discover its effects.



## Original: BENCHMARKING.md

# Profiling and benchmark protocol

## Locate cost

Use sampled stacks and phase measurements together. Search for sampling profiler, call tree, flame graph, inclusive/exclusive time, critical path, off-CPU time, queue delay, IPC/CPI, retired instructions, front-end/back-end bound, execution ports, LLC/TLB misses, bandwidth, GC pauses, lock contention, context switches, and oversubscription.

For .NET, consider `dotnet-trace`, EventPipe, ETW, PerfView, and `dotnet-counters`. Native/system tools include Linux `perf`, Intel VTune, and Instruments. For graphics, separate CPU preparation/submission, GPU execution, upload, GPU fence waits, VSync, frame caps, and frame pacing. Select tools supported on the actual platform.

Record useful work per operation and operation frequency. Do not add inclusive parent time to its children's time or sum overlapping thread durations as elapsed time. Use the measured critical-path fraction to bound possible application gain. Amdahl's expression `1 / ((1-f) + f/s)` applies to the stated serial-fraction model; it does not automatically model overlapping CPU/GPU stages.

If adding probes, use the repository's telemetry mechanism. Gate clocks, registration, buffers, message preparation, and recording together. Measure enabled/disabled overhead and a build with probes absent. Keep algorithms identical. Sample process CPU coarsely and state whether 100% means one logical CPU or the whole machine. `GC.GetAllocatedBytesForCurrentThread` cannot follow an operation across asynchronous thread changes.

## Construct the rig

Use BenchmarkDotNet for managed microbenchmarks unless an existing calibrated harness is more representative. Capture its package version and generated worker environment. Use `[Benchmark(Baseline = true)]`, `[Params]` or `[ParamsSource]`, `[GlobalSetup]`, and appropriate return values or `BenchmarkDotNet.Engines.Consumer`. Use `BenchmarkSwitcher` when command-line filtering is required.

Keep fixture construction outside kernel timing and measure construction separately if the real caller pays it. Parameterize real sizes/distributions, not just convenient powers of two. Prevent dead-code elimination by consuming the actual result. Avoid constant inputs that let the compiler erase work the application performs dynamically. Do not disable useful optimization merely to make timing easier.

For mutable kernels, restore equivalent initial state between measured operations. `[IterationSetup]` is not automatically a reset before every invocation in a multi-invocation iteration; inspect the job settings. Either use an explicitly controlled invocation strategy, prepare independent cases, or measure reset as part of the intended operation. State the resulting cache effects. An ever-growing dictionary or repeatedly emptied bitmap is not a stable comparison.

Configure `MemoryDiagnoser`, `DisassemblyDiagnoser`, and, for concurrency, `ThreadingDiagnoser` where supported. BenchmarkDotNet may use separate diagnostic runs; associate artifacts with the correct job. See its [diagnoser documentation](https://benchmarkdotnet.org/articles/configs/diagnosers.html). Threading statistics do not replace operation-level latency or starvation measurements.

Use Release builds without a debugger. Record source revision plus uncommitted changes or hashes, exact commands, SDK/runtime/compiler, process architecture, CPU model/ISA, OS, GC mode, JIT/AOT/ReadyToRun, Tier0/Tier1/OSR, dynamic PGO, build flags, affinity, power policy, and background load. Verify the worker's actual runtime, not only a display label.

Separate startup from steady state. Allow tiering/PGO to settle, and inspect the code tier used in deployment. For deliberately cold data, define cache-state preparation; a freshly allocated array does not prove a cache miss. Record thread migration and thermal/frequency drift when they affect interpretation.

## Workload contrasts

| Dimension | Select meaningful contrasts |
|---|---|
| Size | empty, singleton, below-vector, exact-vector, tail, cache-resident, beyond LLC |
| Access | sequential, strided, random, indirect, clustered |
| Predicate | none/all, sparse/dense, random half, alternating, long runs, early/late exit |
| Reuse | one-shot/repeated, build-to-read ratio, mutation and structural churn |
| Shape | record size, signature width, ID density, run length, fragmentation |
| Concurrency | one/many threads, reader/writer ratio, skew, short/long holds, oversubscription |
| Platform | deployed runtime/CPU/ISA, accelerated path and scalar fallback |

Select cases that can falsify the mechanism. Avoid a gratuitous Cartesian product. Preserve fixture seeds and use fresh sequences as well as repeats when predictor/cache training could bias results.

## Compare and inspect

Run independent baseline/candidate repetitions; alternate or randomize their order when drift is plausible. Serialize competing timed workloads. Report units, sample counts, mean/median, spread or confidence interval, and relevant p50/p95/p99/max latency. Repetition counts must support the observed effect and variance. Do not prescribe one universal gain percentage or accept a single fast run.

Inspect the hot caller and loop for actual bounds checks, loads, branches, calls, SIMD, register spills, code size, and allocation. An empty disassembly export is not evidence. Obtain an appropriate compiler/JIT dump or mark the mechanism unverified. Do not infer branch misses, cache misses, or execution-port saturation from elapsed time alone.

For Linux tools with suitable event access, these commands are initial probes, not portable prerequisites:

```bash
perf stat -r 5 -e cycles,instructions,branches,branch-misses -- ./program
perf record -e branch-misses:u -g -- ./program
perf report
```

Compare absolute misses, cycles, instructions, and elapsed time. A miss percentage changes when its denominator changes. Note event availability, multiplexing, sampling skid, and attribution limits. Precise events or branch-history recording depend on hardware/tool support. [perf stat](https://man7.org/linux/man-pages/man1/perf-stat.1.html)

Use diagnostic controls such as `NoInlining`, disabled tiering, or disabled ReadyToRun only to answer a specific question. Restore deployment settings for the acceptance run. An optimization obtained by silently changing both the code and compiler configuration has no isolated explanation.

## Total cost and correctness

Account for preparation + execution + cache/index maintenance + encoding/decoding + mutation/relocation + allocation/GC + synchronization/publication. Include retained buffers, padding, tables, pool capacity, old snapshots, and code-size costs. For preprocessing cost `B` and per-use saving `d`, the idealized break-even reuse is `B/d` when `d > 0`; include recurring maintenance separately.

Use a safe independent reference and meaningful differential tests. Cover invalid input and required exception behavior, aliasing, overflow, partial output, fallback ISA, floating-point boundaries, reference lifetimes, and concurrency state invariants. A test that repeats the optimized algorithm is not an independent oracle.

Set correctness requirements, material benefit, workload weights, and regression limits before observing candidate results. Retain only repeatable representative benefits beyond noise. Inspect outliers and record any exclusion rationale. Recheck useful combinations and the complete caller path after integration.

## Evidence record

```text
Candidate / category / source location:
Hypothesis and result that would falsify it:
Baseline/candidate source identity:
Commands, fixtures/seeds, environment, deployment settings:
Executed correctness checks and required invariants:
Before/after: units, repetitions, central estimate, variability, latency tails:
Generated-code/counter evidence and missing diagnostics:
Build/setup, maintenance, memory, allocation, and publication costs:
Decision: retained / rejected / inconclusive / not applicable / blocked:
Raw artifact paths, regression cases, limitations, and next hypothesis:
```

Keep a rejected candidate's evidence even after reverting its code. Reprofile retained changes and update the catalogue ledger. If the test cannot run, say so; a predicted result is not a measurement.



## Original: SOURCES.md

# Technical source index

Read the source for the particular API, instruction, or compiler behavior under test. Match its version to the actual target. A source supplies a contract or hypothesis; benchmark results still require local evidence.

## Supplied array-access studies

- [Faster C# array access](https://blog.tedd.no/2020/06/01/faster-c-array-access/): local array references, bounded spans, and highest-index-first experiments.
- [Why C# multidimensional arrays are slow](https://blog.tedd.no/2021/10/07/why-c-multidimensional-arrays-are-slow/): rectangular, jagged, and flat-array addressing. Recheck present JIT behavior; historical object-header layouts and speed ratios are not portable contracts.
- [Unsafe reference arithmetic in .NET](https://blog.tedd.no/2022/03/07/unsafe-reference-arithmetic-in-net/): low-level access experiments. Its raw-pointer example does not establish pinning. Prefer the managed-byref recipe or explicit pinning and compare equivalent side effects.

## .NET memory and collections

- [Unsafe-code best practices](https://learn.microsoft.com/en-us/dotnet/standard/unsafe-code/best-practices): managed byrefs, GC relocation, pinning, initialization, and range obligations.
- [Unsafe.Add](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.unsafe.add?view=net-10.0): element offsets and distinct byref/pointer overloads.
- [MemoryMarshal.Cast](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.memorymarshal.cast): span reinterpretation, endian examples, and misaligned-access limits for packed scalar I/O.
- [GC.AllocateUninitializedArray](https://learn.microsoft.com/en-us/dotnet/api/system.gc.allocateuninitializedarray?view=net-10.0), [NativeMemory](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.nativememory?view=net-10.0), [stackalloc](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/stackalloc): allocation and lifetime contracts.
- [CollectionsMarshal](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.collectionsmarshal?view=net-10.0): list spans and dictionary byrefs.
- [FrozenDictionary](https://learn.microsoft.com/en-us/dotnet/api/system.collections.frozen.frozendictionary-2?view=net-10.0), [FrozenSet](https://learn.microsoft.com/en-us/dotnet/api/system.collections.frozen.frozenset-1?view=net-10.0): fixed-membership lookup structures and construction tradeoffs.
- [BitOperations](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.bitoperations?view=net-10.0), [BinaryPrimitives](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.binary.binaryprimitives?view=net-10.0): high-level operations to compare before manual instruction selection.
- [Span.CopyTo](https://learn.microsoft.com/en-us/dotnet/api/system.span-1.copyto?view=net-10.0), [Buffer.MemoryCopy](https://learn.microsoft.com/en-us/dotnet/api/system.buffer.memorycopy?view=net-10.0): overlap-correct block-copy contracts.

## Measurement and compiler behavior

- [BenchmarkDotNet diagnosers](https://benchmarkdotnet.org/articles/configs/diagnosers.html), [hardware counters](https://benchmarkdotnet.org/api/BenchmarkDotNet.Diagnosers.HardwareCounter.html): diagnostics and platform restrictions.
- [Dynamic PGO design](https://github.com/dotnet/runtime/blob/main/docs/design/features/DynamicPgo.md), [JIT dumps](https://github.com/dotnet/runtime/blob/main/docs/design/coreclr/jit/viewing-jit-dumps.md), [MethodImplOptions](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.methodimploptions?view=net-10.0): tiering, profiling, generated code, and optimization hints.
- [.NET 11 runtime bounds-check elimination](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime): version-specific examples of range-analysis improvements; verify the deployed runtime's native code.
- [GCC builtins](https://gcc.gnu.org/onlinedocs/gcc/Other-Builtins.html), [Clang language extensions](https://clang.llvm.org/docs/LanguageExtensions.html), [Clang attributes](https://clang.llvm.org/docs/AttributeReference.html): likelihood versus unpredictability hints.
- [LLVM X86CmovConversion](https://llvm.org/doxygen/X86CmovConversion_8cpp.html), [LLVM BOLT](https://llvm.org/docs/BOLT/index.html): conditional moves can be converted back to branches; native binary layout can be profile-directed.
- [perf stat](https://man7.org/linux/man-pages/man1/perf-stat.1.html): counter collection, repetitions, and event limitations.

## CPU and vector operations

- [uops.info VMULSD measurements](https://uops.info/html-instr/VMULSD_XMM_XMM_XMM.html): exact instruction-form latency, reciprocal throughput, and execution ports by microarchitecture.
- [Intel Intrinsics Guide](https://www.intel.com/content/www/us/en/docs/intrinsics-guide/index.html): SSE, AVX/AVX2/AVX-512, loads/stores, comparisons, shuffles, FMA, and ISA requirements.
- [Intel optimization reference manual](https://www.intel.com/content/dam/doc/manual/64-ia-32-architectures-optimization-manual.pdf): AVX/AVX2, software-prefetch distance, cache blocking, streaming stores, and memory-copy considerations.
- [Intel instruction-set manuals](https://www.intel.com/content/www/us/en/developer/articles/technical/intel-sdm.html): `MOV r/m8,r8`, `MOV r/m32,r32`, and `MOV r/m64,r64` operand forms for byte versus packed scalar stores.
- [Arm ACLE Advanced SIMD reference](https://arm-software.github.io/acle/neon_intrinsics/advsimd.html): NEON/AdvSimd operations and types.
- [.NET intrinsics](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics?view=net-10.0), [runtime vectorization guidance](https://github.com/dotnet/runtime/blob/main/docs/coding-guidelines/vectorization-guidelines.md): vector APIs and implementation constraints.
- [Avx2.IsSupported](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.x86.avx2.issupported?view=net-10.0), [Avx2 API](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.x86.avx2?view=net-10.0): exact AVX2 support guard and 256-bit integer intrinsic surface.
- [Single.FusedMultiplyAdd](https://learn.microsoft.com/en-us/dotnet/api/system.single.fusedmultiplyadd?view=net-10.0): single-rounding semantics.
- [Sse.Prefetch0](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.intrinsics.x86.sse.prefetch0?view=net-10.0): PREFETCHT0; a pointer parameter does not pin a managed object.
- [GCC Redwood Cove hint change](https://gcc.gnu.org/pipermail/gcc-cvs/2024-July/405854.html): narrow hardware-prefix behavior and its vendor-manual reference.

## Storage and synchronization

- [Flecs queries](https://www.flecs.dev/flecs/Queries.html): query caching, matching, and maintenance costs.
- [System.Threading.Lock](https://learn.microsoft.com/en-us/dotnet/api/system.threading.lock?view=net-10.0), [C# lock statement](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock): typed locking and Monitor distinctions.
- [SpinWait](https://learn.microsoft.com/en-us/dotnet/standard/threading/spinwait): yielding and two-phase waiting.
- [.NET data parallelism](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/data-parallelism-task-parallel-library), [parallelism pitfalls](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/potential-pitfalls-in-data-and-task-parallelism): partitioning, thread-local state, over-parallelization, and shared-state hazards.

When behavior depends on implementation details, record the runtime/compiler source revision or exact documentation version beside the experiment. Do not infer general speed guarantees from one historical benchmark or one instruction's throughput table.

