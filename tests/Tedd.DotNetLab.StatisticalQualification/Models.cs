namespace Tedd.DotNetLab.StatisticalQualification;

public record SimulationCriterion(string Counter,double Probability,int Minimum,int Maximum);
public record SimulationDesign(string Id,string Kind,ulong Seed,int Size,int CandidateSize,double Mean,double Sigma,double CandidateSigma,int FamilySize,
    bool QualifiedAssumptions,IReadOnlyList<SimulationCriterion> Criteria);
public record SimulationPlan(string Version,int Experiments,string SampleAlgorithm,double Alpha,int Resamples,double Quantile,int FramesPerCluster,
    string AcceptanceMethod,double AcceptanceFamilyError,IReadOnlyList<SimulationDesign> Designs);
public record SimulationOracle(string PlanHash,string Generator,string GeneratorHash,IReadOnlyList<OracleDesign> Designs);
public record OracleDesign(string Id,IReadOnlyDictionary<string,int> Counters,IReadOnlyDictionary<string,double> First);
public record CriterionCheck(string Counter,int Observed,double Probability,int Minimum,int Maximum,bool Passed);
public record SimulationOutcome(SimulationDesign Design,int Experiments,string Assessment,IReadOnlyDictionary<string,int> Counters,
    IReadOnlyDictionary<string,double> First,IReadOnlyList<CriterionCheck> Checks,bool OracleCountsAgree,bool OracleFirstAgrees,bool Passed);
public record QualificationReport(string Protocol,string PlanHash,string OracleHash,string Runtime,string Platform,string ProductionAssemblyHash,
    string RunnerAssemblyHash,int RequiredExperiments,int Resamples,string SampleAlgorithm,bool Complete,bool Passed,IReadOnlyList<SimulationOutcome> Outcomes,
    string Meaning="Qualification is conditional on these frozen synthetic models; it does not establish real-workload independence, representativeness, or runtime promotion eligibility.");
