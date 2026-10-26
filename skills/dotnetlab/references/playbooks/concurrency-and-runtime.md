# Playbook: Concurrency, Async, Source Generation & Modern .NET Runtime (`PAR`)

Targets multi-core scaling bottlenecks (false sharing, lock contention, oversubscription), async/ThreadPool/backpressure overhead, build-time source generation, GC heap tuning, Native AOT vs Tiered PGO trade-offs, and `.NET 10` / `.NET 11` runtime features.

## 1. Multi-Core Scaling, Coherence & Synchronization

### `PAR-01` — Eliminate Cache-Line False Sharing
- **Clue:** Multi-threaded throughput flattens or **gets slower** as worker count grows from 1 -> physical cores despite workers writing to logically distinct counters, slots, or ring-buffer cursors.
- **Mechanism:** Distinct fields or adjacent array elements (`long counts[threadId]`) sharing a 64-byte (or 128-byte spatial-prefetch pair) coherence line bounce between L1 caches (`MESI` invalidations / `HITM`) on every write, including one-writer + concurrent-readers sharing the same line!
- **C# Experiment:**
  1. **Best fix — Thread-local accumulation + single merge:** Accumulate into a stack local inside each worker's partition loop and write/merge **once** after the partition finishes!
  2. **Cache-line padded structs (`[StructLayout(LayoutKind.Explicit, Size = 128)]`):** Separate hot writer cursors (e.g., `Head` vs `Tail` vs read-only `Mask`/`Buffer` fields in queues) onto distinct cache lines:
     ```csharp
     [StructLayout(LayoutKind.Explicit, Size = 128)]
     internal struct PaddedCounter
     {
         [FieldOffset(64)] public long Value;
     }
     ```
- **Verify / Trap:** `StructLayout` pads stride/offsets **relative to the struct start**, not the GC heap object base (though a 128-byte explicit size guarantees `Value` at offset 64 cannot share a 64-byte line with neighboring array elements!). Note that **true sharing** (multiple threads `Interlocked.Increment`ing the *same* counter) is not solved by padding—use sharded/thread-local counters for true sharing. Confirm scaling across `1 -> physical cores -> SMT`.

### `PAR-02` — Ownership Partitioning, Sharding / Lock Striping & Read Snapshots
- **Clue:** Contended central `lock` or `ConcurrentDictionary` serializing independent keys/entities, or read-heavy workloads stalling behind infrequent updates.
- **C# Experiment:**
  - **Disjoint Partitioning:** Assign contiguous key/array ranges to single workers so inner loops run completely lock-free.
  - **Lock Striping / Sharding:** Hash keys across $2^k$ independent shards (`shards[hash & mask]`); define a strict ascending shard acquisition order whenever an operation touches $>1$ shard to prevent deadlocks.
  - **Immutable Copy-on-Write Snapshot Publication:** For read-dominant state (99.9% reads, rare config/route/table updates), read a `Volatile.Read(ref _snapshot)` `FrozenDictionary` / immutable array with **zero locks**, and publish new snapshots on update via `Interlocked.Exchange` / `CompareExchange`.
- **Verify / Trap:** Ensure readers holding an old snapshot reference can finish safely without backing buffers being mutated or returned to `ArrayPool<T>` prematurely!

### `PAR-03` — `System.Threading.Lock`, `ReaderWriterLockSlim` & Critical-Section Scope
- **Clue:** Lock acquisition/contention overhead on `.NET 9+`, holding locks across formatting/allocation/I/O, or serializing heavy read-only work under mutual exclusion.
- **C# Experiment:**
  - On `.NET 9+`, replace `private readonly object _gate = new();` with dedicated **`private readonly System.Threading.Lock _gate = new();`** (`lock (_gate)` or `using (_gate.EnterScope())`), which lowers directly to `Lock` without object header `SyncBlock` indirection.
  - Move allocations, logging argument prep, hashing, and callbacks **outside** the critical section, or batch multiple items under one lock acquisition.
  - Compare `ReaderWriterLockSlim` only when read critical sections are genuinely non-trivial and vastly outnumber writes (for 10 ns getters, `ReaderWriterLockSlim`'s atomic reader-count bookkeeping often costs *more* than `Lock` due to cache-line bouncing on the reader count!).
- **Verify / Trap:** **Never upcast `System.Threading.Lock` to `object`** (`object o = _gate; lock (o)` locks via `Monitor` instead of `Lock`, breaking mutual exclusion with `_gate.EnterScope()`!). Never `await` inside synchronous locks.

### `PAR-04` — Bounded `SpinWait` vs Blocking & Lock-Free CAS Loops
- **Clue:** Excessive OS context-switch wakeups on ultra-short holds, or conversely 100% CPU burn from naive `while (!ready) { }` spin loops under oversubscription.
- **C# Experiment:**
  - Never write an empty busy-wait loop; use `SpinWait spinner = default; while (...) spinner.SpinOnce();` (`PAUSE` / `YIELD` + bounded transition to `Thread.Yield`/`Sleep` via `spinner.NextSpinWillYield`), or prefer hybrid primitives (`Lock`, `SemaphoreSlim`).
  - For custom `Interlocked.CompareExchange` (CAS) state machines, explicitly prove the linearization point, `Volatile`/acquire-release ordering, progress under contention, and **ABA / memory reclamation safety** (managed GC prevents raw pointer use-after-free, *not* logical ABA if pooled objects or indices are recycled!).
- **Verify / Trap:** Benchmark under **oversubscription** (more active threads than cores, preempted lock holder) as well as under-subscribed microbenchmarks—pure spinning looks fast at 2 threads on 16 cores and collapses catastrophically when a holder is preempted!

### `PAR-05` — Coarse Range Partitioning & Degree-of-Parallelism (DOP) Sweeps
- **Clue:** `Parallel.For(0, n, i => TinyWork(i))` or `Task.Run` per item spending more time in delegate/task scheduling and work-stealing than actual compute, or `Environment.ProcessorCount` saturating memory bandwidth and hurting tail latency.
- **C# Experiment:**
  - Enforce a minimum sequential grain threshold (run sequentially when $N < N_{\text{min}}$).
  - Partition into coarse contiguous chunks (`Partitioner.Create(0, n, rangeSize)` or `Parallel.For` over chunk indices) with worker-local accumulators.
  - **Sweep `MaxDegreeOfParallelism`** across `1`, `2`, `4`, **physical core count**, and **SMT logical processor count**—bandwidth-bound and cache-heavy kernels frequently peak at or below physical core count and regress under SMT or nested parallelism!
- **Verify / Trap:** Measure total host CPU-seconds and memory bandwidth alongside wall-clock speedup; avoid nested `Parallel.For` inside concurrent request handlers unless total concurrency is explicitly bounded.

---

## 2. Async Hot Paths, ThreadPool Health & Backpressure

### `PAR-06` — Eliminate Sync-Over-Async & ThreadPool Starvation
- **Clue:** Rising p95/p99 request latency and climbing ThreadPool thread count while total CPU utilization remains well below 100%.
- **C# Experiment:** Audit hot async call chains for `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, synchronous `Stream.Read`/`Write`, or `Thread.Sleep` on ThreadPool threads; make the path genuinely `async` end-to-end.
- **Verify / Trap:** Wrapping synchronous blocking I/O in `Task.Run(() => BlockingCall())` still consumes a ThreadPool worker for the entire block duration—it only shifts starvation! Diagnose waits before blindly raising `ThreadPool.SetMinThreads`.

### `PAR-07` — `ValueTask<T>`, `IValueTaskSource<T>` & `ConfigureAwait(false)`
- **Clue:** `Task<T>` allocations dominating hot async methods that complete synchronously $>80\text{--}95\%$ of the time (buffered socket/stream reads, L1 cache hits, pooled channels).
- **C# Experiment:**
  - Return `ValueTask<T>` (wrapping `T` synchronously with **zero allocation** when ready, or backed by a reusable `ManualResetValueTaskSourceCore<T>` / `PoolingAsyncValueTaskMethodBuilder` when measured).
  - Use `ConfigureAwait(false)` in reusable library code so continuations do not marshal back to a caller's custom `SynchronizationContext` / `TaskScheduler`.
- **Verify / Trap:**
  - **Strict `ValueTask` Contract:** Never `await` a `ValueTask` more than once, never access `.Result` before completion is verified, never read concurrently, and avoid calling `.AsTask()` on the hot path (which allocates a `Task`). `ValueTask<T>` is a wider struct than `Task<T>` reference—keep `Task<T>` when operations almost always suspend asynchronously!
  - Note: Default ASP.NET Core has no `SynchronizationContext`, and `ConfigureAwait(false)` does **not** suppress `ExecutionContext` / `AsyncLocal<T>` flow.

### `PAR-08` — Bounded `Channel<T>` Backpressure vs Unbounded Queues / `SemaphoreSlim`
- **Clue:** Burst traffic spawning millions of pending `Task`s + closure/buffer allocations queued behind `SemaphoreSlim.WaitAsync`, causing memory spikes and Gen2 GC stalls.
- **C# Experiment:** Replace unbounded `ConcurrentQueue` or bare `SemaphoreSlim` fan-in with `Channel.CreateBounded<T>(new BoundedChannelOptions(capacity) { SingleReader = ..., SingleWriter = ..., FullMode = BoundedChannelFullMode.Wait })` to bound in-flight memory and apply upstream async backpressure (`await writer.WriteAsync(...)`).
- **Verify / Trap:** Only set `SingleReader = true` / `SingleWriter = true` when strictly proven (violating it corrupts internal lock-free cursors!). Remember item capacity != byte capacity if items hold variable-sized buffers, and watch `AllowSynchronousContinuations` stack-diving/lock hazards.

---

## 3. Build-Time Source Generators & Service Patterns

### `PAR-09` — `System.Text.Json` Source Generation, `PipeReader` & Cached Options
- **Clue:** Reflection metadata warm-up, per-call `new JsonSerializerOptions(...)` (which **destroys internal metadata caching** and triggers massive CPU/alloc regressions!), or intermediate `MemoryStream` / `string` buffering before JSON parse/write.
- **C# Experiment:**
  - Hoist `JsonSerializerOptions` to a single `static readonly` instance!
  - Define a `[JsonSerializable(typeof(MyDto))] partial class AppJsonContext : JsonSerializerContext` for reflection-free metadata + fast-path `Utf8JsonWriter` serialization (note: `JsonSerializationMode.Serialization` fast-path accelerates **serialization**; deserialization uses generated metadata resolvers rather than fast-path codegen).
  - Deserialize/serialize directly over `PipeReader` / `PipeWriter` / `Stream` / `Utf8JsonReader` (`ReadOnlySpan<byte>`) without intermediate `string` materialization (leveraging `.NET 10` direct `PipeReader` JSON APIs where available).
- **Verify / Trap:** Copying `new JsonSerializerOptions(existing)` does **not** copy the populated reflection/converter cache—reuse the frozen instance or `JsonTypeInfo<T>` directly!

### `PAR-10` — `[GeneratedRegex]`, `[LibraryImport]` & Guarded `[LoggerMessage]`
- **Clue:** Runtime `Regex` compilation / catastrophic backtracking, legacy `[DllImport]` runtime marshalling stubs blocking inlining/AOT, or disabled `logger.LogDebug($"... {x}")` boxing structs and formatting strings on every call.
- **C# Experiment:**
  - **Regex:** Use `[GeneratedRegex("...", RegexOptions.CultureInvariant)]` for zero-startup-compile source-generated engines + `Regex.EnumerateMatches(ReadOnlySpan<char>)` (`ValueMatch` span enumeration with **zero `Match` heap allocations**!). Consider `RegexOptions.NonBacktracking` when linear $O(n)$ worst-case time on untrusted inputs is needed.
  - **P/Invoke:** Use `[LibraryImport]` + `[SuppressGCTransition]` (only for tiny, guaranteed non-blocking, non-managed-callback native leaf functions $< 50\text{ ns}$!).
  - **Logging:** Use `[LoggerMessage]` source generator **and** guard expensive argument computations with `if (logger.IsEnabled(LogLevel.Debug))` (C# still evaluates method arguments *before* calling a `[LoggerMessage]` method unless guarded!).
- **Verify / Trap:** `[GeneratedRegex]` eliminates startup IL generation, **not** exponential backtracking complexity if the pattern itself backtracks catastrophically!

### `PAR-11` — I/O, `HttpClient` Pooling, `HybridCache` & Database Query Hygiene
- **Clue:** Socket exhaustion / TLS handshake cost from `new HttpClient()`, cache stampedes under `ConcurrentDictionary.GetOrAdd(k, ExpensiveFactory)` (whose factory runs **outside** locks and executes concurrently on misses!), or EF Core N+1 / tracking / unindexed queries.
- **C# Experiment:**
  - Reuse a long-lived `SocketsHttpHandler` (`PooledConnectionLifetime` configured for DNS rotation) or `IHttpClientFactory`.
  - Use `HybridCache` (or single-flight keyed async locks) to coalesce concurrent cache misses onto one backend fetch.
  - For read-only EF Core queries, project only needed columns (`Select(x => new Dto(...))`) + `AsNoTracking()`, inspect generated SQL/execution plans (including `.NET 10` parameterized scalar-padding `Contains` SQL behavior), and consider `DbContext` pooling (`AddDbContextPool`).
- **Verify / Trap:** Managed CPU stack samples do **not** measure SQL Server/Postgres query cost—inspect actual SQL plans and round-trip counts, and **never** execute mutating `ExecuteUpdate`/`ExecuteDelete` benchmarks against unverified databases!

---

## 4. GC, Publishing Modes & `.NET 10` / `.NET 11` Runtime Regimes

### `PAR-12` — Server GC DATAS, Heap Hard Limits & Publishing Trade-Offs (JIT vs R2R vs Native AOT)
- **Clue:** Container memory RSS exceeding limits under Server GC, cold-start JIT spikes vs steady-state throughput losses, or comparing publishing models.
- **C# Experiment:**
  - **DATAS (Dynamic Adaptation To Application Sizes):** Enabled by default in Server GC starting in `.NET 9+`—dynamically scales heap count and Gen0 budgets between burst throughput and compact idle working set (often cutting idle/moderate-load memory by 50–80% at a ~0–3% peak-throughput trade-off). Benchmark under sustained + burst load before overriding `System.GC.DynamicAdaptationMode`.
  - **GC Hard Limits (`GCHeapHardLimit`):** Bounds managed GC heap only—leave explicit headroom for native buffers, thread stacks, and JIT code heaps! (Note: JSON `runtimeconfig` numeric limits are **decimal**, whereas `DOTNET_GCHeapHardLimit` env vars are **hexadecimal**!)
  - **Tiered PGO JIT vs ReadyToRun (R2R) vs Native AOT:**
    - *R2R* improves cold startup (at ~2–3$\times$ larger binary size) while still allowing Tier 1 + Dynamic PGO to re-JIT hot methods in steady state.
    - *Native AOT* delivers minimal startup time, binary footprint, and no runtime JIT overhead, **but lacks runtime Dynamic PGO** (interface/virtual-heavy steady-state workloads can run slower under Native AOT than Tiered PGO CoreCLR unless devirtualized statically via `JIT-04`/`JIT-05`!).
- **Verify / Trap:** Separate cold-start benchmarks from warmed Tier 1 steady-state benchmarks; never rank Native AOT > R2R > JIT without measuring the specific workload regime.

### `PAR-13` — `.NET 11` Runtime-Async & Hardware Baseline Experiments
- **Clue:** Deep `async`/`await` call chains on `.NET 11+` targets paying compiler state-machine boxing/hoisting overhead, or evaluating `.NET 11` x86-64-v3 R2R behavior.
- **C# Experiment:** On `.NET 11` targets, test opting the application assembly into runtime-assisted async (`<Features>$(Features);runtime-async=on</Features>`) in an isolated A/B build rig across both synchronous-completion fast paths and true asynchronous suspension/exception/cancellation depths.
- **Verify / Trap:** Much of the `.NET 11` shared framework already compiles with runtime-async regardless of app opt-in; benchmark cold startup, steady-state throughput, allocation bytes/op, and exception unwind paths explicitly rather than assuming a universal win.
