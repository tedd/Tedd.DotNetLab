// Only this file is editable by the evaluated client. Preserve every public signature.
public static class Candidate
{
    public static long SumSquares(int[] values) => Baseline.SumSquares(values);
    public static long Csv(string text) => Baseline.Csv(text);
    public static int Membership(string[] queries, string[] allowed) => Baseline.Membership(queries, allowed);
    public static int CountByte(byte[] bytes, byte needle) => Baseline.CountByte(bytes, needle);
    public static string Join(string[] fields) => Baseline.Join(fields);
    public static int[] Deduplicate(int[] values) => Baseline.Deduplicate(values);
    public static long[] Prefix(int[] values) => Baseline.Prefix(values);
    public static ValueTask<long> CachedSum(int[] cached, CancellationToken cancellation) => Baseline.CachedSum(cached, cancellation);
}
