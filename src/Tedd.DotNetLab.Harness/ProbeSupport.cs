using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Harness;

public record ProbeHelperDescriptor(string Version,string ModuleHash,string ModuleMvid,bool Active,string? ActivePlanHash=null,string? MethodScope=null,bool Timing=false);
public record ProbeControl(string Action,long ExpectedEpoch,Grant Grant,string PlanHash,string MethodScope,
    int Methods=1,bool Timing=false,int Threads=32,int MaximumNesting=64);
public interface IHarnessProbeSupport
{
    ProbeHelperDescriptor Describe();
    object Control(TargetIdentity target,ProbeControl request);
}
