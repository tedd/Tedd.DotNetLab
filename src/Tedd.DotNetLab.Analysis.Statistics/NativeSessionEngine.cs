using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Analysis.Statistics;

public static class NativeSessionEngine
{
    public const string Version="paired-session-mean-log-native-batch-cost/v1";
    public static CostPair Aggregate(string sessionId,IReadOnlyList<CostPair> blocks)
    {
        if(string.IsNullOrWhiteSpace(sessionId)||blocks.Count is <1 or >15||blocks.Select(b=>b.BlockId).Distinct().Count()!=blocks.Count||
            blocks.Any(b=>b.ClusterId!=sessionId||!double.IsFinite(b.Baseline)||!double.IsFinite(b.Candidate)||b.Baseline<=0||b.Candidate<=0))
            throw new LabException("invalid_statistical_input","Native aggregation requires complete distinct matched positive blocks within one session.");
        double original=Math.Exp(StatisticalEngine.Mean(blocks.Select(b=>Math.Log(b.Baseline)).ToArray()));
        double candidate=Math.Exp(StatisticalEngine.Mean(blocks.Select(b=>Math.Log(b.Candidate)).ToArray()));
        if(!double.IsFinite(original)||!double.IsFinite(candidate)||original<=0||candidate<=0)
            throw new LabException("invalid_statistical_input","Session geometric costs exceed the finite positive range.");
        return new(sessionId,sessionId,original,candidate);
    }
}
