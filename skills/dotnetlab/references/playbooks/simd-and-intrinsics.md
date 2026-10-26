# Playbook: C# SIMD & Hardware Intrinsics (`SIMD`)

Deep reference for vectorizing compute, scanning, parsing, filtering, bit manipulation, and numerical kernels in C# using built-in vectorized BCL primitives, portable `System.Runtime.Intrinsics` vectors (`Vector128<T>`, `Vector256<T>`, `Vector512<T>`), and architecture-specific hardware intrinsics (`System.Runtime.Intrinsics.X86` and `System.Runtime.Intrinsics.Arm`).

## 1. Vectorization Strategy, ISA Guards & Memory Access

### `SIMD-01` — Follow the .NET Vectorization Ladder First
- **Clue:** Scalar byte/char/numeric loops performing uniform element-wise math, search, validation, equality, min/max, or reductions.
- **C# Experiment:** Progress down the ladder only when higher levels cannot express the operation or leave a verified assembly gap:
  1. **Vectorized BCL APIs:** `MemoryExtensions` (`IndexOf`, `IndexOfAny`, `Contains`, `SequenceEqual`, `SequenceCompareTo`, `Count`, `Replace`, `CommonPrefixLength`), `SearchValues<T>`, `System.Text.Ascii` (`IsValid`, `EqualsIgnoreCase`, `ToUpperInPlace`, `Trim`, `FromUtf16`/`ToUtf16`), and `System.Numerics.Tensors.TensorPrimitives` (`Dot`, `Sum`, `CosineSimilarity`, `SoftMax`, `Add`, `Multiply`, `Min`/`Max`, `Exp`/`Log`).
  2. **Cross-Platform Fixed-Width Vectors:** `Vector128<T>`, `Vector256<T>`, `Vector512<T>` static methods (`LoadUnsafe`, `Equals`, `LessThan`, `ExtractMostSignificantBits`, `ConditionalSelect`, `Shuffle`, `Widen`, `Narrow`, `Sum`), which lower directly to SSE/AVX2/AVX-512 on x64 and NEON on Arm64! (Prefer `Vector128`/`Vector256` over legacy variable-width `System.Numerics.Vector<T>` when shuffle/mask geometry depends on lane count.)
  3. **Explicit ISA Intrinsics (`X86` / `Arm`):** `Sse2`, `Ssse3`, `Sse41`, `Sse42`, `Avx`, `Avx2`, `Fma`, `Bmi1`, `Bmi2`, `Pclmulqdq`, `Avx512F`, `Avx512BW`, `Avx512Vbmi`, `AvxVnni`, `AdvSimd`, `AdvSimd.Arm64`, `Sve`—for instructions portable APIs cannot express (`VPSHUFB` nibble LUTs, `VPMADDUBSW`, `VPCOMPRESSD`, `VPTEST`, `PEXT`/`PDEP`, `CRC32`, `PCLMULQDQ`).
- **Verify / Trap:** Inspect Tier 1 assembly via `lab.inspect` `native` before writing custom intrinsics—BCL methods already contain hand-tuned AVX2/AVX-512/NEON paths with short-input fast paths!

### `SIMD-02` — Exact Hardware ISA Guards & JIT Dead-Branch Elimination
- **Clue:** `PlatformNotSupportedException` risks, confuse-AVX-with-AVX2 bugs, or runtime feature-check overhead inside loops.
- **C# Experiment:**
  - Guard **outside** the loop using the **exact intrinsic class property** (`Avx2.IsSupported`, `Ssse3.IsSupported`, `Avx512BW.IsSupported`, `Avx512BW.VL.IsSupported`, `Bmi2.X64.IsSupported`, `AdvSimd.Arm64.IsSupported`). RyuJIT treats every `*.IsSupported` and `Vector*.IsHardwareAccelerated` property as a **JIT compile-time constant**, folding untaken ISA branches out of the native code completely!
  - Always retain—and explicitly unit-test and benchmark—the 128-bit and/or scalar fallback path.
- **Verify / Trap:**
  - **Critical Trap (`Avx` vs `Avx2` vs `Vector256`):** `Vector256.IsHardwareAccelerated` or `Avx.IsSupported` does **NOT** guarantee `Avx2.IsSupported`! (`Avx` covers 256-bit `float`/`double` FP only; 256-bit integer operations require `Avx2.IsSupported`). Calling `Avx2.*` under an `Avx.IsSupported` check throws `PlatformNotSupportedException` on AVX1-only CPUs/VMs.
  - **AVX-512 Granularity:** AVX-512 is split across `Avx512F` (32/64-bit lanes), `Avx512BW` (8/16-bit integer lanes), `Avx512DQ`, `Avx512Vbmi` (byte permutes), and `Avx512Vbmi2` (byte/word compress/expand), plus `.VL` nested classes (`Avx512BW.VL.IsSupported`) when applying EVEX instructions to 128/256-bit vectors!

### `SIMD-03` — GC-Safe Unaligned Vector Loads & Stores (`LoadUnsafe` / `StoreUnsafe`)
- **Clue:** Verbose `fixed (T* p = span)` blocks pinning GC heap objects unnecessarily just to load/store SIMD vectors.
- **C# Experiment:** Use `MemoryMarshal.GetReference(span)` + `Vector256.LoadUnsafe(ref r0, (nuint)i)` and `vec.StoreUnsafe(ref dst0, (nuint)i)`—they emit single `vmovdqu` / `ldr q` instructions using GC-tracked interior `ref`s with **zero GC pinning** and **zero sign-extension (`movsxd`)**:
  ```csharp
  ref byte srcRef = ref MemoryMarshal.GetReference(src);
  nuint i = 0, vecEnd = (nuint)(src.Length - Vector256<byte>.Count);
  for (; i <= vecEnd; i += (nuint)Vector256<byte>.Count)
  {
      Vector256<byte> v = Vector256.LoadUnsafe(ref srcRef, i);
      // ...
  }
  ```
  (Guard `src.Length >= Vector256<byte>.Count` *before* computing `src.Length - Vector256<byte>.Count` so unsigned cast cannot underflow!)
- **Verify / Trap:** Use raw `fixed` pointers only when calling pointer-only intrinsics (`Avx2.GatherVector256`, `Sse.Prefetch0`, `Avx.StoreAlignedNonTemporal`). Modern x64/Arm64 unaligned `vmovdqu` has identical throughput to aligned `vmovdqa` except when crossing cache-line/page boundaries.

### `SIMD-04` — Tail Handling: Overlapped Final Vector, Width Cascade & Masked Tails
- **Clue:** Inputs of length 17–63 or non-multiples of 32 spending 40–70% of execution time in a slow scalar remainder loop!
- **C# Experiment:** Match tail strategy to operation semantics:
  1. **Overlapped Unaligned Final Vector (Fastest when `length >= VectorCount` and operation is idempotent or pure scan):** Process full vectors up to `i + VectorCount <= length`, then if `i < length`, execute **one final unaligned vector at `offset = length - VectorCount`** overlapping the already-processed middle bytes! Zero scalar loop needed whenever `length >= 32` (for `Vector256<byte>`):
     ```csharp
     if (Vector256.IsHardwareAccelerated && span.Length >= Vector256<byte>.Count)
     {
         ref byte r0 = ref MemoryMarshal.GetReference(span);
         nuint len = (nuint)span.Length;
         nuint i = 0, last = len - (nuint)Vector256<byte>.Count;
         for (; i < last; i += (nuint)Vector256<byte>.Count)
             TransformChunk(ref r0, ref dst0, i);
         TransformChunk(ref r0, ref dst0, last); // Overlapped tail! Safe if src != dst or idempotent
         return;
     }
     ```
  2. **Width Cascade (`256 -> 128 -> 64-bit SWAR -> scalar`):** Step down cleanly for inputs `< 32` bytes (`if (rem >= 16)` `Vector128`, `if (rem >= 8)` `ulong`, then scalar).
  3. **Hardware Masked Tails (`Avx512` / `Sve`):** On `Avx512BW` / `Avx512F` (or Arm `Sve`), mask the remainder lanes directly using hardware opmasks / `CreateTrueMask*` without reading or writing past `span.Length`.
- **Verify / Trap:**
  - **Overlapped write hazard:** Do **not** use an overlapped tail if the transform mutates in-place non-idempotently (`dst[k] += 1` would double-increment overlapped lanes!) or accumulates sums without masking duplicate lanes!
  - **Illegal overread hazard:** **Never** load an unmasked vector past the logical end of `Span<T>` assuming heap padding exists—crossing into an unmapped OS guard page triggers `AccessViolationException`!

### `SIMD-05` — Benchmark `128` vs `256` vs `512` Widths as Separate Hypotheses
- **Clue:** Assuming `Vector512` > `Vector256` > `Vector128` monotonically.
- **C# Experiment:** Benchmark 128-bit, 256-bit, and (where supported) 512-bit kernels independently across short ($N \le 64$), L1-resident ($4\text{ KiB}$), and LLC-exceeding ($16\text{ MiB}$) sizes.
- **Verify / Trap:**
  - If the kernel is **DRAM bandwidth-bound**, 128-bit or 256-bit already saturates memory controllers; 512-bit cannot transfer fewer bytes.
  - On AVX2, 256-bit `Vector256` is internally **two 128-bit lanes** for many byte/word shuffles and packs (`VPSHUFB`, `VPACKUSWB`, `VPUNPCKL*`), sometimes requiring extra 3-cycle cross-lane permutes (`VPERMQ` / `VPERM2I128`) that 128-bit SSE avoids.
  - On older Intel Skylake-X / Cascade Lake CPUs, heavy 512-bit ZMM instructions can trigger core frequency downclocking (whereas Zen 4/5 and Granite Rapids handle AVX-512 without heavy license downclocking, and EVEX additionally unlocks **32 vector registers `zmm0..zmm31` + 8 mask registers `k0..k7`** vs 16 YMM registers!).

---

## 2. Scanning, Shuffles, Reductions & Compaction Idioms

### `SIMD-06` — Compare + `MoveMask` / `ExtractMostSignificantBits` + Bit-Walking
- **Clue:** Searching arrays/spans for matching elements, counting matches, validating ranges, or locating all indices matching a predicate.
- **C# Experiment:** Compare lanes -> extract 1 bit per lane/byte into a scalar `uint` -> count (`BitOperations.PopCount`) or jump directly to matching indices (`TrailingZeroCount` + `mask &= mask - 1`):
  ```csharp
  Vector256<byte> chunk = Vector256.LoadUnsafe(ref r0, i);
  Vector256<byte> eq = Vector256.Equals(chunk, Vector256.Create(needle));
  uint mask = eq.ExtractMostSignificantBits(); // VPMOVMSKB on x64 (1 bit per byte)
  while (mask != 0)
  {
      int bitPos = BitOperations.TrailingZeroCount(mask);
      OnMatch((int)i + bitPos);
      mask &= mask - 1; // Clears lowest set bit
  }
  ```
- **Verify / Trap:**
  - **The `Avx2.MoveMask` Byte-vs-Lane Trap:** `Avx2.MoveMask(Vector256<byte>)` (`VPMOVMSKB`) returns **32 bits (1 bit per byte)**. If you compare `Vector256<int>` (`VPCMPEQD`) and call `Avx2.MoveMask(cmp.AsByte())`, **each matching `int` sets 4 consecutive bits** (`PopCount` is $4\times$ the match count, and `TrailingZeroCount` gives a *byte* offset `bitPos`, requiring `bitPos >> 2` for the `int` index!). Instead, either use `eqInt.ExtractMostSignificantBits()` (which extracts 1 bit per `int` lane!) or `Avx.MoveMask(cmp.AsSingle())` (`VMOVMSKPS`, 8 bits = 1 bit per 32-bit lane)!

### `SIMD-07` — In-Register 16-Byte Lookup Tables via `VPSHUFB` (`Ssse3` / `Avx2.Shuffle`)
- **Clue:** Classifying bytes into character classes (JSON/CSV/HTTP/XML delimiters, whitespace, hex digits, Base64, UTF-8 lead bytes) or computing per-byte popcount in parallel at 32 bytes per cycle.
- **C# Experiment:** `Ssse3.Shuffle(lut128, indices128)` / `Avx2.Shuffle(lut256, indices256)` (`VPSHUFB`) performs **16 or 32 parallel 4-bit table lookups in 1 cycle**! For arbitrary byte classification, split each input byte into `lowNibble = b & 0x0F` and `highNibble = (b >> 4) & 0x0F`, look up bitmasks in two 16-byte tables, and bitwise-AND them:
  ```csharp
  // Broadcast the SAME 16-byte LUT into BOTH 128-bit lanes of Vector256!
  Vector256<byte> lowLut  = Vector256.Create(lut16Low, lut16Low);
  Vector256<byte> highLut = Vector256.Create(lut16High, lut16High);
  Vector256<byte> nibbleMask = Vector256.Create((byte)0x0F);

  Vector256<byte> lo = Avx2.Shuffle(lowLut, input & nibbleMask);
  Vector256<byte> hi = Avx2.Shuffle(highLut, Avx2.ShiftRightLogical(input.AsUInt16(), 4).AsByte() & nibbleMask);
  Vector256<byte> matches = Vector256.Equals(lo & hi, Vector256<byte>.Zero); // or non-zero test
  ```
- **Verify / Trap:**
  - **`VPSHUFB` High-Bit Zeroing Rule:** In `Avx2.Shuffle(table, control)`, if bit 7 (`0x80`) of a `control` byte is `1`, the output byte is **zeroed (`0x00`)** rather than indexing `table[control & 0x0F]`! Mask with `& 0x0F` whenever inputs can be `>= 0x80` (non-ASCII UTF-8), *unless* you intentionally exploit bit 7 to zero out non-ASCII bytes for free!
  - **AVX2 In-Lane Rule:** `Avx2.Shuffle` cannot cross the 128-bit middle boundary—you **must** duplicate the 16-byte table into both lower and upper 128-bit halves of `Vector256<byte>`!

### `SIMD-08` — Cross-Lane Permutes, Unpacks & AoS<->SoA Transpose
- **Clue:** Rearranging lanes across 128-bit halves, deinterleaving RGB/RGBA/stereo channels, or transposing $4\times 4$ / $8\times 8$ blocks.
- **C# Experiment:**
  - Cross-lane 32/64-bit permutes on AVX2: `Avx2.PermuteVar8x32(vec, idx)` (`VPERMD`/`VPERMPS`), `Avx2.Permute4x64(vec, imm8)` (`VPERMQ`/`VPERMPD`), `Avx2.Permute2x128` (`VPERM2I128`).
  - Full 64-byte cross-lane byte permutes on AVX-512 VBMI: `Avx512Vbmi.PermuteVar64x8` / `Avx512Vbmi.VL.PermuteVar32x8` (`VPERMB`).
  - Remember that `Avx2.UnpackLow` / `UnpackHigh` (`VPUNPCKL*`/`VPUNPCKH*`) and `PackUnsignedSaturate` (`VPACKUSWB`) operate **independently within each 128-bit lane** (`[A0, B0, A1, B1]` interleaved as `[A0_lo, B0_lo | A1_lo, B1_lo]`), requiring a final `Avx2.Permute4x64(..., 0b11_01_10_00)` (`0xD8`) when strict sequential order across all 256 bits is needed!
- **Verify / Trap:** Cross-lane permutes (`VPERMD`, `VPERMQ`) have 3-cycle latency on most x86 cores (vs 1 cycle for in-lane `VPSHUFB`) and compete for shuffle port 5—keep order-insensitive accumulations unordered inside the loop and fix lane order **once** after the loop if possible!

### `SIMD-09` — Multi-Vector Accumulators & Single Post-Loop Horizontal Reduction
- **Clue:** Calling `Avx.HorizontalAdd` (`VHADDPS`) or `Vector256.Sum` **inside** a hot loop, or stalling a single SIMD accumulator on 4-cycle FP add/FMA latency.
- **C# Experiment:**
  - Unroll $2\times$–$4\times$ with independent vector accumulators (`acc0`, `acc1`, `acc2`, `acc3`), tree-add them (`(acc0 + acc1) + (acc2 + acc3)`) **once after the loop**, then reduce horizontally once (`Vector128<T> sum128 = acc.GetLower() + acc.GetUpper();` -> `Vector128.Sum(sum128)`).
- **Verify / Trap:** `VHADDPS` / `PHADDD` decodes into multiple µops with cross-lane/shuffle port pressure—almost never use horizontal instructions inside an element loop unless transposing adjacent pairs across 2–4 distinct vectors simultaneously! Check FP reassociation tolerances.

### `SIMD-10` — Widening, Saturating Math & Integer Dot Products (`VPMADDUBSW` / VNNI)
- **Clue:** Summing `byte`/`short` arrays overflowing 8/16-bit lanes, clamping pixel math (`Math.Clamp(v, 0, 255)`), or quantized `int8`/`uint8`/`int16` dot products and convolutions.
- **C# Experiment:**
  - **Byte sum without overflow (`PSADBW`):** `Avx2.SumAbsoluteDifferences(bytes, Vector256<byte>.Zero)` (`VPSADBW`) sums 8 unsigned bytes at a time into four `ushort`/`ulong` lanes in 1 instruction!
  - **Saturating math:** `Avx2.AddSaturate` / `SubtractSaturate` (`VPADDSB`/`VPADDUSB`) and `Avx2.PackUnsignedSaturate` (`VPACKUSWB`) clamp to `[0, 255]` or `[-128, 127]` with zero branches.
  - **Adjacent Multiply-Add (`VPMADDUBSW` + `VPMADDWD`):** `Avx2.MultiplyAddAdjacent(u8, s8)` multiplies 32 `byte * sbyte` pairs and sums adjacent pairs into `Vector256<short>` (watch signed 16-bit saturation on `-128 * -128` when using unsigned*signed!); chain with `Avx2.MultiplyAddAdjacent(i16, ones16)` (`VPMADDWD`) to widen-sum directly into `Vector256<int>`!
  - **VNNI / Arm DotProd:** Where `AvxVnni.IsSupported` (`Avx512Vnni`) or `AdvSimd.Arm64` (`DotProduct`) is supported, fuse 4-way `u8*i8 -> i32` accumulation into a single instruction (`VPDPBUSD` / `SDOT`/`UDOT`).
- **Verify / Trap:** Verify exact signed vs unsigned lane types (`byte` vs `sbyte`) and intermediate `short` saturation (`VPMADDUBSW` saturates to `short.MaxValue`/`MinValue` if $u_0 s_0 + u_1 s_1 > 32767$).

### `SIMD-11` — Vector Conditional Select (`BlendVariable`) & `Min`/`Max` NaN Semantics
- **Clue:** Per-element `if`/`else` inside vectorizable loops preventing SIMD lowering.
- **C# Experiment:** Compute both values in vectors and select lane-wise via `Vector256.ConditionalSelect(mask, ifTrue, ifFalse)` (lowers to `VPBLENDVB` / `VBLENDVPS` / `BIT`/`BSL`), or use `Vector256.Min` / `Max`.
- **Verify / Trap:**
  - **`Avx2.BlendVariable` / `Avx.BlendVariable` Operand Order & Sign-Bit Rule:** `Avx2.BlendVariable(left, right, mask)` selects **`right` where the MSB (bit 7 / bit 31) of `mask` is `1`**, and `left` where `0` (opposite parameter order from `Vector256.ConditionalSelect(mask, left, right)`!). Double-check parameter order!
  - **Floating-Point `Min`/`Max` NaN Trap:** `Sse.Min(a, b)` / `Avx.Min(a, b)` (`VMINPS`) is **non-commutative on `NaN`** (`a < b ? a : b`—if either lane can be `NaN`, `Sse.Min(x, NaN)` returns `NaN`, whereas `Sse.Min(NaN, x)` returns `x`!), whereas `Vector256.Min` adds extra instructions to match IEEE/`Math.Min` NaN propagation. Use `Avx.Min`/`Max` directly when `NaN` propagation must be fast and inputs are finite (or place the clamp constant second so `NaN` inputs propagate into `b` intentionally).

### `SIMD-12` — Stream Compaction (Writing Matching Elements Contiguously)
- **Clue:** `if (pred(x)) dst[outIdx++] = x;` copying matching elements into a dense output array.
- **C# Experiment:**
  - **AVX-512 (`Avx512F` / `Avx512Vbmi2`):** Single-instruction hardware compress-store (`Avx512F.CompressStore` / `Avx512F.VL` `VPCOMPRESSD`, or `Avx512Vbmi2` `VPCOMPRESSB`) + `outIdx += BitOperations.PopCount(mask)`.
  - **AVX2 (8$\times$ `int`/`float`):** Extract 8-bit lane mask (`uint m = (uint)Avx.MoveMask(cmp.AsSingle());`), look up a precomputed 256-entry `PermuteVar8x32` index table (`static ReadOnlySpan<byte>` packed 8 indices per entry), permute matching lanes to the front (`Avx2.PermuteVar8x32(data, permIdx)`), unaligned-store 8 lanes to `dst[outIdx]` (when destination has $\ge 8$-lane slack), and advance `outIdx += BitOperations.PopCount(m)`!
- **Verify / Trap:** Unaligned 8-lane overwrite requires at least 8 lanes of valid writable capacity remaining in `dst`—finish the final `< 8` destination slots with a safe loop!

### `SIMD-13` — Hardware Gather (`Avx2.GatherVector256`) vs Scalar / Prefetch
- **Clue:** Vector math reading `table[indices[i]]` from non-contiguous offsets (`VPGATHERDD` / `VGATHERDPS`).
- **C# Experiment:** Test `Avx2.GatherVector256(basePtr, indexVec, scale: 4)` against scalar loads (or `SIMD-07` `VPSHUFB` / `VPERMB` if the table fits in 16–64 bytes!).
- **Verify / Trap:** If `table` fits in 1–2 SIMD registers, **in-register shuffle (`VPSHUFB`/`VPERMD`/`VPERMB`) crushes `Gather` by 5–20$\times$**! If `table` exceeds LLC and indices are random, DRAM latency dominates both `Gather` and scalar code, and on several CPUs microcoded `VPGATHER*` can even lose to scalar loads—always benchmark on target hardware and pin `basePtr` + bounds-check `indexVec` before calling `GatherVector256`!

### `SIMD-14` — Vectorized Bitmaps & Single-Branch All-Zero Tests (`Avx.TestZ` / `VPTEST`)
- **Clue:** Intersecting/diffing `ulong[]` bitmaps or checking whether a 128/256-bit chunk is completely zero (e.g., ASCII fast-path `chunk & 0x80 == 0` or sparse empty-chunk skip).
- **C# Experiment:**
  - Bitwise ops: `Avx2.And`, `Avx2.Or`, `Avx2.Xor`, `Avx2.AndNot(a, b)` (note: **`Avx2.AndNot(left, right)` computes `(~left) & right`**!).
  - Fast branch test without `MoveMask`: `if (Avx.TestZ(vec, mask))` (`VPTEST`, sets CPU `ZF` directly if `(vec & mask) == 0` without moving through a GPR!) or `vec == Vector256<byte>.Zero`.
- **Verify / Trap:** Double-check `Avx2.AndNot(toNegate, mask)` argument order (`~a & b`, **not** `a & ~b`!), and mask unused high bits in the final `ulong` tail word.

### `SIMD-15` — Arm64 NEON (`AdvSimd.Arm64`) & SVE (`Sve`) Specifics
- **Clue:** Cross-platform code fast on x64 (`MoveMask`) running slowly on Apple Silicon / AWS Graviton / Cobalt Arm64!
- **C# Experiment:**
  - **Why `ExtractMostSignificantBits` differs on Arm64:** Arm64 NEON has **no direct `PMOVMSKB` instruction**! Extracting 16 single-bit byte signs on NEON takes a multi-instruction sequence (`Vector128.ExtractMostSignificantBits` uses `AND` + `UADDLV` / shifts). When scanning bytes on `AdvSimd.Arm64`, either test non-zero chunk first (`cmp.AsUInt64() != Vector128<ulong>.Zero` / `AdvSimd.Arm64.MaxAcross(cmp) != 0`) before extracting positions, or pack 4 bits per byte using narrowing shift (`ShiftRightLogicalNarrowingLower(cmp.AsUInt16(), 4)` `SHRN` -> `ulong` 64-bit nibble mask where each byte maps to 4 bits, `TrailingZeroCount(m) >> 2`)!
  - Exploit Arm64 strengths: `AdvSimd.Arm64.AddAcross` (`ADDV`), `Load2xVector128` / `Load4xVector128` (`LD2`/`LD4` hardware deinterleaving of RGB/RGBA/key-value pairs!), `VectorTableLookup` (`TBL` 16–64 byte LUTs), and (where hardware/OS expose SVE) `Sve` vector-length-agnostic predicated loops (`never` hardcode SVE lane counts).
- **Verify / Trap:** Always test both x64 and Arm64 paths (or keep portable `Vector128<T>`/`Vector256<T>` where codegen is verified clean on both).

---

## 3. Scalar Hardware Intrinsics, Bit Manipulation & Domain Instructions

### `SIMD-16` — `BitOperations`, `BinaryPrimitives` & BMI1 / BMI2 (`PEXT` / `PDEP` Trap!)
- **Clue:** Software loops counting bits, finding lowest/highest set bits, rounding to powers of two, swapping endianness, or packing/extracting scattered bitfields (Morton Z-order codes, chess bitboards, variable-bit codecs).
- **C# Experiment:**
  - **Portable hardware-lowered intrinsics (`System.Numerics.BitOperations`):**
    - `BitOperations.PopCount(x)` -> `POPCNT` / `CNT`
    - `BitOperations.TrailingZeroCount(x)` -> `TZCNT` / `RBIT+CLZ` (returns `32` or `64` on `x == 0`, safe!)
    - `BitOperations.LeadingZeroCount(x)` / `Log2(x)` -> `LZCNT` / `BSR` / `CLZ`
    - `BitOperations.RoundUpToPowerOf2(x)`, `IsPow2(x)` (`(x & (x - 1)) == 0 && x > 0`), `RotateLeft` / `RotateRight` (`ROL`/`ROR`/`RORX`)
  - **Endian swaps:** `BinaryPrimitives.ReverseEndianness(val)` -> single-cycle `BSWAP` / `REV` (also vectorized on `Span<int>` / `Span<short>` in modern .NET!).
  - **Explicit `Bmi1` / `Bmi2` (`System.Runtime.Intrinsics.X86`):**
    - `Bmi1.ResetLowestSetBit(x)` (`BLSR`: `x & (x - 1)`), `Bmi1.ExtractLowestSetBit(x)` (`BLSI`: `x & -x`), `Bmi1.AndNot(a, b)` (`ANDN`: `~a & b`), `Bmi1.BitFieldExtract` (`BEXTR`).
    - `Bmi2.ZeroHighBits(x, idx)` (`BZHI`), `Bmi2.MultiplyNoFlags` (`MULX`), `Bmi2.ParallelBitExtract(val, mask)` (`PEXT`), `Bmi2.ParallelBitDeposit(val, mask)` (`PDEP`).
- **Verify / Trap:**
  - **Critical Microarchitecture Trap (`PEXT` / `PDEP` on AMD Zen 1 / Zen 2):** On Intel Haswell+ and AMD **Zen 3 / Zen 4 / Zen 5**, `Bmi2.ParallelBitExtract` (`PEXT`) and `ParallelBitDeposit` (`PDEP`) execute in hardware in **3 cycles (1 cycle throughput)**. On **AMD Zen 1, Zen+, and Zen 2**, `Bmi2.IsSupported` is `true`, **but `PEXT`/`PDEP` are microcoded at 18 to ~300 cycles**, making them vastly slower than shift/mask or lookup-table SWAR! Verify target CPU generation before shipping `PEXT`/`PDEP` unconditionally under `Bmi2.IsSupported`.

### `SIMD-17` — Hardware CRC32C, Carry-Less Multiply (`PCLMULQDQ`), AES & Fast FP Math
- **Clue:** Software table-driven CRC32/hashing, quote/escape string parity masks (JSON/CSV), Galois-field math, fast non-cryptographic mixing, or vector polynomial math.
- **C# Experiment:**
  - **Hardware CRC32C:** `Sse42.X64.Crc32(crc, ulongVal)` / `Arm.Crc32.Arm64.ComputeCrc32C(crc, ulongVal)` processes 8 bytes in 3 cycles (pipeline 3 independent streams across chunk thirds for $3\times$ ILP!). Note: `Sse42.Crc32` computes **Castagnoli CRC-32C** (`0x1EDC6F41`), *not* IEEE 802.3 CRC-32 (`System.IO.Hashing.Crc32` already uses `PCLMULQDQ` folding for IEEE CRC-32!).
  - **Carry-Less Multiplication (`Pclmulqdq.CarrylessMultiply` / `Arm.Aes.PolynomialMultiplyWideningLower`):** Computes 64$\times$64 -> 128-bit XOR-product (`VPCLMULQDQ`). Essential for branchless **quote-region prefix-XOR** in SIMD JSON/CSV parsers (`CarrylessMultiply(quoteMaskVec, Vector128.Create(~0UL), 0x00)` flips bits inside every quoted pair in 1 instruction!).
  - **Hardware AES Rounds:** `System.Runtime.Intrinsics.X86.Aes.Encrypt` (`VAESENC`) / `Arm.Aes` provides high-throughput hardware diffusion (used by modern non-crypto string hashes when guarded; for real cryptography, always use `System.Security.Cryptography.AesGcm` / `Aes` for constant-time + key-schedule safety).
  - **FMA & Reciprocal Sqrt:** `Fma.MultiplyAdd(a, b, c)` (`VFMADD*`) fuses `a * b + c` into 1 rounding and doubles FLOP throughput; `Sse.ReciprocalSqrt` (`VRSQRTPS`, ~12-bit accuracy) + one Newton-Raphson step $y \cdot (1.5 - 0.5 \cdot x \cdot y^2)$ reaches near-`float` precision at 3–5$\times$ `VSQRTPS + VDIVPS` throughput when an explicit FP error contract exists.
- **Verify / Trap:** Never substitute approximate `Reciprocal` / `ReciprocalSqrt` without an explicit error budget and guards for `0.0`, `+Infinity`, and `NaN` (`Infinity * 0.0 == NaN` breaks naive Newton-Raphson on `0` or `+Inf`!).

### `SIMD-18` — Register Spills, `VZEROUPPER` & Calling Conventions
- **Clue:** Adding more vector locals slows the loop down, or calls inside an AVX2/AVX-512 loop emit repeated `vzeroupper` + stack saves.
- **C# Experiment:**
  - x64 Windows/Linux standard ABI gives **16 YMM registers (`ymm0`–`ymm15`)** (`AVX-512` EVEX unlocks 32 `zmm0`–`zmm31`). On Windows x64, `xmm6`–`xmm15` are **callee-saved** (and upper halves of `ymm`/`zmm` are volatile across non-inlined calls!), so calling any non-inlined helper inside a SIMD loop forces RyuJIT to spill live `ymm`/`zmm` values to `[rsp+...]` and emit `vzeroupper`!
  - Keep inner SIMD loops free of non-inlined calls (extract cold paths to `NoInlining` throw/slow-path methods outside the vector loop, inline tiny vector helpers with `[MethodImpl(MethodImplOptions.AggressiveInlining)]`), and keep live vector count $\le 12\text{--}14$ on AVX2 to avoid stack spills.
- **Verify / Trap:** Confirm zero `[rsp+offset]` vector spills and zero `vzeroupper` inside the hot loop back-edge via `lab.inspect` `native`.
