# Optimization in modern .NET: .NET 10 and later

**Research baseline: October 14, 2026.** .NET 10 is the established LTS release, supported through November 14, 2028. .NET 11 RC2 was released on October 13, 2026; its changes should still be treated as prerelease behavior rather than final release guarantees. This review covers modern .NET, CoreCLR, C#, ASP.NET Core, and EF Core—not .NET Framework 1–4. ([Microsoft](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core))

**My recommended optimization order is: eliminate unnecessary work, reduce data movement and retention, control concurrency, select deployment settings, and only then optimize individual instructions.**

The important distinction is between **improvements the runtime already supplies** and **changes that require application redesign**. Modern .NET can eliminate some allocations and dispatch overhead automatically; it cannot compensate for an unnecessary database query, an unbounded work queue, or repeatedly converting the same data between representations. The runtime capabilities discussed below are documented; application-level benefits remain hypotheses until measured. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime))

All numerical benchmark results below are published examples, not measurements I performed on your application.

## 1. Establish what “optimized” means

Do not collapse performance into one benchmark number. I recommend defining separate acceptance criteria:

| Objective | Measurements to prioritize | What the experiment must distinguish |
|---|---|---|
| Throughput and compute cost | Completed operations per CPU-second; CPU time per operation | Less work versus simply using more cores |
| Latency | Median, p95/p99, timeouts, queueing time | Faster execution versus waiting elsewhere |
| Memory efficiency | Allocated bytes per operation, live managed heap, process working set | Allocation churn versus retained memory |
| Startup and elasticity | Process startup, first successful request, time to steady state | Cold behavior versus warmed-up behavior |

A useful prioritization calculation: if a method accounts for 5% of an operation’s execution time, making that method twice as fast reduces total execution time by only 2.5%, assuming the remaining work is unchanged. That is sufficient reason to profile before rewriting.

### Start with process-level evidence

The first tools should normally be `dotnet-counters` and `dotnet-trace`:

```bash
dotnet-counters monitor --process-id <PID> --counters System.Runtime

dotnet-trace collect --process-id <PID>
```

Use counters to identify broad symptoms, then traces to investigate their causes. CPU sampling, GC activity, allocation behavior, and blocking are different kinds of evidence; a CPU-heavy method is not necessarily responsible for most elapsed request time. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters))

For supported Linux environments, the newer `dotnet-trace collect-linux` path can capture native/kernel context and CPU or thread-time profiles. It is currently a preview capability with kernel, architecture, and permission requirements—not a universally available replacement for ordinary EventPipe tracing. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-trace))

### Use microbenchmarks to test mechanisms, not whole-system outcomes

BenchmarkDotNet should run in Release mode without an attached debugger. Consume benchmark results so the compiler cannot remove the work, and test the actual runtime and hardware combinations you intend to deploy. Add `MemoryDiagnoser` for allocations and `DisassemblyDiagnoser` when the hypothesis concerns generated code. ([benchmarkdotnet.org](https://benchmarkdotnet.org/articles/guides/good-practices.html))

My recommended test matrix includes small and large inputs, successful and unsuccessful lookups, ordinary and adversarial distributions, cold processes, and warmed steady state. For services, follow the microbenchmark with load tests at multiple offered request rates, recording failures and queueing rather than reporting successful-request throughput alone.

**A microbenchmark win should explain a production improvement—not substitute for demonstrating one.**

## 2. Exploit the JIT before trying to outsmart it

### Tiered compilation and dynamic PGO are already part of the baseline

Dynamic profile-guided optimization has been enabled by default since .NET 8. It uses instrumentation gathered during early execution to optimize subsequently compiled code. Consequently, advice to “enable PGO for a free speedup” is often merely restating the modern default. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-8/runtime))

Keep tiered compilation and PGO enabled initially. Benchmark representative execution paths after sufficient warm-up, but measure startup separately: a long-running service and a command-line utility can favor different compilation trade-offs. The runtime exposes configuration switches for controlled experiments; these should not become a collection of unexplained defaults. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation))

### What .NET 10 changes

.NET 10 expands inlining, devirtualization, escape analysis, and struct promotion. In suitable code, it can eliminate allocations associated with small fixed-size arrays, delegates, and some enumeration patterns. It can also expose struct fields directly to further optimization instead of treating the entire value as an opaque aggregate. These transformations depend on the code shape and what the compiler can prove; they are not semantic guarantees. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/runtime))

Stephen Toub’s .NET 10 RC1 performance report illustrates this with specific microbenchmarks:

| Published example | .NET 9 | .NET 10 RC1 |
|---|---:|---:|
| Small reference-array example | 11.580 ns; 48 B allocated | 3.960 ns; 0 B |
| Captured-lambda example | 19.530 ns; 88 B allocated | 6.685 ns; 24 B |

In the second example, eliminating the delegate allocation did **not** eliminate the closure allocation. These results demonstrate particular compiler capabilities, not a general threefold application speedup. ([Microsoft for Developers](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/))

The practical consequence is to re-benchmark historical hot-path workarounds after a runtime upgrade. An abstraction that previously incurred an allocation or interface call might now disappear after inlining.

### Two attributes deserve particular skepticism

`AggressiveInlining` is a request, not a guarantee; unnecessary use can produce worse code. More surprisingly, `AggressiveOptimization` bypasses the first tier of compilation and therefore loses optimizations that depend on tiering, including dynamic PGO. Microsoft explicitly describes it as rarely appropriate. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.compilerservices.methodimploptions?view=net-10.0))

Prefer intelligible, optimizable code until disassembly proves that a specific intervention is beneficial. Microsoft’s unsafe-code guidance includes .NET 10 examples where ordinary safe writes and `CopyTo` generate the same optimized machine code as hand-written unsafe alternatives. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/unsafe-code/best-practices))

## 3. Optimize data movement, allocation, and lifetime separately

### Use spans for synchronous processing; memory for asynchronous ownership

`Span<T>` and `ReadOnlySpan<T>` let synchronous code operate on existing contiguous data without constructing replacement arrays or strings. `Memory<T>` and `ReadOnlyMemory<T>` are suitable when a buffer must remain available across asynchronous operations. Neither abstraction transfers ownership automatically. The owner must keep the underlying storage valid for the entire consumer operation. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/memory-t-usage-guidelines))

This is particularly valuable in parsers, serializers, protocol handling, and numerical kernels. Examine the complete path: removing one substring allocation is less useful when the next layer immediately calls `ToString()`.

Also distinguish allocation avoidance from retention. A small memory slice referencing a large array still depends on that array. When the slice must survive much longer than its source operation, copying a small amount of data can be the more memory-efficient design. That follows from the ownership relationship, not from the size of the slice alone. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/memory-and-spans/memory-t-usage-guidelines))

### Stay in the representation you actually need

For byte-oriented protocols, prefer a byte-oriented pipeline instead of repeatedly decoding UTF-8 into UTF-16 strings and encoding it again. .NET 10 adds relevant APIs, including UTF-8 hex conversion overloads and span-based normalization support. The useful optimization is removing conversions and intermediate storage, not merely selecting a faster conversion routine. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries))

### Treat `stackalloc` as a bounded scratch-buffer technique

Use `stackalloc` for small, predictably bounded temporary storage. Do not derive arbitrarily large stack allocations from external input, and avoid allocating repeatedly inside loops. Stack-allocated memory is not automatically initialized; code must initialize any portion it reads. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/operators/stackalloc))

There is no universal optimal threshold. Choose one through testing under the actual call depth and workload, with a non-stack fallback for larger inputs.

### Pool buffers only when ownership is explicit

`ArrayPool<T>.Rent(n)` can return an array larger than `n`, containing previous contents. Operate on the intended slice, not the full rental. Return it to the same pool exactly once, normally through `try/finally`, and never access it after return. An asynchronous consumer must finish before the rental is returned. Clear contents when required by the data’s sensitivity and reuse policy. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.arraypool-1.rent?view=net-10.0))

Pooling deliberately retains reusable storage. Therefore, its success criterion should include both reduced allocation pressure and acceptable retained memory—not just lower bytes allocated per operation.

### Structs are not an automatic memory optimization

Replacing classes with structs changes value semantics and copying behavior. Large structs can introduce expensive copies; inappropriate use of `in`, `ref`, or mutable members can undermine the intended benefit. Use compact value types where their semantics fit, and examine readonly/ref-based access when copies are visible in the profile. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/csharp/advanced-topics/performance/))

## 4. Select collections according to their access pattern

### Alternate lookup avoids allocating temporary string keys

Modern dictionaries support alternate key representations when the comparer supports them. A string-keyed dictionary can therefore be queried using `ReadOnlySpan<char>` instead of creating a temporary string. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2.getalternatelookup?view=net-10.0))

For example, this .NET 10-compatible lookup avoids `token.ToString()`:

```csharp
using System;
using System.Collections.Generic;

public static class MethodCodes
{
    // Never mutated after initialization.
    private static readonly Dictionary<string, int> Codes =
        new(StringComparer.Ordinal)
        {
            ["GET"] = 1,
            ["POST"] = 2,
            ["DELETE"] = 3
        };

    public static bool TryGet(ReadOnlySpan<char> token, out int code)
    {
        var lookup = Codes.GetAlternateLookup<ReadOnlySpan<char>>();
        return lookup.TryGetValue(token, out code);
    }
}
```

The comparer matters: alternate lookup is not supported for every arbitrary key/comparer combination. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.collections.generic.dictionary-2.getalternatelookup?view=net-10.0))

### Frozen collections trade construction cost for repeated reads

`FrozenDictionary` is appropriate for data constructed infrequently and queried extensively. It spends additional effort during construction to optimize subsequent access. It is not a default replacement for a dictionary rebuilt per request, and Microsoft cautions that construction characteristics depend on the keys supplied. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.collections.frozen.frozendictionary-2?view=net-10.0))

A useful break-even model is:

$$
N_{\text{lookups}}>
\frac{C_{\text{frozen construction}}-C_{\text{dictionary construction}}}
{C_{\text{dictionary lookup}}-C_{\text{frozen lookup}}}
$$

This only predicts a benefit when the denominator is positive for your data. Measure construction, lookup distribution, and memory together.

### Cache search preprocessing

`SearchValues<T>` supports optimized repeated searches against a reusable search set. Create the search representation once for stable delimiters or patterns, rather than rebuilding it inside the scan. This is relevant to tokenizers and parsers that repeatedly search for the same characters or strings. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.buffers.searchvalues?view=net-10.0))

### Use `CollectionsMarshal` only inside tightly controlled hot paths

`CollectionsMarshal.GetValueRefOrAddDefault` exposes a reference to a dictionary value, allowing in-place updates without a second lookup or copying a large value:

```csharp
ref int count = ref CollectionsMarshal.GetValueRefOrAddDefault(
    counts, key, out _);
count++;
```

Do not add or remove dictionary entries while that reference is in use. The API supplies no additional concurrency protection. Similarly, `CollectionsMarshal.AsSpan(list)` exposes list storage under the constraint that elements are not added or removed while the span is being used. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.collectionsmarshal.getvaluereforadddefault?view=net-10.0))

### Do not impose a blanket LINQ ban

.NET 10 contains further LINQ specialization and vectorization improvements. Costs depend on the operator chain and concrete source type, so “LINQ is always slow” is not a useful optimization rule. Profile the actual query, then compare a direct loop only when the sequence processing is materially expensive. ([Microsoft for Developers](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-10/))

## 5. Optimize concurrency before optimizing individual awaits

### Remove blocking from asynchronous paths

ThreadPool starvation can present as rising thread counts and request latency while CPU utilization remains below saturation. Blocking on asynchronous work is a common cause. Investigate synchronous waits and blocking calls rather than immediately increasing minimum thread counts. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/debug-threadpool-starvation))

For server-side I/O, the objective is to stop occupying a worker while waiting—not to move the same blocking operation into another queued task.

### Bound both active work and pending work

A semaphore can limit how many operations execute simultaneously while still allowing the application to create an enormous number of waiting tasks. My recommendation is to control admission as well as execution.

A bounded `Channel<T>` supports this design. With `BoundedChannelFullMode.Wait`, awaited writes propagate backpressure. Set `SingleReader` and `SingleWriter` only when those guarantees are true. Treat `AllowSynchronousContinuations` carefully: a producer may execute consumer continuation work, altering latency and execution behavior. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/extensions/channels))

A queue bound measured in items is not automatically a byte bound. One thousand tiny messages and one thousand large buffers have radically different memory requirements. Size the queue and concurrency limits from the resources they retain and the downstream capacity they consume.

### `ValueTask<T>` is a specialized contract, not a faster default

`ValueTask<T>` is useful when avoiding task allocation matters—for example, on a hot path with frequent synchronous completion or a suitable pooled implementation. Its costs include a larger value to copy and more restrictive consumption semantics. Converting it through `AsTask()` can reintroduce allocation. Microsoft recommends `Task`/`Task<T>` as the default unless measurements justify otherwise. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1?view=net-10.0))

Consume a general `ValueTask<T>` once: await it once, or convert it once and use the resulting task. Do not assume it supports the same reuse patterns as a `Task<T>`. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.valuetask-1?view=net-10.0))

### `ConfigureAwait(false)` is not a scheduling command

It does not force execution onto a background thread and does not suppress `ExecutionContext` or `AsyncLocal` flow. ASP.NET Core does not install a custom synchronization context by default, although custom contexts and task schedulers can still matter. Use it according to library semantics, not as an indiscriminate performance switch. ([Microsoft for Developers](https://devblogs.microsoft.com/dotnet/configureawait-faq/))

### Reduce sharing before substituting synchronization primitives

For ordinary synchronous mutual exclusion, modern C# supports a dedicated `System.Threading.Lock`. The compiler recognizes its static type and uses the specialized locking path. Keep critical sections short and do not cast it to `object` before locking. It does not permit awaiting inside the protected block. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/csharp/language-reference/statements/lock))

For CPU-parallel loops, prefer partition-local accumulation followed by a merge over repeatedly updating shared state. Parallel loops have partitioning and synchronization costs, and nested parallelism can oversubscribe the available processors. More tasks are not evidence of more useful parallelism. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/parallel-programming/potential-pitfalls-in-data-and-task-parallelism))

## 6. Move predictable work to build time

### JSON: distinguish generated metadata from generated serialization

`System.Text.Json` source generation has two materially different benefits. Metadata generation replaces runtime discovery and supports both serialization and deserialization. Fast-path generation emits specialized serialization logic for supported cases; there is no equivalent generated fast-path deserializer. Options, converters, and execution paths affect eligibility, so enabling source generation does not guarantee that every operation uses the fastest path. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/source-generation-modes))

Reuse `JsonSerializerOptions` rather than constructing it per operation. Its metadata caches are reusable and thread-safe; copying an options instance does not copy its populated metadata cache. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/configure-options))

.NET 10 adds direct `PipeReader` deserialization support. When input already arrives through pipelines, evaluate that path instead of wrapping it as a stream. This removes an adapter, but it does not imply zero-copy object construction or allocation-free deserialization. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-10/libraries))

### Logging: avoid preparation work as well as logging work

Source-generated `[LoggerMessage]` methods avoid recurring template parsing and reduce boxing and temporary allocations. However, expensive arguments are evaluated before the generated method executes. Guard expensive payload construction with an appropriate enabled-level check instead of assuming the generated method can undo that work. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/extensions/logging/high-performance-logging))

### Regex: select the algorithm before its compilation mode

For stable patterns, `[GeneratedRegex]` moves generation to build time and works well with AOT. Separately evaluate `RegexOptions.NonBacktracking` where the required pattern features permit it, and use appropriate timeouts for untrusted input. Compilation alone does not repair pathological matching behavior. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/base-types/regular-expression-source-generators))

### Native interop: remove avoidable marshalling setup

For suitable P/Invoke declarations, `[LibraryImport]` generates marshalling code at build time instead of relying on runtime-generated stubs, and can enable inlining. This is relevant to interop-heavy applications and AOT compatibility. The marshalling contract still needs to match the native ABI exactly. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/native-interop/pinvoke-source-generation))

## 7. For services, database and network work deserve early attention

### EF Core: inspect SQL and query plans first

Prioritize indexes, projected columns, bounded result sets, and the number of round trips. Avoid accidental N+1 queries. Large joins can duplicate substantial data; split queries can mitigate that but introduce additional round trips. No-tracking queries avoid tracking overhead, but identity resolution and repeated entities make the trade-off more nuanced than “always disable tracking.” ([learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/performance/efficient-querying))

**A .NET 10-specific migration issue:** EF Core 10 changes the default translation of parameterized collection membership queries such as `ids.Contains(entity.Id)`. The documented strategy uses individual scalar parameters with padding, balancing plan-cache reuse against cardinality information. Microsoft explicitly notes that no strategy wins for every query. Recheck generated SQL and plans across realistic collection sizes after upgrading. ([learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/what-is-new/ef-core-10.0/whatsnew))

### Keep three different optimizations separate

`DbContext` pooling reuses context instances; connection pooling reuses database connections. They solve different problems, and pooled context state requires care—particularly tenant-dependent state. Compiled queries reduce repeated query-processing overhead; compiled models primarily target model initialization. Neither repairs an inefficient database execution plan. ([learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics))

For set-based changes, evaluate `ExecuteUpdateAsync` and `ExecuteDeleteAsync` instead of loading entities only to modify or delete them. This can eliminate materialization and change-tracking work as well as unnecessary client/server transfers. ([learn.microsoft.com](https://learn.microsoft.com/en-us/ef/core/performance/efficient-updating))

### Reuse HTTP connection pools correctly

Use long-lived `HttpClient` instances with appropriately managed handler lifetimes, or use `IHttpClientFactory`. Repeatedly constructing and disposing independent handlers discards their connection pools. Connection lifetime configuration also matters for responding to DNS changes; “keep everything forever” is not a complete policy. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/http/httpclient-guidelines))

### Prevent duplicated cache-miss work

`HybridCache` supplies cache-aside behavior, optional secondary storage, and stampede protection. It is worth evaluating when concurrent misses repeatedly trigger expensive work. Cache freshness, bounded storage, invalidation, and cross-node consistency remain application design requirements. ([Microsoft for Developers](https://devblogs.microsoft.com/dotnet/hybrid-cache-is-now-ga/))

Do not equate `ConcurrentDictionary.GetOrAdd` with “run this expensive factory exactly once.” Its factory executes outside the dictionary’s locks and can run multiple times under contention, even though only one value is retained. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.collections.concurrent.concurrentdictionary-2.getoradd?view=net-10.0))

## 8. Treat GC configuration as a workload trade-off

### DATAS changes the old Server GC assumptions

Dynamic Adaptation to Application Sizes—DATAS—is enabled by default for Server GC from .NET 9 onward. It adjusts heap count and allocation budgets according to the workload rather than simply operating as a fixed heap-per-core arrangement. It can trade some throughput for substantially lower memory consumption. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas))

Microsoft’s documented introduction benchmark, on a 48-core Linux system, reported approximately 2–3% lower maximum throughput with more than 80% lower working set. This is an illustration of the trade-off, not a predicted .NET 10 result for arbitrary services. More Gen0/Gen1 collections did not, by itself, make the configuration inferior. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/garbage-collection/datas))

My recommended comparison is the default configuration against a small number of justified alternatives, under sustained load, sudden bursts, and the actual CPU/memory limits. Measure tail latency and working set together.

### Heap limits are not process-memory limits

A GC heap hard limit constrains GC-related memory, not the complete process working set. Native allocations, stacks, executable code, and other process resources still require headroom. Avoid copying a heap-limit percentage into every container without validating the application’s non-GC memory consumption. Also note the configuration trap: numeric GC values use decimal in JSON configuration but hexadecimal in environment variables. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/garbage-collector))

Treat manual collections, heap-count overrides, and specialized latency modes as responses to a demonstrated mechanism—not as a standard “performance configuration” bundle.

## 9. Choose JIT, ReadyToRun, and Native AOT by deployment economics

| Deployment approach | Principal reason to evaluate it | Principal trade-off |
|---|---|---|
| JIT with tiered compilation and PGO | Workload-adaptive optimization in long-running applications | Runtime compilation and warm-up |
| ReadyToRun | Reduce startup and first-use compilation | Larger assemblies; hot methods can still be JIT-compiled |
| Native AOT | Startup, deployment density, and avoiding runtime JIT | Build-time specialization and compatibility restrictions |

These are different deployment strategies, not an ascending ranking of execution speed. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/runtime-config/compilation))

ReadyToRun retains both IL and native code. Microsoft documents assembly-size increases commonly around two to three times; tiered compilation can replace frequently executed R2R methods with JIT-generated code. Measure deployment size, disk loading, first request, and steady state together. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/deploying/ready-to-run))

Native AOT removes runtime JIT compilation and can improve startup and memory footprint. It also restricts dynamic loading and runtime code generation, requires trimming, and may increase generated code size through generic specialization. It does **not** mean that all reflection is impossible, nor that steady-state throughput must exceed a PGO-optimized JIT build. Resolve AOT/trimming warnings and test the published binary. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/deploying/native-aot/))

## 10. For numerical kernels, optimize the memory path before instruction width

For computation over contiguous data, evaluate established bulk APIs and `TensorPrimitives` before writing hardware intrinsics. `TensorPrimitives` operates directly over spans and includes arithmetic, reductions, transformations, and conversions. .NET 10 stabilizes the broader tensor APIs, which remain supplied through the `System.Numerics.Tensors` package. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/api/system.numerics.tensors.tensorprimitives))

My recommended progression is:

**Algorithm and layout → fewer passes and temporaries → library primitives → portable vectors → architecture-specific intrinsics.**

For example, when an expression is implemented as separate operations that materialize intermediate arrays, compare it with a single-pass implementation before increasing vector width. The latter cannot eliminate the former’s additional data transfers.

Benchmark small inputs as well as large arrays, and test the deployment CPUs rather than assuming the development machine’s instruction set. Validate floating-point tolerances and reproducibility alongside speed. Unsafe access should require demonstrated code-generation benefits, especially because newer JITs increasingly optimize ordinary safe loops and memory operations. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/standard/unsafe-code/best-practices))

## 11. What .NET 11 changes—and what it does not yet justify

### Runtime-native async is the major experimental change

.NET 11 introduces runtime-native async, moving async transformation work from conventional compiler-generated state machines toward runtime-managed suspension and resumption. Application code opts in; much of the shared framework is already built with the new mechanism. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime))

The documented project configuration is:

```xml
<PropertyGroup>
  <TargetFramework>net11.0</TargetFramework>
  <Features>$(Features);runtime-async=on</Features>
</PropertyGroup>
```

The important qualification is that the stated .NET 11 async-performance goal is **parity with .NET 10**, with known cases still producing less efficient code. The mechanism enables further optimization and can reduce generated binary size, but it is not evidence that every async operation becomes allocation-free or faster. ([Microsoft for Developers](https://devblogs.microsoft.com/dotnet/performance-improvements-in-net-11/))

For an evaluation, compare synchronous completion, actual suspension, deep await chains, cancellation, exception-heavy paths, startup, and diagnostic behavior. Preserve a non-opted-in baseline so runtime-version effects and async-lowering effects are not conflated.

### Hardware baselines can affect startup comparisons

The documented .NET 11 x86/x64 baseline moves to x86-64-v2, while the Windows/Linux ReadyToRun target moves to x86-64-v3. These are different requirements: it would be incorrect to summarize this as “all .NET 11 applications require AVX2.” Supported processors below the R2R target can incur additional startup compilation. .NET 11 also adds hardware-accelerated `Half` operations on supported instruction sets. ([learn.microsoft.com](https://learn.microsoft.com/en-us/dotnet/core/whats-new/dotnet-11/runtime))

## Recommended implementation sequence

**First, establish causality.** Record the deployed runtime and publishing mode, capture traces under representative load, and identify whether the limiting resource is CPU execution, waiting, allocation, retention, or downstream capacity.

**Second, remove costs with low semantic risk.** Start with unnecessary database work, repeated conversions, duplicated cache misses, excessive buffering, unbounded concurrency, and repeatedly constructed metadata. Benchmark modern standard-library alternatives before retaining custom low-level code.

**Third, specialize proven hot paths.** Evaluate spans, alternate lookups, frozen collections, pooling, source generation, SIMD, GC alternatives, and publishing modes as isolated experiments. Require both a mechanism-level explanation and an end-to-end improvement.

**The strongest modern .NET optimization is not the most complicated implementation. It is the least complicated implementation that demonstrably meets the workload’s latency, throughput, memory, and deployment constraints.**
