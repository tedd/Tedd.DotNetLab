namespace Tedd.DotNetLab.Contracts;

public record LiveBenchmarkPolicy(int InitialOperations = 1, int? OperationsPerIteration = null,
    int MaximumOperationsPerIteration = 1_000_000, long MaximumTotalOperations = 100_000_000,
    int TargetIterationMilliseconds = 20, int MeasurementIterations = 15,
    int MinimumWarmupIterations = 6, int MaximumWarmupIterations = 40,
    double WarmupTolerance = .05, double MaximumCoefficientOfVariation = .10, double MaximumDrift = .05,
    int OverheadIterations = 9)
{
    public void Validate()
    {
        if (InitialOperations < 1 || MaximumOperationsPerIteration is < 1 or > 100_000_000 ||
            InitialOperations > MaximumOperationsPerIteration || OperationsPerIteration is < 1 ||
            OperationsPerIteration > MaximumOperationsPerIteration || MaximumTotalOperations is < 1 or > 1_000_000_000 ||
            TargetIterationMilliseconds is < 1 or > 1000 || MeasurementIterations is < 6 or > 100 ||
            MinimumWarmupIterations is < 6 or > 100 || MaximumWarmupIterations < MinimumWarmupIterations || MaximumWarmupIterations > 100 ||
            OverheadIterations is < 5 or > 31 || !Fraction(WarmupTolerance) || !Fraction(MaximumCoefficientOfVariation) || !Fraction(MaximumDrift))
            throw new LabException("invalid_plan", "Live benchmark calibration, warmup, sampling, or operation budget is invalid.");
    }
    private static bool Fraction(double value) => double.IsFinite(value) && value > 0 && value <= 1;
}

public record LiveBenchmarkIteration(string Phase, int Ordinal, Measurement Measurement);
public record LiveBenchmarkSession(string Protocol, string PolicyHash, int OperationsPerIteration,
    bool WarmupConverged, bool Correct, bool Quiescent, string? Failure, IReadOnlyList<LiveBenchmarkIteration> Iterations,
    string OverheadScope = "Empty batch delegate and timer floor; excludes workload-owned loops; raw measurements are not overhead-subtracted.");
