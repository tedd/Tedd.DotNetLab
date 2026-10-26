# Playbook: Algorithms, Lookups & Data Structures (`ALG`)

Highest-leverage optimization family: eliminating unnecessary passes, lowering asymptotic complexity, and choosing data structures that match key density, read/write ratio, and CPU cache hierarchy. Always test algorithmic wins before micro-tuning instructions.

## 1. Algorithmic Complexity & Pass Elimination

### `ALG-01` — Replace $O(n^2)$ Rescans with Hash, Direct Index, or Sort-and-Sweep
- **Clue:** Nested `for`/`foreach`, `List.Contains` / `FirstOrDefault` inside a loop, or quadratic scaling as input count grows.
- **C# Experiment:**
  - Bounded dense integer keys -> direct `T[]` or `ulong[]` bitset.
  - Arbitrary keys -> `HashSet<T>` / `Dictionary<TKey, TValue>` (pre-sized via `EnsureCapacity(n)`), or sort once (`Array.Sort` / `Span.Sort`) + single linear merge/sweep.
- **Verify / Trap:** Charge index construction + allocation against total lookups. For tiny $n$ ($\le 12$), a contiguous `ReadOnlySpan<T>` linear scan (`IndexOf`) often beats hash computation and bucket chasing due to SIMD vectorization and L1 locality. Preserve tie-breaking, duplicate handling, and comparer semantics.

### `ALG-02` — Top-$k$ Selection & Partial Sort Instead of Full Sort
- **Clue:** `OrderBy(...).Take(k)` or `Array.Sort` followed by reading only a small prefix ($k \ll n$).
- **C# Experiment:** Maintain a bounded `PriorityQueue<TElement, TPriority>` of capacity $k$ ($O(n \log k)$), or run Quickselect / min-max scan ($O(n)$ for $k=1..4$).
- **Verify / Trap:** Verify tie-ordering stability if callers depend on deterministic ordering among equal keys; test adversarial sorted/reverse-sorted distributions.

### `ALG-03` — Adaptive / Natural-Run Handling for Nearly Sorted Data
- **Clue:** Repeatedly sorting data that is already mostly ordered or receives small incremental appends.
- **C# Experiment:** Check `isSorted` in one pass (`a[i - 1] <= a[i]`) before sorting, binary-search + `Span.CopyTo` shift for tiny incremental batches, or merge sorted runs.
- **Verify / Trap:** Measure mutation frequency; if random churn dominates, extra pre-checks add branch cost without skipping `Sort`.

### `ALG-04` — Sparse Active Sets & Inverted Indexes Over All-Pairs / Full-Population Scans
- **Clue:** Iterating millions of mostly inactive slots, empty buckets, or non-interacting spatial pairs.
- **C# Experiment:** Maintain a dense active-ID list (swap-remove with sparse back-index), spatial grid/BVH, or iterate set bits of an occupancy `ulong[]` bitmap rather than scanning every slot.
- **Verify / Trap:** Benchmark across low, medium, and high occupancy densities—at $>50\%$ density, branchless dense sequential scans can outperform indirect sparse gathers.

### `ALG-05` — Early Exit & Predicate Ordering by Selectivity / Cost
- **Clue:** Evaluating expensive functions, regexes, or memory loads before cheap rejecting predicates, or scanning the full input after satisfiability is known.
- **C# Experiment:** Order conjunctions (`&&`) from **cheapest + highest rejection rate** to most expensive (e.g., length check -> integer tag/first-byte check -> hash -> full span comparison). Break immediately on `Any` / first-match queries.
- **Verify / Trap:** Ensure reordered predicates have no observable side effects or exception-order changes; test no-match (worst-case full scan) alongside early-match inputs.

### `ALG-06` — Hoist Invariants, Memoize Parse/Setup & Maintain Incremental Deltas
- **Clue:** Repeated parsing, normalization, `Regex` / expression compilation, `Encoding` lookup, or recomputing a global aggregate after single-item mutations.
- **C# Experiment:**
  - Parse into a canonical binary/enum representation once at ingress and pass typed structs/IDs downstream.
  - Maintain incremental running sums/counts/dirty flags (`ALG` delta update) rather than rescanning on read.
- **Verify / Trap:** Define strict cache invalidation, staleness bounds, concurrency safety, and memory retention caps; verify numerical drift if incremental floating-point subtraction is used.

### `ALG-07` — Producer-Consumer Stream Fusion
- **Clue:** Multi-stage pipelines materializing intermediate `T[]` / `List<T>` / `string` per stage (`Select(...).Where(...).ToArray()`).
- **C# Experiment:** Fuse adjacent transforms and filters into a single pass writing directly to the destination `Span<T>` or `IBufferWriter<byte>` while each element is still hot in registers/L1.
- **Verify / Trap:** Excessive fusion of unrelated heavy expressions can spike register pressure and inhibit SIMD auto-vectorization (test loop fission if spills appear in Tier 1 assembly). Preserve partial-write rollback on exceptions.

---

## 2. High-Speed Lookups, Hashing & Search Primitives

### `ALG-08` — Static PE `.rdata` Span Lookup Tables (Zero Heap Allocation)
- **Clue:** `static readonly byte[]` / `sbyte[]` / `bool[]` lookup tables for character classification, hex/Base64 decoding, state machines, or small bounded domains, or long `if`/`else` chains over `byte` inputs.
- **C# Experiment:** Expose constant byte tables as `ReadOnlySpan<byte>` backed directly by the assembly's PE `.rdata` section—zero heap object, zero static constructor, compile-time constant length (`256` eliminates bounds checks when indexed by `byte`):
  ```csharp
  private static ReadOnlySpan<byte> HexLut =>
  [
      // 256-byte table embedded directly in PE metadata; no byte[] allocation
  ];

  byte mapped = HexLut[inputByte]; // byte is 0..255 == length 256 -> zero bounds check!
  ```
- **Verify / Trap:** Works for `byte`/`sbyte`/`bool` constant initializers; on little-endian targets (`BitConverter.IsLittleEndian`), fixed `ushort`/`uint` tables can also be cast with `MemoryMarshal.Cast<byte, uint>(ByteLut)` if endianness is guarded. Watch L1D footprint if tables exceed a few kilobytes under cold cache.

### `ALG-09` — Single-Lookup Dictionary Access via `CollectionsMarshal`
- **Clue:** `ContainsKey` + indexer, `TryGetValue` + indexer overwrite (double hash + double bucket probe), or copying large value structs out and back in.
- **C# Experiment:** Perform one hash/probe and mutate in place via managed `ref`:
  ```csharp
  ref Counter slot = ref CollectionsMarshal.GetValueRefOrAddDefault(map, key, out bool exists);
  slot.Total += delta; // Mutates value in-place inside Dictionary entry array
  ```
  Use `CollectionsMarshal.GetValueRefOrNullRef(map, key)` + `!Unsafe.IsNullRef(ref slot)` for read/update-if-present without inserting.
- **Verify / Trap:** **Never** add, remove, or resize `map` while `ref slot` is live—resizing replaces the backing array and invalidates the `ref`! Provides zero concurrency protection.

### `ALG-10` — Zero-Allocation Span Keys via `GetAlternateLookup<ReadOnlySpan<char>>()`
- **Clue:** Allocating `span.ToString()` or `line.Substring(...)` solely to query a `Dictionary<string, TValue>` or `HashSet<string>`.
- **C# Experiment:** Initialize the dictionary/set with a span-compatible `StringComparer` (`StringComparer.Ordinal` or `OrdinalIgnoreCase`) and obtain the alternate lookup struct:
  ```csharp
  var lookup = dict.GetAlternateLookup<ReadOnlySpan<char>>();
  if (lookup.TryGetValue(tokenSpan, out int id)) { /* zero string allocation */ }
  ```
  (In `.NET 10+`, also check alternate lookup support on `ConcurrentDictionary` / `FrozenDictionary` where applicable.)
- **Verify / Trap:** Requires a supported `IAlternateEqualityComparer`; when inserting a missing key via `lookup[span] = val`, a `string` key must still be materialized once for storage.

### `ALG-11` — Read-Mostly Lookup Tables with `FrozenDictionary` / `FrozenSet`
- **Clue:** Long-lived `Dictionary<TKey, TValue>` or `HashSet<T>` built at startup or config reload and queried thousands/millions of times.
- **C# Experiment:** Build `source.ToFrozenDictionary(StringComparer.Ordinal)` / `ToFrozenSet()`. Runtime analyzes the exact keyset (length buckets, ASCII/ordinal single-char/substring discrimination, perfect/dense integer slots) to specialize lookup code.
- **Verify / Trap:** Construction cost $C_f$ is significantly higher than `Dictionary` ($C_d$). Require positive lookup win ($L_d - L_f > 0$) and break-even $N > (C_f - C_d)/(L_d - L_f)$. Never rebuild `FrozenDictionary` per request! Note: `ImmutableDictionary` is a tree for persistent structural sharing and is usually **much slower** for lookups—do not confuse the two.

### `ALG-12` — Vectorized Multi-Target & Substring Scanning with `SearchValues<T>`
- **Clue:** Manual character/byte loops checking delimiters (`c == ',' || c == ';' || ...`), `IndexOfAny` with $>5$ targets, or scanning text for any of several literal keywords.
- **C# Experiment:** Precompute and cache a `static readonly SearchValues<byte>`, `SearchValues<char>`, or (`.NET 9+`) `SearchValues<string>` (`StringComparison.Ordinal` / `OrdinalIgnoreCase`), then call `span.IndexOfAny(s_delimiters)` / `IndexOfAnyExcept` / `ContainsAny`.
  ```csharp
  private static readonly SearchValues<byte> s_jsonDelims = SearchValues.Create("\"\\\r\n\t"u8);
  int idx = utf8Span.IndexOfAny(s_jsonDelims);
  ```
- **Verify / Trap:** `SearchValues.Create` performs heavy preprocessing (SIMD nibble bitmaps, Teddy/Rabin-Karp tables for multi-string search)—**always** hoist to a `static readonly` or long-lived field, never construct per call!

---

## 3. Bitmaps, Signatures & Compressed Representations

### `ALG-13` — Packed `ulong[]` Bitmaps, Set-Bit Walking & Backbuffered Diffs
- **Clue:** `bool[]` occupancy flags (8$\times$ memory bloat), `HashSet<int>` over bounded IDs, ECS component signature matching, or scanning all items to find dirty entries.
- **C# Experiment:**
  - Pack 64 flags per `ulong`: `wordIdx = id >> 6`, `mask = 1UL << (id & 63)`.
  - **Signature matching:** `(present & required) == required && (present & excluded) == 0`.
  - **Fast set-bit iteration (skipping 64 zeros in one jump):**
    ```csharp
    ulong word = bitmap[w];
    int baseId = w << 6;
    while (word != 0)
    {
        int bit = BitOperations.TrailingZeroCount(word);
        Process(baseId + bit);
        word &= word - 1; // BLSR: clears lowest set bit in 1 cycle
    }
    ```
  - **Bulk state transitions:** Compute next state into an owned backbuffer and iterate `ulong changed = old[w] ^ next[w]` to process only mutated entities.
- **Verify / Trap:**
  - **C# Shift Trap:** `1UL << 64` evaluates to `1UL << 0 == 1UL` because C# masks `ulong` shift counts by `& 63`! Always guard tail masks explicitly: `usedBits == 64 ? ulong.MaxValue : (1UL << usedBits) - 1UL`, and clear/mask unused high bits in the final word before `~`, `==`, or `BitOperations.PopCount`.
  - Concurrent point writes to different bits in the **same** `ulong` word cause lost updates and cache-line true sharing unless synchronized (`Interlocked.Or`/`And`).

### `ALG-14` — Cached Query Plans, Archetype Matching & Run/Delta Compression
- **Clue:** Repeated query normalization, ECS archetype/column lookup inside entity loops, or memory-bandwidth-bound scans over long runs of identical metadata or sorted IDs.
- **C# Experiment:**
  - Cache normalized query plans, matching archetype lists, and component column offsets outside entity iteration; start joins from the rarest required component.
  - For sorted IDs or long state runs, compare interval lists `[start, length)`, Run-Length Encoding (RLE) operating directly on runs without decompressing to scratch, or delta + varint packing.
- **Verify / Trap:** Enumerate every invalidation trigger (schema change, chunk split, entity move, ID reuse/generation wrap). Test fragmented/alternating distributions where RLE expands memory and degrades random access.

### `ALG-15` — Length-First & Word-Prefix Fast Paths for Short Keys
- **Clue:** Hot lookups/comparisons of short keys (HTTP header names, JSON property names, keywords, ticker symbols, $\le 16$ bytes) spending time in full hashing or `SequenceEqual` setup even when most candidates differ.
- **C# Experiment:**
  - Reject on length first (`a.Length != b.Length`), then compare an 8-byte (or 4-byte) prefix as one integer before touching the rest:
    ```csharp
    static bool EqualsShort(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length) return false;
        if (a.Length >= 8)
        {
            ulong pa = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(a));
            ulong pb = Unsafe.ReadUnaligned<ulong>(ref MemoryMarshal.GetReference(b));
            if (pa != pb) return false;
        }
        return a.SequenceEqual(b);
    }
    ```
  - For a small fixed keyword set, `switch` on `(length, first ulong)` pairs computed once per input token.
- **Verify / Trap:** `SequenceEqual` is already vectorized—this only pays when most comparisons fail early. Never read 8 bytes from a shorter span. Case-insensitive ASCII prefixes need a `| 0x2020202020202020` mask that is only valid for letters—guard non-letter bytes.
