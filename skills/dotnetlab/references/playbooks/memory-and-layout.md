# Playbook: Memory, Allocations & Cache Layout (`MEM`)

Targets allocation churn, GC pause pressure, redundant buffer copies, pointer chasing, cache-line waste, multidimensional indexing overhead, and DRAM/TLB/store-forwarding bottlenecks.

## 1. Zero-Allocation Views, Scratch Buffers & Pooling

### `MEM-01` — `Span<T>` / `ReadOnlySpan<T>` & `Memory<T>` Slice Pipelines
- **Clue:** `Substring`, `Split`, `ToArray`, `Take(...).ToArray()`, or intermediate staging buffers in synchronous or async parsing/transform code.
- **C# Experiment:** Thread `ReadOnlySpan<T>` / `Span<T>` through synchronous call trees; use `Memory<T>` / `ReadOnlyMemory<T>` across `async` boundaries. For delimiter splitting without `string[]` allocation, use `MemoryExtensions.Split` (`ReadOnlySpan<Range>` or `.NET 9+` `SpanSplitEnumerator<T>`):
  ```csharp
  foreach (Range part in line.AsSpan().Split(','))
  {
      ReadOnlySpan<char> token = line.AsSpan(part);
      // process token with zero heap allocations
  }
  ```
- **Verify / Trap:** `Memory<T>` and `ArraySegment<T>` keep the **entire** underlying array alive in GC! If a tiny long-lived slice is retained from a multi-megabyte receive buffer, copy that small slice deliberately to avoid pinning/retaining the large buffer.

### `MEM-02` — Bounded `stackalloc` Outside Loops + `[SkipLocalsInit]`
- **Clue:** Small temporary buffers ($\le 512\text{--}1024$ bytes) allocated per call on the heap.
- **C# Experiment:** Allocate stack scratch **once outside loops** with a pooled fallback for larger sizes, and apply `[SkipLocalsInit]` on the hot method after proving every read byte is written first:
  ```csharp
  [SkipLocalsInit]
  static int FormatKey(ReadOnlySpan<byte> src)
  {
      byte[]? rented = null;
      Span<byte> buf = src.Length <= 256
          ? stackalloc byte[256]
          : (rented = ArrayPool<byte>.Shared.Rent(src.Length));
      try
      {
          Span<byte> work = buf[..src.Length];
          // write all of work before reading...
          return ComputeChecksum(work);
      }
      finally
      {
          if (rented is not null)
              ArrayPool<byte>.Shared.Return(rented);
      }
  }
  ```
- **Verify / Trap:** **Never** execute `stackalloc` inside a loop (`StackOverflowException` crashes the process and cannot be caught!). Without `[SkipLocalsInit]`, CoreCLR zero-initializes `stackalloc` frames (`rep stosb` / `vpxor`), which can erase savings on short operations—only add `[SkipLocalsInit]` when partial reads/exceptions cannot leak uninitialized stack bytes.

### `MEM-03` — `ArrayPool<T>.Shared`, Exclusive Scratch Reuse & `EnsureCapacity`
- **Clue:** High Gen0/Gen1 allocation rate, LOH ($\ge 85,000$ bytes) Gen2/background GC pressure, or `List<T>` / `Dictionary<K,V>` doubling copies.
- **C# Experiment:**
  - Pre-size collections (`list.EnsureCapacity(expected)`, `new Dictionary<K,V>(expected)`).
  - Reuse an instance-owned scratch array when single-caller lifetime is guaranteed, or rent from `ArrayPool<T>.Shared`.
- **Verify / Trap:** `Rent(minLength)` returns `array.Length >= minLength` containing **dirty data from prior callers**! Always slice `rented.AsSpan(0, minLength)`, return **at most once** in `finally` *after* all async consumers finish, pass `clearArray: true` when holding references or secrets, and monitor retained pool working set.

### `MEM-04` — `GC.AllocateUninitializedArray<T>` & Pinned Object Heap (POH)
- **Clue:** CPU time spent in OS/GC zero-filling (`ZeroMemory`) for large `new byte[N]` / unmanaged `T[]` buffers that are immediately overwritten, or heap fragmentation from long-lived `GCHandleType.Pinned` buffers.
- **C# Experiment:**
  - `T[] buf = GC.AllocateUninitializedArray<T>(length);` for primitive/unmanaged types immediately overwritten in full.
  - `byte[] ioBuf = GC.AllocateArray<byte>(length, pinned: true);` to place long-lived I/O or native-interop buffers on the **Pinned Object Heap (POH)** so movable Gen0–Gen2 heaps can compact freely without pin fragmentation.
- **Verify / Trap:** `AllocateUninitializedArray` is a permission, not a guarantee, and reference-containing types are still zeroed for GC safety; ensure exception/short-read paths never expose unwritten tail elements. Never allocate short-lived buffers on the POH (POH is collected only in full Gen2 GCs).

### `MEM-05` — Aligned Native Buffers via `NativeMemory.AlignedAlloc`
- **Clue:** Persistent large buffers outside GC scan/compaction overhead, or 32/64-byte alignment required for non-temporal SIMD stores (`Avx.StoreAlignedNonTemporal`) or cache-line isolation.
- **C# Experiment:** Pair `NativeMemory.AlignedAlloc(byteCount, alignment)` with `NativeMemory.AlignedFree(ptr)` (never mix `Alloc` with `AlignedFree`!), wrap long-lived ownership in `SafeHandle`/`IDisposable`, and call `GC.AddMemoryPressure` / `RemoveMemoryPressure` when small managed wrappers own large native regions.
- **Verify / Trap:** Check `(nuint)count * (nuint)sizeof(T)` with `checked` arithmetic before allocating; **never** store managed object references inside `NativeMemory`.

### `MEM-06` — Direct UTF-8 Byte Pipelines & `IBufferWriter<byte>`
- **Clue:** Decoding network/file UTF-8 bytes into UTF-16 `string`s, formatting/concatenating `string`s, and re-encoding back to UTF-8.
- **C# Experiment:** Keep data as UTF-8 (`ReadOnlySpan<byte>`, `"literal"u8`), parse/format numbers via `Utf8Parser` / `Utf8Formatter` or `ISpanFormattable` / `IUtf8SpanFormattable`, and write directly into destination `IBufferWriter<byte>` spans (`GetSpan` -> write -> `Advance`).
- **Verify / Trap:** Audit the **entire** pipeline—optimizing one UTF-8<->UTF-16 transcode is inferior to deleting both transcodes. Validate malformed UTF-8 sequence handling where untrusted input enters.

### `MEM-07` — Direct `List<T>` Backing Span via `CollectionsMarshal.AsSpan`
- **Clue:** `List<T>` iteration or bulk copying showing indexer/enumerator overhead or blocking `Span<T>` / SIMD APIs.
- **C# Experiment:** `Span<T> items = CollectionsMarshal.AsSpan(list);` to pass `List<T>` storage directly to `MemoryExtensions`, SIMD loops, or `ref` iteration.
- **Verify / Trap:** **Zero structural mutations** (`Add`, `Remove`, `Clear`, `Insert`, `EnsureCapacity`, `TrimExcess`) may occur on `list` while `items` is live (including via delegates or reentrant calls), or `items` will point to a stale array or out-of-count elements!

---

## 2. Data Layout, Locality & Access Patterns

### `MEM-08` — AoS -> SoA / AoSoA & Hot/Cold Field Splitting
- **Clue:** Streaming over `MyObject[]` or wide `MyStruct[]` (e.g., 128 bytes) when the hot loop touches only 1–2 fields (loading 64-byte cache lines for 4–8 useful bytes, plus pointer chasing for `class` elements).
- **C# Experiment:**
  - Replace `class[]` with contiguous value-type `struct[]` first to delete object headers (16 bytes on x64) and pointer indirections.
  - Split cold metadata (`Name`, `DebugInfo`, `RareConfig`) into a parallel side array indexed by the same ID (**Hot/Cold Split**).
  - For streaming/SIMD scans, transpose hot fields into **Structure of Arrays (SoA)** (`float[] X, Y, Z`) or **AoSoA** (`struct Block8 { fixed float X[8], Y[8], Z[8]; }` / `[InlineArray(8)]`) so single-record access and 8-lane SIMD stay co-located in one cache line!
- **Verify / Trap:** Pure SoA hurts random whole-entity lookups across 10 columns (10 separate cache lines / TLB entries)—use Hot/Cold split or AoSoA when mixed access occurs.

### `MEM-09` — 32-Bit Index + Generation Handles Instead of 64-Bit Object References
- **Clue:** Large graphs/trees/caches on x64 where 8-byte object references double cache/L3/DRAM footprint and GC mark times.
- **C# Experiment:** Store nodes in contiguous `TNode[]` pools and reference nodes via 32-bit `int`/`uint` indices (or packed `(uint Index, uint Generation)` handles when slots are recycled).
- **Verify / Trap:** Never truncate raw 64-bit native pointers! Validate index capacity, reserve a sentinel (`-1` or `uint.MaxValue`), and check generation equality when dereferencing recycled handles.

### `MEM-10` — Flatten `T[,]` / `T[,,]` to 1D `T[]` & Contiguous Inner-Loop Stride
- **Clue:** Hot loops indexing rectangular `T[y, x]` / `T[z, y, x]`, jagged `T[][]` pointer-chasing per row, or column-major traversal (`for x ... for y ... grid[y, x]`) striding across cache lines and pages.
- **C# Experiment:**
  - Interchange loops so the **innermost loop increments the contiguous unit-stride dimension** (`x` in `y * stride + x`).
  - Flatten `T[,]` into 1D `T[]` (`T[,]` has slower multi-axis lower-bound/range helpers in CoreCLR than 1D `SZARRAY`), hoist `int rowOffset = y * stride;` outside the `x` loop, and slice `Span<T> row = buffer.AsSpan(y * stride, width);`:
    ```csharp
    for (int y = 0; y < height; y++)
    {
        Span<float> row = grid.AsSpan(y * stride, width);
        for (int x = 0; x < row.Length; x++)
            row[x] *= scale; // Unit-stride, single hoisted bounds check, auto-vectorizable
    }
    ```
- **Verify / Trap:** Validate `width`, `height`, `stride >= width`, and `checked(height * stride) <= grid.Length` up front—a valid flat index `y * width + x` alone does not prevent an out-of-bounds `x >= width` wrapping into row `y + 1`! Choose jagged `T[][]` only when rows are genuinely ragged or independently resized.

### `MEM-11` — Cache Tiling / Blocking & Stencil Interior/Boundary Splitting
- **Clue:** Matrix, image convolution, blur, cellular automata, or 2D/3D stencil loops where working sets exceed L1D (32–48 KiB) or L2, causing repeated reloads of neighboring rows, plus per-cell `if (x == 0 || x == width - 1)` edge branches.
- **C# Experiment:**
  1. **Split boundaries from interior:** Process top/bottom/left/right border cells separately (clamp/wrap/mirror/halo) so the `[1 .. width - 2]` interior kernel has constant valid offsets and **zero edge branches**, enabling clean SIMD vectorization.
  2. **Rolling line buffers / cache tiles:** Sweep tile dimensions `(TileY, TileX)` so active input rows + halo + output tile stay resident in L1D/L2.
  3. **Separable kernels:** If a $K \times K$ 2D filter is mathematically separable, split into horizontal + vertical $1\times K$ passes ($2K$ ops vs $K^2$ ops per pixel).
- **Verify / Trap:** Check read-only input vs order-dependent in-place update semantics, intermediate FP rounding differences on separable passes, and differential-test every corner/edge cell against a naive scalar oracle.

---

## 3. Block Traffic, Packed Stores, Prefetch & Hardware Hazards

### `MEM-12` — Packed `uint` / `ulong` Loads & Stores (SWAR / Fixed Records)
- **Clue:** Emitting 4–8 separate `mov byte ptr [dst+k], reg8` stores (e.g., RGBA pixels, 4-byte tags, ASCII headers) or comparing fixed 4/8-byte tokens byte-by-byte.
- **C# Experiment:** Pack the bit pattern in a register and emit a single unaligned wide load/store (`mov dword/qword ptr [dst], reg`) via `BinaryPrimitives.WriteUInt32LittleEndian` / `Unsafe.ReadUnaligned<ulong>`:
  ```csharp
  uint rgba = (uint)r | ((uint)g << 8) | ((uint)b << 16) | ((uint)a << 24);
  BinaryPrimitives.WriteUInt32LittleEndian(dst.Slice(offset, 4), rgba);
  ```
- **Verify / Trap:** Guard endianness (`BinaryPrimitives` handles byte order cleanly; raw `MemoryMarshal.Cast<byte, uint>` exposes host endianness), verify destination slice length (`>= 4` / `>= 8`), and inspect Tier 1 assembly to confirm the JIT emitted a single `mov` rather than 4 byte extractions.

### `MEM-13` — Runtime Block Copy/Fill vs Non-Temporal Streaming Stores
- **Clue:** Scalar `for` copy/zero loops, or huge write-only output buffers ($>$ LLC size) polluting cache by reading destination lines (Read-For-Ownership) that will not be read again soon.
- **C# Experiment:**
  - Always test `Span<T>.CopyTo`, `Span<T>.Fill`, `Span<T>.Clear`, or `Buffer.MemoryCopy` first—CoreCLR already dispatches to vectorized/unrolled SIMD and `rep movsb` thresholds and preserves overlap (`memmove`) semantics.
  - Only for **large, aligned, write-only outputs** that are **not** immediately read by the consumer, test `Sse2.StoreNonTemporal` / `Avx.StoreAlignedNonTemporal` (requires pinned 16/32-byte aligned destination + `Sse2.MemoryFence()` `SFENCE` when ordering requires it).
- **Verify / Trap:** If the next stage reads the output buffer while it would otherwise fit in L2/L3, non-temporal stores **regress severely** by forcing DRAM round-trips! Measure the **entire producer + consumer** pipeline, never isolated copy bandwidth alone. (`Unsafe.CopyBlockUnaligned` does **not** guarantee overlap safety—prefer `Span.CopyTo`.)

### `MEM-14` — Software Prefetch Distance Sweeps (`Sse.Prefetch0`)
- **Clue:** Demonstrably DRAM/LLC latency-bound pointer chasing, hash-bucket probing, or random indexed table lookups where `indices[i + lookahead]` is computable ahead of time.
- **C# Experiment:** Pin the target table (`fixed (Entry* basePtr = table)`), guard `Sse.IsSupported`, and sweep lookahead distances $d \in \{0, 2, 4, 8, 16\}$ against a `d = 0` no-prefetch control:
  ```csharp
  if (Sse.IsSupported && i + Lookahead < indices.Length)
  {
      uint nextIdx = indices[i + Lookahead];
      if (nextIdx < (uint)table.Length)
          Sse.Prefetch0(basePtr + nextIdx);
  }
  ```
- **Verify / Trap:** Hardware prefetchers already excel at linear and constant-stride streams—adding `Prefetch0` to unit-stride loops usually **slows them down** via extra µops and address arithmetic! Keep prefetched addresses strictly within live pinned bounds; too small $d$ fails to hide latency, too large $d$ evicts L1 lines before consumption.

### `MEM-15` — Store-to-Load Forwarding Mismatches, 4 KiB Aliasing & NUMA First-Touch
- **Clue:** Store-forwarding stalls (writing bytes/shorts then immediately reading an overlapping `uint`/`ulong` or `Vector128` from the same stack/buffer slot), false 4 KiB address aliasing between source and destination streams, or multi-socket parallel loops stalling on remote NUMA memory initialized by a single startup thread.
- **C# Experiment:**
  - **Store forwarding:** Assemble wide values in registers using shifts/ORs instead of writing narrow fields to memory and reloading wide!
  - **4 KiB aliasing:** Test offsetting co-indexed source/destination buffer bases away from exact multiples of `4096` bytes when PMU evidence shows `ld_blocks` / 4K-alias stalls.
  - **NUMA first-touch:** Initialize partition slices in parallel on the same worker threads/affinities that will process them so OS physical pages fault onto local NUMA nodes.
- **Verify / Trap:** Microarchitecture- and OS-specific; require controlled offset experiments or hardware PMU confirmation before retaining layout padding.

---

## 4. Hidden Allocations & GC Mark Cost

### `MEM-16` — Value-Type Enumerators & `ref readonly` Span Iteration
- **Clue:** `foreach` over a collection typed as `IEnumerable<T>` / `IList<T>` / `ICollection<T>` (boxes the struct enumerator: one heap allocation per loop plus 2 interface calls per element), LINQ in hot loops, or `foreach (var item in list)` over wide structs (a full struct copy per element).
- **C# Experiment:**
  - Type locals/parameters as the concrete collection (`List<T>`, `T[]`, `Dictionary<K,V>`) or as `ReadOnlySpan<T>` so `foreach` binds to the struct enumerator / span loop.
  - Iterate wide structs by reference: `foreach (ref readonly var item in CollectionsMarshal.AsSpan(list))` (or `ref var` to mutate in place).
  - Custom collections: expose `public Enumerator GetEnumerator()` returning a `struct` enumerator (and keep the interface implementation explicit).
- **Verify / Trap:** `gc.gen0_collections` and `allocated_bytes_per_operation` should drop; `ref` iteration over `CollectionsMarshal.AsSpan` inherits all `MEM-07` mutation rules. For structs $\le 16$ bytes the copy is often free (registers)—verify the gain.

### `MEM-17` — `params ReadOnlySpan<T>` & `[InlineArray(N)]` Fixed Buffers
- **Clue:** Hot calls to `params T[]` methods (`string.Concat`, logging helpers, `Path.Combine`, your own `Sum(params int[])`) allocate a hidden array on every call; small fixed-size scratch kept as `T[]` fields or `new T[4]` locals.
- **C# Experiment:**
  - C# 13 / .NET 9+: add a `params ReadOnlySpan<T>` overload—the compiler places arguments in a stack buffer, so calls allocate nothing (the BCL already has these overloads for many APIs; recompiling may pick them up automatically).
  - Replace small fixed scratch arrays with an `[InlineArray(N)]` struct (stored inline in the containing struct/stack frame, indexable as `Span<T>`).
- **Verify / Trap:** Overload resolution prefers the span overload only when the target framework provides it—confirm in IL/allocations. Large `InlineArray` structs copied by value are expensive (see `JIT-06`); pass them by `ref`.

### `MEM-18` — Flatten Long-Lived Object Graphs to Cut GC Mark Time
- **Clue:** Millions of long-lived small objects (`Dictionary<int, Node>`, linked trees, caches of `class` records) where **Gen2 GC pauses** or `gc.heap_size_bytes` dominate even though steady-state allocation is low—the GC must trace every reference on each full/background collection.
- **C# Experiment:** Store the data in primitive/`struct` arrays without references (`struct Node { int Left, Right; float Value; }` in one `Node[]`, string data interned into one `byte[]`/`char[]` slab with `(offset, length)` pairs). An array of reference-free structs is a single object the GC never scans inside.
- **Verify / Trap:** Compare `gc.gen2_collections`, `gc.heap_size_bytes`, and pause metrics from `gc-diagnostic` before/after; lifetime management becomes manual (free lists, generations—see `MEM-09`). Any reference-type field inside the struct brings tracing back.
