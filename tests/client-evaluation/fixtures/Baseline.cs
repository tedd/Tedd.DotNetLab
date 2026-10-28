using System.Globalization;

public static class Baseline
{
    public static long SumSquares(int[] values) => values.Select(x => (long)x * x).ToArray().Aggregate(0L, (a, b) => unchecked(a + b));
    public static long Csv(string text) => text.Length == 0 ? 0 : text.Split(',').Select(x => long.Parse(x, CultureInfo.InvariantCulture)).Aggregate(0L, (a, b) => unchecked(a + b));
    public static int Membership(string[] queries, string[] allowed) => queries.Count(q => allowed.Contains(q, StringComparer.Ordinal));
    public static int CountByte(byte[] bytes, byte needle) => bytes.Where(x => x == needle).ToArray().Length;
    public static string Join(string[] fields)
    {
        string result = "";
        for (int i = 0; i < fields.Length; i++) result += (i == 0 ? "" : "|") + fields[i];
        return result;
    }
    public static int[] Deduplicate(int[] values)
    {
        var result = new List<int>();
        foreach (int value in values) if (!result.Contains(value)) result.Add(value);
        return result.ToArray();
    }
    public static long[] Prefix(int[] values)
    {
        var result = new long[values.Length];
        for (int i = 0; i < values.Length; i++)
            for (int j = 0; j <= i; j++) result[i] += values[j];
        return result;
    }
    public static async ValueTask<long> CachedSum(int[] cached, CancellationToken cancellation)
    {
        long sum = 0;
        foreach (int value in cached) { cancellation.ThrowIfCancellationRequested(); await Task.Yield(); sum += value; }
        cancellation.ThrowIfCancellationRequested();
        return sum;
    }
}
