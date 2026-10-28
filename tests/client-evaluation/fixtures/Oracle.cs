using System.Globalization;
using System.Numerics;

// These checks use independent definitions and deterministic generated inputs. The runner
// verifies this file's hash before rebuilding an agent's candidate.
public static class Oracle
{
    public static async Task<int> Verify()
    {
        int checks = 0;
        void Check(bool correct, string reason) { checks++; if (!correct) throw new InvalidOperationException(reason); }
        var random = new Random(73129);
        int[] lengths = [0, 1, 2, 7, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 256, 1025];
        foreach (int length in lengths)
        for (int repetition = 0; repetition < 8; repetition++)
        {
            int[] values = Enumerable.Range(0, length).Select(_ => random.Next(-10000, 10001)).ToArray();
            int[] original = (int[])values.Clone();
            long squares = (long)values.Aggregate(BigInteger.Zero, (sum, x) => sum + (BigInteger)x * x);
            Check(Candidate.SumSquares(values) == squares, "sum-squares");
            long sum = (long)values.Aggregate(BigInteger.Zero, (s, x) => s + x);
            string csv = string.Join(",", values.Select(x => " " + x.ToString(CultureInfo.InvariantCulture) + " "));
            Check(Candidate.Csv(csv) == sum, "csv");
            byte[] bytes = new byte[length]; random.NextBytes(bytes);
            byte needle = (byte)repetition;
            int expectedCount = 0;
            for (int i = 0; i < bytes.Length; i++) if (bytes[i] == needle) expectedCount++;
            Check(Candidate.CountByte(bytes, needle) == expectedCount, "byte-scan-tail");
            var prefix = Candidate.Prefix(values);
            Check(prefix.Length == values.Length && (prefix.Length == 0 || prefix[0] == values[0]), "prefix-first");
            for (int i = 1; i < prefix.Length; i++) Check(prefix[i] - prefix[i - 1] == values[i], "prefix-recurrence");
            var unique = Candidate.Deduplicate(values);
            Check(unique.Distinct().Count() == unique.Length && unique.ToHashSet().SetEquals(values), "dedup-set");
            Check(unique.Select(x => Array.IndexOf(values, x)).SequenceEqual(unique.Select(x => Array.IndexOf(values, x)).Order()), "dedup-order");
            Check(await Candidate.CachedSum(values, CancellationToken.None) == sum, "async-cache-sum");
            Check(values.SequenceEqual(original), "input-array-immutable");
        }
        byte[] allBytes = Enumerable.Range(0, 513).Select(x => (byte)x).ToArray();
        for (int needle = 0; needle < 256; needle++)
            Check(Candidate.CountByte(allBytes, (byte)needle) == (needle == 0 ? 3 : 2), "all-byte-values");
        Check(allBytes.SequenceEqual(Enumerable.Range(0, 513).Select(x => (byte)x)), "byte-input-immutable");
        foreach (string[] fields in new string[][] { [], [""], ["", ""], ["a", "", "b"], ["|", "x|y", "λ", "😀", "\0"] })
        {
            string joined = Candidate.Join(fields);
            string expected = fields.Length == 0 ? "" : fields.Aggregate((a, b) => a + "|" + b);
            Check(joined == expected, "join-empty-unicode");
        }
        for (int repetition = 0; repetition < 32; repetition++)
        {
            string[] fields = Enumerable.Range(0, repetition * 5).Select(x => x % 3 == 0 ? "" : "λ|😀" + random.Next()).ToArray();
            string expected = fields.Length == 0 ? "" : fields.Aggregate((a, b) => a + "|" + b);
            Check(Candidate.Join(fields) == expected, "join-generated");
        }
        foreach (string[] allowed in new string[][] { [], ["a"], ["a", "a", "A", "λ", "😀", ""] })
        {
            string[] queries = ["a", "A", "a", "b", "", "λ", "😀"];
            int expected = queries.Sum(q => allowed.Any(a => string.Equals(q, a, StringComparison.Ordinal)) ? 1 : 0);
            Check(Candidate.Membership(queries, allowed) == expected, "ordinal-duplicate-membership");
        }
        Check(Candidate.SumSquares([int.MinValue, int.MaxValue]) == unchecked((long)int.MinValue * int.MinValue + (long)int.MaxValue * int.MaxValue), "widen-before-multiply");
        Check(Candidate.SumSquares([int.MinValue, int.MinValue]) == long.MinValue, "unchecked-square-total");
        Check(Candidate.Csv("9223372036854775807,1") == long.MinValue, "csv-unchecked-total");
        Check(Candidate.Csv(" -9223372036854775808, +1 ") == long.MinValue + 1, "csv-signed-boundaries");
        int[] reused = [1, 2, 1];
        Check(Candidate.SumSquares(reused) == 6 && Candidate.Prefix(reused).SequenceEqual(new long[] { 1, 3, 4 }), "reused-array-initial");
        Check(Candidate.Deduplicate(reused).SequenceEqual(new[] { 1, 2 }) && await Candidate.CachedSum(reused, CancellationToken.None) == 4, "reused-sequence-initial");
        reused[0] = 3;
        Check(Candidate.SumSquares(reused) == 14 && Candidate.Prefix(reused).SequenceEqual(new long[] { 3, 5, 6 }), "reused-array-mutated");
        Check(Candidate.Deduplicate(reused).SequenceEqual(new[] { 3, 2, 1 }) && await Candidate.CachedSum(reused, CancellationToken.None) == 6, "reused-sequence-mutated");
        string[] mutableFields = ["a", "b"];
        Check(Candidate.Join(mutableFields) == "a|b", "join-reused-initial");
        mutableFields[0] = "z";
        Check(Candidate.Join(mutableFields) == "z|b", "join-reused-mutated");
        byte[] mutableBytes = [1, 2, 1];
        Check(Candidate.CountByte(mutableBytes, 1) == 2, "byte-reused-initial");
        mutableBytes[0] = 2;
        Check(Candidate.CountByte(mutableBytes, 1) == 1, "byte-reused-mutated");
        string[] mutableAllowed = ["a"], mutableQueries = ["a", "a", "b"];
        Check(Candidate.Membership(mutableQueries, mutableAllowed) == 2, "membership-reused-initial");
        mutableAllowed[0] = "b";
        Check(Candidate.Membership(mutableQueries, mutableAllowed) == 1, "membership-allowed-mutated");
        mutableQueries[0] = "b";
        Check(Candidate.Membership(mutableQueries, mutableAllowed) == 2, "membership-queries-mutated");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        foreach (int[] values in new int[][] { [], [1] })
        {
            bool threw = false;
            try { await Candidate.CachedSum(values, cancelled.Token); } catch (OperationCanceledException) { threw = true; }
            Check(threw, "cache-cancelled");
        }
        return checks;
    }
}
