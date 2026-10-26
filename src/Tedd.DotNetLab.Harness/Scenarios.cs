using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Harness;

public record ScenarioContext(string Variant, CancellationToken Cancellation);
public record ResetContext(ResetLevel Level);
public interface ISynchronousScenario
{
    ScenarioDescriptor Describe();
    void Prepare(ScenarioContext context);
    void Reset(ResetContext context);
    long ExecuteBatch(int operationCount);
    ValidationResult Validate();
    void Cleanup();
}
public interface IAsynchronousScenario
{
    ScenarioDescriptor Describe();
    ValueTask PrepareAsync(ScenarioContext context);
    ValueTask ResetAsync(ResetContext context);
    ValueTask<long> ExecuteBatchAsync(int operationCount, CancellationToken cancellation);
    ValueTask<ValidationResult> ValidateAsync();
    ValueTask CleanupAsync();
}
public interface IScenarioDispatcher
{
    Task<T> DispatchAsync<T>(Func<T> action, CancellationToken cancellation);
}
public interface IApplicationMutationBoundary : IScenarioDispatcher
{
    Task<IDisposable> HoldAsync(CancellationToken cancellation);
}
public sealed class InlineDispatcher : IScenarioDispatcher
{
    public Task<T> DispatchAsync<T>(Func<T> action, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        return Task.FromResult(action());
    }
}
public sealed class ScenarioRegistry
{
    private readonly Dictionary<string,object> scenarios = new(StringComparer.Ordinal);
    public void Register(ISynchronousScenario scenario) => Register(scenario.Describe().Id,scenario);
    public void Register(IAsynchronousScenario scenario) => Register(scenario.Describe().Id,scenario);
    private void Register(string id,object scenario)
    {
        if (!scenarios.TryAdd(id,scenario)) throw new ArgumentException("Scenario ID is already registered.");
    }
    public object Get(string id) => scenarios.GetValueOrDefault(id) ?? throw new LabException("scenario_not_found","Unknown registered scenario.");
    public IReadOnlyList<ScenarioDescriptor> Describe() => scenarios.Values.Select(Describe).ToArray();
    public static ScenarioDescriptor Describe(object scenario) => scenario switch
    {
        ISynchronousScenario sync => sync.Describe(),
        IAsynchronousScenario asyncScenario => asyncScenario.Describe(),
        _ => throw new ArgumentException("Invalid scenario.")
    };
}
