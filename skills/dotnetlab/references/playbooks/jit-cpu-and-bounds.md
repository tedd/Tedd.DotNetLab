# Playbook: JIT, CPU Pipelines, Bounds & Branches (`JIT`)

Targets CoreCLR RyuJIT Tier 1 code generation: eliminating array/span range checks and index sign-extension, keeping hot methods inlineable, leveraging Dynamic PGO and startup-frozen constants, breaking loop-carried dependency chains, and resolving branch mispredictions.

## 1. Bounds-Check Elimination & x64 Addressing

### `JIT-01` — Local Array Capture, Unsigned Bounds Idiom & Highest-Index Pre-Check
- **Clue:** Tier 1 disassembly (`lab.inspect` `native`) shows `cmp reg, [rax+8]` + `jae CORINFO_HELP_RNGCHKFAIL` inside a hot loop, or repeated `mov` reloads of an instance field `_items` because the JIT cannot prove `_items` was not mutated/aliased across calls or writes.
- **C# Experiment:**
  1. **Copy field reference to a local before the loop** (`T[] arr = _items;` — copies the 8-byte reference, not the elements) and bound the loop by `arr.Length` (never `_count` without slicing first!).
  2. **Slice once up front:** `Span<T> span = _items.AsSpan(start, count);` validates `start` and `count` once and gives the loop a fixed `.Length`.
  3. **Unsigned single-branch guard:** `(uint)index < (uint)span.Length` checks both `index >= 0` and `index < Length` in a single unsigned `cmp` + `jae`.
  4. **Highest-index pre-validation for fixed/strided accesses:** Validate the highest index up front so RyuJIT range analysis can prove lower non-negative offsets (`[0]`, `[1]`, `[2]`, `[3]`) are in-bounds:
     ```csharp
     var arr = _buffer; // Local capture prevents field reload per iteration
     if ((uint)(offset + 3) >= (uint)arr.Length || offset < 0)
         ThrowOutOfRange();
     // RyuJIT sees offset >= 0 and offset + 3 < arr.Length -> 4 loads, 0 inner range checks
     return arr[offset] + arr[offset + 1] + arr[offset + 2] + arr[offset + 3];
     ```
- **Verify / Trap:** Prove `offset + 3` cannot integer-overflow (e.g., `(uint)offset > (uint)(arr.Length - 4)` when `arr.Length >= 4`). Always inspect Tier 1 assembly—modern RyuJIT may already eliminate the check in simple `for (int i = 0; i < arr.Length; i++)` loops, making manual workarounds redundant. Preserve observable exception type/order when required.

### `JIT-02` — GC-Tracked Byref Iteration & `nuint` Indexing (Eliminating `movsxd`)
- **Clue:** Complex multi-array or strided indexing where RyuJIT still emits `CORINFO_HELP_RNGCHKFAIL` or 32-to-64-bit sign-extension instructions (`movsxd rdx, edx`) on every array access on x64.
- **C# Experiment:**
  - **Eliminate `movsxd` safely:** Index with `nuint` (or `(nint)(uint)i` when `i >= 0` is guaranteed) so the index is zero-extended natively in 64-bit registers without `movsxd`.
  - **Managed byref arithmetic (`MemoryMarshal` + `Unsafe.Add`):** Obtain a GC-tracked `ref T` (unlike `fixed` pointers, GC updates `ref T` automatically during compaction without pinning!):
    ```csharp
    static int DotProductUnchecked(ReadOnlySpan<int> a, ReadOnlySpan<int> b)
    {
        if (a.Length != b.Length)
            ThrowLengthMismatch();

        ref int ra = ref MemoryMarshal.GetReference(a);
        ref int rb = ref MemoryMarshal.GetReference(b);
        int sum = 0;
        nuint len = (nuint)a.Length;
        for (nuint i = 0; i < len; i++)
            sum += Unsafe.Add(ref ra, i) * Unsafe.Add(ref rb, i);
        return sum;
    }
    ```
- **Verify / Trap:** Only adopt `Unsafe.Add` when safe `Span<T>` slicing leaves a **verified assembly + timing gap**! Never dereference `GetArrayDataReference` / `GetReference` on an empty span (`len == 0`), keep intermediate `ref` offsets inside valid object bounds (`Unsafe.Add` counts *elements*, `Unsafe.AddByteOffset` counts *bytes*), and never assume `Unsafe.AsPointer` pins movable GC memory (`GC.KeepAlive` prevents collection, **not** compaction/relocation!).

### `JIT-03` — Extract Cold Throw Blocks (`[DoesNotReturn]` Throw Helpers)
- **Clue:** Small hot properties, Span indexers, or parsers fail to inline into callers because `throw new ArgumentOutOfRangeException($"...")` bloats IL size, allocates string formatting temporaries, and forces stack-frame prolog/epilog generation.
- **C# Experiment:** Move `throw` statements into dedicated static cold helpers marked `[DoesNotReturn]` and `[MethodImpl(MethodImplOptions.NoInlining)]`:
  ```csharp
  [DoesNotReturn]
  [MethodImpl(MethodImplOptions.NoInlining)]
  private static void ThrowOutOfRange() =>
      throw new ArgumentOutOfRangeException();
  ```
- **Verify / Trap:** `[DoesNotReturn]` tells RyuJIT that control never merges back after the call, moving the call block to the cold tail of the method (`int3` after `call`) and dramatically shrinking the hot path's inlining budget. Confirm with the `jit-and-contention-diagnostic` profile: `lab.query` `jit_inlining` should stop listing the helper's caller with a `FailReason` such as `too many il bytes` / `unprofitable inline`.

---

## 2. Inlining, Specialization, Structs & Frozen Configuration

### `JIT-04` — Devirtualization, `sealed` Types & Tiered Dynamic PGO
- **Clue:** Interface/virtual dispatch (`call qword ptr [rax+...]`) inside hot loops blocking inlining, constant propagation, escape analysis, and vectorization.
- **C# Experiment:**
  - Seal concrete leaf classes (`public sealed class ...`) and type local/field variables as concrete types when polymorphism is unnecessary.
  - Ensure **Tiered Compilation + Dynamic PGO** (`TieredPGO=1`, default since `.NET 8`) has warmed the path on representative inputs so RyuJIT emits guarded devirtualization (`cmp [rcx], MethodTable; jne slowPath`) and inlines the dominant type!
  - In `.NET 10+`, RyuJIT performs expanded stack allocation (escape analysis) for small fixed arrays and delegates when callees devirtualize and inline—re-check assembly before adding manual unsafe code.
- **Verify / Trap:** **Avoid blanket `[MethodImpl(MethodImplOptions.AggressiveOptimization)]`!** Skipping Tier 0 bypasses Tier 0 instrumentation and **disables Dynamic PGO** for that method, often regressing steady-state throughput by 10–30%. Test `[MethodImpl(MethodImplOptions.AggressiveInlining)]` selectively only on proven tiny leaf helpers, watching for I-cache bloat.

### `JIT-05` — Zero-Cost Generic `struct` Policy Specialization
- **Clue:** Passing `Func<T, TResult>` delegates, closures (`<>c__DisplayClass`), or `interface` instances into a tight inner loop (e.g., custom pixel blitter, comparer, filter, or math operator) causing millions of indirect calls per second.
- **C# Experiment:** Parameterize the kernel over an `interface`-constrained **value type** (`where TOp : struct, IOperation`). For `struct` type arguments, CoreCLR generates **dedicated unshared native code per `TOp`**, completely devirtualizing and inlining `TOp` methods with zero heap allocation and zero indirect branch:
  ```csharp
  internal interface ITransform { int Apply(int x); }
  internal readonly struct ScaleAdd(int scale, int bias) : ITransform
  {
      [MethodImpl(MethodImplOptions.AggressiveInlining)]
      public int Apply(int x) => x * scale + bias;
  }

  static void Map<TOp>(Span<int> data, TOp op) where TOp : struct, ITransform
  {
      for (int i = 0; i < data.Length; i++)
          data[i] = op.Apply(data[i]); // 100% direct + inlined in Tier 1; SIMD-eligible
  }
  ```
- **Verify / Trap:** Each distinct `struct` `TOp` compiles its own native method body—keep the number of specializations bounded to protect JIT time and I-cache footprint.

### `JIT-06` — `readonly struct`, `readonly` Members & `in` / `ref readonly` Copies
- **Clue:** Hidden defensive copies (`vmovdqu`/`movups` stack copies before every method/property call) when reading `readonly` fields, `in` parameters, or `ref readonly` locals of non-`readonly` structs, or copying wide structs ($> 16\text{--}24$ bytes) by value on every call.
- **Why GC counters miss it:** Defensive copies live on the stack, so `allocated_bytes_per_operation` stays at `0 B` while CPU cost rises. Only **caller** Tier 1 assembly reveals them—use `lab.run` `capture_jit_disasm` (it sees inlined callers and generic instantiations).
- **C# Experiment:**
  - Mark immutable value types `readonly struct` (or mark pure getters/methods `readonly`) to eliminate hidden defensive copies immediately.
  - For wide structs ($> 16\text{--}32$ bytes), compare `in T` / `ref readonly T` parameter/return passing against by-value passing.
- **ABI facts that decide the winner:**
  - **Windows x64:** structs of exactly 1/2/4/8 bytes travel in one register; **everything else** (including a 12- or 16-byte struct) is copied by the caller to the stack and passed by hidden pointer—`in` often wins earlier on Windows.
  - **Linux/macOS System V x64:** structs $\le 16$ bytes travel in up to 2 registers—by-value usually wins up to 16 bytes.
  - **Arm64:** $\le 16$ bytes (and HFA/HVA up to 4 floats/vectors) travel in registers.
- **Verify / Trap:**
  - Passing tiny structs by `in` adds an indirection and forces the caller's local to have an address (`lvAddrExposed`): the JIT must home it to the stack instead of keeping its fields in registers (no struct promotion), and a narrow-store → wide-load reload can hit a store-forwarding stall. Small structs are usually **faster by value**.
  - Measure on the target OS—the ABI differs, so a Windows win is not a Linux win.

### `JIT-06b` — `readonly ref struct` Cursors, `scoped` & `allows ref struct`
- **Clue:** A hot parser/tokenizer/reader keeps re-slicing a `ReadOnlySpan<T>` (`span = span[1..]` writes both pointer and length every step), or wraps a span in a `class` / ordinary struct (heap or boxing), or generic helpers reject `Span<T>` arguments.
- **C# Experiment:**
  - Encapsulate cursor state in a `ref struct` (stack-only, can hold `Span<T>` / `ref` fields, never boxed, never captured). Make it `readonly ref struct` when all state is fixed after construction so members need no defensive copies.
  - **Two-`ref` cursor (C# 11+ `ref` fields):** hold `ref byte _cur` + `ref byte _end` and advance with `Unsafe.Add` / test with `Unsafe.IsAddressLessThan(ref _cur, ref _end)`—one register update per step instead of re-writing span pointer + length. Only keep it when `capture_jit_disasm` shows fewer instructions **and** timing improves.
  - **`scoped`:** mark span parameters/locals `scoped` when they never escape, so callers can pass `stackalloc` slices without the compiler rejecting it (prefer this over `unsafe` workarounds).
  - **C# 13 / .NET 9+ `allows ref struct`:** `static int Count<T>(T reader) where T : IReader, allows ref struct` gets one specialized, devirtualized body per struct type argument (same mechanism as `JIT-05`)—zero boxing for `ref struct` readers.
- **Verify / Trap:** `ref struct` cannot cross `await`/`yield` or be stored in fields of normal types. Re-slicing safe spans is often already optimal—raw two-`ref` cursors drop bounds checks, so prove `_cur` never passes `_end` (and never read from an empty span's reference) with differential tests.

### `JIT-07` — Cached Typed Delegates (`CreateDelegate`) & Function Pointers
- **Clue:** `MethodInfo.Invoke` (array allocation + boxing + reflection checks), `Activator.CreateInstance`, or dynamic expression compilation on hot paths.
- **C# Experiment:** Resolve reflection **once** and cache a strongly typed `Func<...>` / `Action<...>` via `methodInfo.CreateDelegate<Func<TArg, TResult>>()` or compiled `Expression<TDelegate>` (or static `delegate*<TArg, TResult>` where unmanaged/static dispatch fits).
- **Verify / Trap:** Indirect calls through delegates/function pointers still inhibit cross-call inlining unless guarded/specialized; cache delegates per type/key without capturing per-call closures.

### `JIT-08` — Startup-Frozen `static readonly` Flags for Dead-Code Elimination
- **Clue:** Process-wide configuration switches (`AppContext.TryGetSwitch`, telemetry/diagnostics flags, environment modes) checked on every hot iteration, or allocating diagnostic arguments even when disabled.
- **C# Experiment:** Capture primitive (`bool`/`int`) startup flags inside a **non-generic** `static` class with an **explicit static constructor** (suppressing `beforefieldinit`), force initialization via `RuntimeHelpers.RunClassConstructor` *before* any hot caller is JIT-compiled, and place all feature-only argument preparation inside `if (StartupFlags.DiagEnabled)`:
  ```csharp
  internal static class StartupFlags
  {
      internal static readonly bool TelemetryEnabled;
      static StartupFlags() =>
          TelemetryEnabled = AppContext.TryGetSwitch("MyApp.Telemetry", out bool on) && on;
  }
  // During startup, AFTER configuring switches and BEFORE invoking hot methods:
  RuntimeHelpers.RunClassConstructor(typeof(StartupFlags).TypeHandle);
  ```
- **Verify / Trap:** CoreCLR Tier 1 folds initialized `static readonly` primitive fields into JIT compile-time constants and **deletes the disabled `if` branch completely**! Keep the holder non-generic, note that a `static readonly MyOptions` reference does *not* freeze mutable properties on `MyOptions`, never use for runtime-mutable toggles, and note Native AOT cannot specialize startup values post-compile. Verify both `true` and `false` in separate processes via `lab.inspect` `native`.

---

## 3. CPU Dependency Chains, Loop Structure & Branch Prediction

### `JIT-09` — Break Loop-Carried Recurrences with Multiple Independent Accumulators
- **Clue:** Scalar or SIMD reduction (`sum += a[i]`, `hash = hash * P + b[i]`, min/max, dot product) running at 1 element per 3–5 cycles despite multiple idle CPU execution ports.
- **Mechanism:** An instruction with latency $L$ and reciprocal throughput $R$ fed by $K$ independent chains takes at least $\max(R, L / K)$ cycles per op (e.g., Skylake/Zen `VMULSD`/`VADDSD` with $L = 4, R = 0.5$ needs $K = \lceil 4 / 0.5 \rceil = 8$ independent chains to saturate throughput; integer `ADD` with $L = 1, R = 0.25$ benefits from $K = 4$ chains).
- **C# Experiment:** Unroll by 4 and accumulate into 4 independent locals (`s0`, `s1`, `s2`, `s3`), combining `(s0 + s1) + (s2 + s3)` once after the loop:
  ```csharp
  long s0 = 0, s1 = 0, s2 = 0, s3 = 0;
  int i = 0, limit = span.Length - 3;
  for (; i < limit; i += 4)
  {
      s0 += span[i];
      s1 += span[i + 1];
      s2 += span[i + 2];
      s3 += span[i + 3];
  }
  long total = (s0 + s1) + (s2 + s3);
  for (; i < span.Length; i++) total += span[i];
  ```
- **Verify / Trap:** **Floating-point reassociation (`float`/`double`) changes rounding order, cancellation, and NaN propagation!** Only reassociate FP reductions when the application's numerical tolerance permits it (note `TensorPrimitives.Sum` / SIMD reductions already use multi-lane parallel accumulation). Too many accumulators ($> 8\text{--}12$ on x64) spill registers to the stack—inspect Tier 1 assembly for `[rsp+...]` spills.

### `JIT-10` — Software Pipelining & Controlled Loop Unrolling vs µop-Cache
- **Clue:** Dependent load -> compute -> store chain stalling on L1/L2 load-to-use latency (4–14 cycles), or loop counter/branch overhead dominating tiny 2–3 instruction loop bodies.
- **C# Experiment:** Load the next iteration's input early or interleave 2–4 independent items per loop step; test modest unroll factors ($2\times$ or $4\times$).
- **Verify / Trap:** Over-unrolling ($8\times$–$16\times$) overflows the CPU Decoded I-Cache (µop cache / loop stream detector) and harms short inputs with long scalar tails! Measure both tiny and large $N$.

### `JIT-11` — Loop Unswitching (Hoisting Loop-Invariant Mode Branches)
- **Clue:** `if (options.IgnoreCase)` or `switch (mode)` tested on every iteration when `mode` cannot change during the loop.
- **C# Experiment:** Hoist the invariant branch outside the loop and dispatch to 2–3 specialized tight loops (or use `JIT-05` generic `struct` policy specialization so you write the loop once in C# while the JIT emits separate branch-free bodies!).
- **Verify / Trap:** Prove no callback or aliased write inside the loop can mutate `mode`; check code-size growth.

### `JIT-12` — Branch Predictability, Common-Case Fall-Through & Opcode Dispatch
- **Clue:** Heavy branch mispredictions (~15–20 cycle pipeline flush per miss) on random/unpredictable predicates, or bytecode/tag/parser dispatch loops.
- **C# Experiment:**
  - Remember: **50% taken != 50% mispredicted!** (`T,F,T,F,...` is ~0% miss on modern TAGE predictors; random 50% coin-flip is ~50% miss). Test both skewed/clustered and random-50% distributions.
  - Shorten the data dependency chain feeding the branch condition so mispredictions resolve earlier.
  - For skewed tagged data (e.g., 90% ASCII fast-path, 10% escape/UTF-8), put the fast path first so common fall-through executes straight-line code; compare ordered `if`/`else` vs `switch` jump tables (`JMP [table + idx*8]`, which have indirect branch target prediction cost and lose per-case inlining if factored out).
- **Verify / Trap:** Source block order does not guarantee machine-code block order—Dynamic PGO reorders basic blocks based on observed profile counts, so always verify Tier 1 layout under realistic warm-up!

### `JIT-13` — Branchless Conditional Select (`CMOV` / `CSEL`) & Bitmask Arithmetic
- **Clue:** Truly unpredictable (~30–70% random) predicate selecting between two cheap, already-available scalar values (`min`/`max`, conditional increment, clamp, lookup fallback).
- **C# Experiment:** Compare `if` against ternary `?:`, `Math.Min`/`Max`/`Clamp`, conditional integer add, or explicit bitmasks:
  ```csharp
  // Branchless conditional count (avoids Jcc misprediction on random data)
  count += value > threshold ? 1 : 0; // Inspect Tier 1 for SETG / ADD vs JLE

  // Explicit bitmask select when ?: still lowers to a branch:
  int mask = -(condition ? 1 : 0); // 0xFFFFFFFF if true, 0 if false
  int selected = b ^ ((a ^ b) & mask);
  ```
- **Verify / Trap:**
  - **Rule of thumb:** Branchless wins only when $\text{extra branchless work} < P(\text{miss}) \times \text{miss penalty}$. On predictable (>95% or <5%) data, a predicted branch is **faster** than `CMOV`/`CSEL` because `CMOV` converts a control dependency into a **strict data dependency** on *both* inputs!
  - **Safety:** Never convert short-circuiting guards (`x != 0 ? a / x : 0` or `idx < arr.Length ? arr[idx] : 0`) into eager evaluation of both sides! Note also `Math.Abs(int.MinValue)` overflows, and FP `x * 0.0` turns `NaN`/`Infinity` into `NaN`.

### `JIT-14` — Scalar Strength Reduction & Fused Multiply-Add (`FMA`)
- **Clue:** Integer `DIV`/`IDIV` (10–40+ cycles latency) by runtime-invariant divisors inside loops, power-of-two `%` on signed integers, or separate `a * b + c` FP chains.
- **C# Experiment:**
  - Compile-time constant divisors are already strength-reduced by RyuJIT (`IMUL` + shift). When a divisor is **runtime-variable but loop-invariant** (e.g., grid width or hash modulus), replace `% width` in inner loops with incremental wrap (`if (++x == width) x = 0;`), power-of-two unsigned mask (`(uint)idx & mask`), or precomputed fast-mod multiplier (Lemire fastmod).
  - Use `Math.FusedMultiplyAdd(a, b, c)` / `MathF.FusedMultiplyAdd` (`VFMADD*` / `FMADD`) when single-rounding fused multiply-add is numerically permitted.
- **Verify / Trap:** Signed `x % 8` is negative for `x < 0`, whereas `x & 7` is non-negative—only mask unsigned or proven non-negative values! `FusedMultiplyAdd` computes $\text{rn}(a \cdot b + c)$ with a single rounding rather than two, which can alter exact bit-for-bit FP outputs.


---

## 4. Generic Equality & Exception-Free Hot Paths

### `JIT-15` — Unboxed Generic Value-Type Equality & Hashing
- **Clue:** Generic containers/helpers calling `a.Equals(b)` / `object.Equals(a, b)` / `a.GetHashCode()` on an unconstrained `T`, a custom `IEqualityComparer<T>` field invoked per element, or `struct` keys without `IEquatable<T>` (`ValueType.Equals` falls back to reflection-style field comparison and boxes). Allocation counters show per-lookup bytes, or `jit_inlining` shows `Equals` not inlined.
- **C# Experiment:**
  - Implement `IEquatable<T>` (+ a matching `GetHashCode`) on every struct key; prefer `readonly record struct` or a hand-written `Equals(T other)`.
  - In generic code, call `EqualityComparer<T>.Default.Equals(a, b)` directly—RyuJIT recognizes it and devirtualizes/inlines it for value-type `T`. Only store a comparer field when callers really pass custom comparers; branch once `if (typeof(T).IsValueType && comparer is null)` to a comparer-free loop (the `Dictionary<TKey,TValue>` pattern).
  - Constrain `where T : IEquatable<T>` when you own the API so `a.Equals(b)` binds to the non-boxing overload.
- **Verify / Trap:** `record struct` equality compares every field—hand-write `Equals` when only a key subset defines identity. Keep `Equals`/`GetHashCode` consistent and check `allocated_bytes_per_operation` drops to `0`.

### `JIT-16` — Exception-Free Hot Paths (`Try*`, `OperationStatus`)
- **Clue:** `try { int.Parse(...) } catch (FormatException)` inside per-record loops, `KeyNotFoundException` / `InvalidCastException` used as control flow, or traces showing `FirstChanceException` storms. A thrown .NET exception costs roughly 10–50 µs (stack trace capture + unwinding)—thousands of times more than a branch.
- **C# Experiment:** Use `int.TryParse(ReadOnlySpan<char>, ...)`, `Utf8Parser.TryParse`, `IUtf8SpanParsable<T>.TryParse`, `Dictionary.TryGetValue`, `is` / `as` patterns, and APIs returning `System.Buffers.OperationStatus` (`Done`, `NeedMoreData`, `DestinationTooSmall`, `InvalidData`) so malformed or partial input stays on a cheap branch.
- **Verify / Trap:** Keep exceptions for truly exceptional, caller-visible failures and preserve the public contract (exception type/message where callers depend on it). Benchmark the realistic invalid-input ratio—`Try*` versions of the valid path are usually equal, so the win scales with how often input is bad.
