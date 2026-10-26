using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Domain;

public static class Policies
{
    public static void Validate(ResourceLimits limits)
    {
        if(limits.MaximumCaptureBytes is <1 or >2147483648||limits.MaximumDurationSeconds is <1 or >600||limits.DiskReserveBytes<0||
            limits.DrainTimeoutSeconds is <1 or >120||limits.MaximumRows is <1 or >1000||limits.InlineBytes is <1024 or >65536||
            limits.MaximumParserMemoryBytes is <134217728 or >2147483648||limits.MaximumParserDurationSeconds is <1 or >600||
            limits.MaximumParserEvents is <1 or >1000000||limits.MaximumParserOutputBytes is <1024 or >16777216||
            limits.MaximumConcurrentOperations is <1 or >64||limits.MaximumQueuedActivities is <1 or >1024)
            throw new LabException("invalid_plan","Workspace resource policy exceeds its supported bounds.");
    }
    public static void Validate(ExperimentPlan plan)
    {
        if(plan.TargetRuntimeVersion is { } version&&!RuntimeSupport.IsSelectableManagedVersion(version))
            throw new LabException("invalid_plan","Target runtime requires an exact .NET version of 10.0.0 or later, including any preview suffix.");
        if (plan.Variants.Count is not (2 or 4) || plan.Variants.Distinct().Count() != plan.Variants.Count ||
            plan.Blocks is < 1 or > 10000 || plan.OperationsPerRun is < 1 or > 100000000 ||
            plan.WarmupBatches is < 0 or > 1000 || plan.MaximumDurationSeconds is < 1 or > 600 ||
            string.IsNullOrWhiteSpace(plan.ScenarioId) || string.IsNullOrWhiteSpace(plan.InputHash))
            throw new LabException("invalid_plan", "Invalid scenario, variants, sample budget, or limits.");
        Validate(plan.Analysis);
        Validate(Metrics.Primary(plan));
        if(plan.DiagnosticWindows is { } windows&&(!double.IsFinite(windows.ResidualToleranceMilliseconds)||windows.ResidualToleranceMilliseconds is <=0 or >10||
            windows.ParserVersion!="traceevent-runtime/3"||windows.WindowAlgorithm!="target-clock-window/v1"))
            throw new LabException("invalid_plan","Unsupported diagnostic parser/window algorithm or invalid frozen clock tolerance.");
        var guardrails=plan.GuardrailPolicies??[];
        var names=plan.GuardrailMetrics??[];
        if(guardrails.Count>8||guardrails.Select(p=>Metrics.Identity(p.Metric)).Distinct().Count()!=guardrails.Count||
            guardrails.Any(p=>Metrics.Identity(p.Metric)==Metrics.Identity(plan.PrimaryMetric))||names.Count!=guardrails.Count||
            names.Distinct().Count()!=names.Count||names.Any(name=>!guardrails.Any(p=>p.Metric==name)))
            throw new LabException("invalid_plan","Every distinct declared guardrail requires its frozen metric policy.");
        foreach(var guardrail in guardrails) Validate(guardrail);
        if(plan.Factorial is { } factorial)
        {
            if(plan.Variants.Count!=4||!plan.Variants.SequenceEqual(new[]{factorial.Variant00,factorial.Variant10,factorial.Variant01,factorial.Variant11})||
                string.IsNullOrWhiteSpace(factorial.ComponentAId)||string.IsNullOrWhiteSpace(factorial.ComponentBId)||string.IsNullOrWhiteSpace(factorial.CombinationId)||
                new[]{factorial.ComponentAId,factorial.ComponentBId,factorial.CombinationId}.Distinct().Count()!=3)
                throw new LabException("invalid_plan","Factorial plans require the ordered complete four-arm mapping and distinct declared components/combination.");
            var metrics=new[]{plan.PrimaryMetric}.Concat(names).ToArray();
            if(factorial.InteractionScales.Count!=metrics.Length||metrics.Any(metric=>!factorial.InteractionScales.ContainsKey(metric)))
                throw new LabException("invalid_plan","Every factorial metric requires its predeclared interaction scale.");
            foreach(var pair in factorial.InteractionScales)
                if(!Enum.IsDefined(pair.Value)||pair.Value==ContrastScale.Log&&Metrics.Define(pair.Key).Scale!=EffectScale.PositiveRatio)
                    throw new LabException("invalid_plan","Log interactions require a positive-cost metric; allocation interactions use an absolute scale.");
        }
        int comparisons=(plan.Factorial is null?plan.Variants.Count-1:6)*(1+guardrails.Count(p=>!p.DescriptiveOnly));
        if(plan.Analysis.FamilySize<comparisons)
            throw new LabException("invalid_plan","Comparison family must cover every inferential arm and metric.");
        if (plan.IndependentBlocks && string.IsNullOrWhiteSpace(plan.IndependenceJustification))
            throw new LabException("invalid_plan", "Independence requires an explicit justification.");
        if (plan.Confirmatory && (!plan.IndependentBlocks || plan.Profile != "quiet-validation"))
            throw new LabException("invalid_plan", "Confirmation requires independent blocks and quiet measurements.");
        Profiles.Resolve(plan.Profile);
    }
    public static void Validate(AnalysisPolicy policy)
    {
        var numbers = new[] { policy.AlphaFamily, policy.EquivalenceLowerRatio, policy.EquivalenceUpperRatio,
            policy.UsefulImprovementRatio, policy.MeaningfulRegressionRatio };
        if (numbers.Any(x => !double.IsFinite(x)) || policy.AlphaFamily is <= 0 or >= .5 ||
            policy.FamilySize is < 1 or > 10000 || policy.MinimumBlocks < 10 ||
            !(0 < policy.UsefulImprovementRatio && policy.UsefulImprovementRatio <= policy.EquivalenceLowerRatio &&
              policy.EquivalenceLowerRatio < 1 && 1 < policy.EquivalenceUpperRatio &&
              policy.EquivalenceUpperRatio <= policy.MeaningfulRegressionRatio))
            throw new LabException("invalid_plan", "Invalid alpha, sample guardrail, or overlapping decision thresholds.");
        double local=policy.AlphaFamily/policy.FamilySize;
        if(local<=0||1-local/2>=1||1-local>=1)
            throw new LabException("invalid_plan","Local alpha cannot be represented by the qualified binary64 interval procedure.");
    }
    public static void Authorize(Grant grant, string workspace, TargetIdentity target, Permission permission,
        DateTimeOffset now, string? hash = null,string? methodScope=null)
    {
        if (grant.Workspace != workspace || grant.TargetId != target.Id ||
            grant.StartTimeUtcTicks != target.StartTimeUtcTicks || grant.Permission != permission ||
            grant.ExpiresAt <= now || grant.MaximumOperations <= 0 || grant.ContentHash != hash||grant.MethodScope!=methodScope)
            throw new LabException("permission_denied", "Grant is expired, exhausted, or outside the requested scope.");
    }
    public static void Validate(MetricPolicy policy)
    {
        var definition=Metrics.Define(policy.Metric);
        if(policy.Unit!=definition.Unit||policy.ObservationKind!=definition.ObservationKind||policy.Direction!=definition.Direction||policy.Scale!=definition.Scale||
            policy.MinimumBlocks is <10 or >10000||!new[]{policy.EquivalenceLower,policy.EquivalenceUpper,policy.UsefulImprovement,policy.MeaningfulRegression}.All(double.IsFinite))
            throw new LabException("invalid_plan","Metric unit, direction, observation type, estimator scale, or budget differs from its qualified definition.");
        double center=policy.Scale==EffectScale.PositiveRatio?1:0;
        bool ordered=policy.Direction==MetricDirection.LowerIsBetter?
            policy.UsefulImprovement<=policy.EquivalenceLower&&policy.EquivalenceLower<center&&center<policy.EquivalenceUpper&&policy.EquivalenceUpper<=policy.MeaningfulRegression:
            policy.MeaningfulRegression<=policy.EquivalenceLower&&policy.EquivalenceLower<center&&center<policy.EquivalenceUpper&&policy.EquivalenceUpper<=policy.UsefulImprovement;
        if(!ordered||policy.Scale==EffectScale.PositiveRatio&&new[]{policy.EquivalenceLower,policy.EquivalenceUpper,policy.UsefulImprovement,policy.MeaningfulRegression}.Any(x=>x<=0))
            throw new LabException("invalid_plan","Objective thresholds overlap or are invalid for the declared scale/direction.");
    }
    public static void Validate(Measurement value)
    {
        if (value.Frequency <= 0 || value.StartTicks < 0 || value.EndTicks <= value.StartTicks ||
            value.CompletedOperations <= 0 || value.ProcessAllocationBytes < 0 || value.ThreadAllocationBytes < 0)
            throw new LabException("invalid_measurement", "Invalid clock, completed work, or allocation delta.");
        if (value.ThreadAllocationBytes.HasValue && value.StartThreadId != value.EndThreadId)
            throw new LabException("invalid_measurement", "Current-thread allocation crossed threads.");
        if (value.Telemetry is { } telemetry)
        {
            if (telemetry.Gen0Collections < 0 || telemetry.Gen1Collections < 0 || telemetry.Gen2Collections < 0 ||
                telemetry.HeapSizeBytes < 0 || telemetry.FragmentedBytes < 0 || telemetry.TotalCommittedBytes < 0 ||
                telemetry.PinnedObjectsCount < 0 || telemetry.ThreadCpuCycles < 0 || telemetry.ThreadCpuNanoseconds < 0 ||
                telemetry.InstructionsRetired < 0 || telemetry.HardwareCpuCycles < 0 ||
                telemetry.BranchMisses < 0 || telemetry.CacheMisses < 0)
                throw new LabException("invalid_measurement", "Batch telemetry contains negative counter or heap values.");
            bool hasThreadCounter = telemetry.ThreadCpuCycles.HasValue || telemetry.ThreadCpuNanoseconds.HasValue ||
                telemetry.InstructionsRetired.HasValue || telemetry.HardwareCpuCycles.HasValue ||
                telemetry.BranchMisses.HasValue || telemetry.CacheMisses.HasValue;
            if (hasThreadCounter && value.StartThreadId != value.EndThreadId)
                throw new LabException("invalid_measurement", "Current-thread CPU/PMU counters crossed threads.");
        }
    }
}

public static class Profiles
{
    public static IReadOnlyList<string> Names { get; } = new[]
        { "quiet-validation", "baseline-context", "cpu-diagnostic", "allocation-diagnostic", "targeted-probes", "runtime-metrics-context", "jit-and-contention-diagnostic" };
    public static CaptureProfile Resolve(string name)
    {
        if (!Names.Contains(name)) throw new LabException("invalid_profile", "Unknown capture profile.");
        if (name == "targeted-probes") return new(name,"selected-probes/net10/1",[],32,false,["harness","selected_probes"]);
        if (name == "quiet-validation") return new(name, "net10/1", [], 32, false, ["harness"]);
        if(name=="runtime-metrics-context") return new(name,"net10/metrics/1",
            [new("System.Diagnostics.Metrics",6,4,new Dictionary<string,string> { ["Metrics"]="System.Runtime",["RefreshInterval"]="1",
                ["MaxTimeSeries"]="1000",["MaxHistograms"]="20" }),new("Tedd.DotNetLab-Harness",-1,4,new Dictionary<string,string>())],32,false,["counters","modern_runtime_metrics","markers"]);
        if (name == "jit-and-contention-diagnostic") return new(name, "net10/jit-contention/1",
            [new("Microsoft-Windows-DotNETRuntime", 1 | 0x8 | 0x10 | 0x1000 | 0x4000, 5, new Dictionary<string,string>()),
             new("Tedd.DotNetLab-Harness", -1, 4, new Dictionary<string,string>()),
             new("System.Runtime", 0, 4, new Dictionary<string,string> { ["EventCounterIntervalSec"] = "1" })],
            32, true, ["gc", "suspension", "counters", "markers", "jit", "inlining", "contention"]);
        var providers = new List<ProviderSpec>
        {
            new("Microsoft-Windows-DotNETRuntime", 1, name == "allocation-diagnostic" ? 5 : 4, new Dictionary<string,string>()),
            new("Tedd.DotNetLab-Harness", -1, 4, new Dictionary<string,string>()),
            new("System.Runtime", 0, 4, new Dictionary<string,string> { ["EventCounterIntervalSec"] = "1" })
        };
        var classes = new List<string> { "gc", "suspension", "counters", "markers" };
        if (name == "allocation-diagnostic") classes.Add("allocation_samples");
        if (name == "cpu-diagnostic")
        {
            providers[0] = providers[0] with { Keywords = 1 | 0x10 | 0x8 };
            providers.Add(new("Microsoft-DotNETCore-SampleProfiler", 0, 4, new Dictionary<string,string>()));
            classes.Add("samples");
        }
        return new(name, "net10/1", providers, 32, name == "cpu-diagnostic", classes);
    }
}
