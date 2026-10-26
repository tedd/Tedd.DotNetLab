using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Tedd.DotNetLab.Analysis.Statistics;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.StatisticalQualification;

static string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
try
{
    var arguments=new Dictionary<string,string>(StringComparer.Ordinal);
    if(args.Length%2!=0) throw new ArgumentException("Use --plan PATH --oracle PATH --output PATH [--design ID].");
    for(int i=0;i<args.Length;i+=2) if(!arguments.TryAdd(args[i],args[i+1])) throw new ArgumentException("Repeated argument.");
    if(arguments.Keys.Any(key=>key is not ("--plan" or "--oracle" or "--output" or "--design"))) throw new ArgumentException("Unknown argument.");
    string output=Path.GetFullPath(arguments["--output"]);
    if(!output.StartsWith(@"D:\Workspaces\AI\",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Qualification output must be in the designated task workspace.");
    for(var directory=new DirectoryInfo(Path.GetDirectoryName(output)!);directory is not null;directory=directory.Parent)
        if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0) throw new ArgumentException("Output ancestors cannot be reparse points.");
    if(File.Exists(output)) throw new ArgumentException("Output already exists; preserve the preceding qualification report.");
    string planPath=arguments["--plan"],oraclePath=arguments["--oracle"];
    var plan=Wire.Deserialize<SimulationPlan>(File.ReadAllText(planPath)); Simulation.Validate(plan);
    var oracle=Wire.Deserialize<SimulationOracle>(File.ReadAllText(oraclePath));
    string planHash=Hash(planPath),oracleHash=Hash(oraclePath);
    if(oracle.PlanHash!=planHash) throw new InvalidDataException("Independent oracle is bound to a different simulation plan.");
    var designs=plan.Designs.Where(d=>!arguments.TryGetValue("--design",out string? id)||d.Id==id).ToArray();
    if(designs.Length==0) throw new ArgumentException("No selected design.");
    var outcomes=new List<SimulationOutcome>(); using var cancellation=new CancellationTokenSource();
    Console.CancelKeyPress+=(_,e)=>{ e.Cancel=true; cancellation.Cancel(); };
    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
    QualificationReport Report()=>new(plan.Version,planHash,oracleHash,Environment.Version.ToString(),RuntimeInformation.OSDescription,
        Hash(typeof(StatisticalEngine).Assembly.Location),Hash(typeof(Simulation).Assembly.Location),plan.Experiments,plan.Resamples,plan.SampleAlgorithm,
        outcomes.Count==plan.Designs.Count,outcomes.Count==plan.Designs.Count&&outcomes.All(o=>o.Passed),outcomes.ToArray());
    foreach(var design in designs)
    {
        var expected=oracle.Designs.Single(d=>d.Id==design.Id);
        var outcome=Simulation.Execute(plan,design,expected,n=>Console.WriteLine($"{design.Id}: {n}/{plan.Experiments}"),cancellation.Token); outcomes.Add(outcome);
        // A finished design is durable before beginning the next ensemble.
        string checkpoint=output+".partial"; File.WriteAllText(checkpoint,Wire.Serialize(Report()));
        using(var stream=new FileStream(checkpoint,FileMode.Open,FileAccess.ReadWrite,FileShare.None)) stream.Flush(true);
        File.Move(checkpoint,output,true);
        Console.WriteLine($"{design.Id}: {outcome.Assessment}; oracle={outcome.OracleCountsAgree&&outcome.OracleFirstAgrees}; gates={outcome.Checks.All(c=>c.Passed)}");
    }
    return outcomes.All(o=>o.Passed)?0:2;
}
catch(Exception error) when(error is ArgumentException or KeyNotFoundException or InvalidDataException or InvalidOperationException or OperationCanceledException or IOException)
{ Console.Error.WriteLine(error.Message); return 1; }
