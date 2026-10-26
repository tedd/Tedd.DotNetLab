using System.Reflection;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public record CompatibilityBuildPins(string CoreClrVersion,string SdkVersion,IReadOnlyDictionary<string,string> Packages);
public record CompatibilityController(string OperatingSystem,string Architecture,string RuntimeVersion,string Framework,string RuntimeEngine,
    CapabilityState State,string Reason,bool BuildPinsMatchQualification);
public record CompatibilityFeature(string Name,CapabilityState State,string Scope,string Reason);
public record CompatibilityPlatform(string OperatingSystem,string Architecture,string RuntimeVersion,string Qualification,
    IReadOnlyList<CompatibilityFeature> Features);
public record CompatibilityDependency(string AssemblyName,string AssemblyVersion,string? InformationalVersion,string ModuleVersionId,
    string? BinaryHash,Availability IdentityAvailability,string? Reason);
public record CompatibilityNativeBinary(string IdentityScope,Availability Availability,string? BinaryHash,long? Length,string? Reason,string SignatureStatus);
public record CompatibilityTargetObservation(TargetIdentity Identity,IReadOnlyDictionary<string,Capability> Capabilities,string Observation,
    bool ProcessIdentityRevalidated,bool ProfilerHandshakeRevalidated);
public record CompatibilityRuntimeSelection(string PlanField,string VersionFormat,string OperatingSystem,string Architecture,
    string MinimumRuntime,string Profile,string RuntimeIdentity,string Qualification);
public record CompatibilityReport(string SchemaVersion,string QualificationRevision,CompatibilityBuildPins BuildPins,CompatibilityController Controller,
    IReadOnlyList<CompatibilityPlatform> Platforms,IReadOnlyList<string> MethodRestrictions,IReadOnlyList<CompatibilityDependency> Dependencies,
    CompatibilityNativeBinary NativeBinary,CompatibilityTargetObservation? Target,bool PermissionsGranted=false,bool FullPlatformCertification=false,
    CompatibilityRuntimeSelection? ManagedRuntimeSelection=null);

public static class CompatibilityReports
{
    public const string QualifiedRuntime="10.0.12",QualifiedSdk="10.0.401",Revision="compatibility-scope/v5";
    public static CompatibilityBuildPins BuildPins()
    {
        var attributes=typeof(CompatibilityReports).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().ToDictionary(item=>item.Key,item=>item.Value??"",StringComparer.Ordinal);
        string Pin(string key)=>attributes.TryGetValue(key,out string? value)&&value.Length>0?value:
            throw new LabException("compatibility_unavailable","Compatibility build metadata is missing.");
        const string packagePrefix="Tedd.DotNetLab.NuGet.";
        return new(Pin("Tedd.DotNetLab.CoreClr"),Pin("Tedd.DotNetLab.Sdk"),attributes.Where(item=>item.Key.StartsWith(packagePrefix,StringComparison.Ordinal))
            .OrderBy(item=>item.Key,StringComparer.Ordinal).ToDictionary(item=>item.Key[packagePrefix.Length..],item=>item.Value,StringComparer.Ordinal));
    }
    public static CompatibilityController Controller(CompatibilityBuildPins pins,string os,string architecture,string runtime,string framework,string engine)
    {
        bool build=pins.CoreClrVersion==QualifiedRuntime&&pins.SdkVersion==QualifiedSdk;
        bool compatible=architecture=="x64"&&RuntimeSupport.SupportsController(runtime,os)&&engine=="coreclr";
        return new(os,architecture,runtime,framework,engine,compatible?CapabilityState.Supported:CapabilityState.Unsupported,
            compatible?"Managed controller is compatible across CoreCLR 10.0+ (including .NET 11/12 previews & RCs); targets on CoreCLR 6.0+ are supported.":
                "Controller requires CoreCLR 10.0 or later on Windows/Linux x64; retained/offline evidence remains independently usable.",build);
    }
    public static CompatibilityReport Create(string approvedProfilerPath,TargetInfo? retainedTarget=null)
    {
        var pins=BuildPins(); string os=OperatingSystem.IsWindows()?"windows":OperatingSystem.IsLinux()?"linux":OperatingSystem.IsMacOS()?"macos":"other";
        return new(Wire.Version,Revision,pins,Controller(pins,os,RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),RuntimeSupport.Current,RuntimeInformation.FrameworkDescription,RuntimeEngine()),
            [Platform("windows"),Platform("linux"),Platform("windows",RuntimeSupport.Net10Patch),Platform("windows",RuntimeSupport.Net11),DevelopmentPlatform()],Restrictions(),
            [Dependency(typeof(Microsoft.Diagnostics.NETCore.Client.DiagnosticsClient).Assembly),Dependency(typeof(Microsoft.Diagnostics.Tracing.TraceEvent).Assembly),
                Dependency(typeof(Microsoft.Data.Sqlite.SqliteConnection).Assembly),Dependency(typeof(Iced.Intel.Decoder).Assembly)],
            NativeBinary(approvedProfilerPath),retainedTarget is null?null:new(retainedTarget.Identity,retainedTarget.Capabilities,
                "retained_catalog_observation; discovery/handshake age and process liveness are not revalidated by this report",false,false),
            ManagedRuntimeSelection:new("target_runtime_version","exact major.minor.patch with any preview/RC/build suffix",
                "windows/linux","x64","6.0.0","all-supported-profiles","exact runtime version match when pinned in experiment plan",
                "optional exact target runtime pin across CoreCLR 6.0+ including .NET 11 RC and .NET 12+ builds"));
    }
    private static string RuntimeEngine()
    {
        try
        {
            using var process=Process.GetCurrentProcess();
            if(process.Modules.Count>1024) return "unknown";
            var names=process.Modules.Cast<ProcessModule>().Select(module=>module.ModuleName).ToArray();
            int core=names.Count(name=>name.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase)||name=="libcoreclr.so"||name=="libcoreclr.dylib");
            bool mono=names.Any(name=>name.StartsWith("mono-2.0-",StringComparison.OrdinalIgnoreCase)||name.StartsWith("libmonosgen-",StringComparison.Ordinal)||name.StartsWith("libmono-2.0",StringComparison.Ordinal));
            if(core==1&&!mono) return "coreclr";
            if(core==0&&mono) return "mono";
            return "unknown";
        }
        catch(Exception error) when(error is System.ComponentModel.Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        { return "unknown"; }
    }
    private static CompatibilityPlatform Platform(string os,string version=QualifiedRuntime)
    {
        bool windows=os=="windows";
        CompatibilityFeature Feature(string name,CapabilityState state,string scope,string reason)=>new(name,state,scope,reason);
        return new(os,"x64",version,"documented_fixture_workflows; complete_platform_and_release_certification_not_established",
        [
            Feature("eventpipe",CapabilityState.Supported,"pinned runtime and provider profiles","Accessible diagnostic endpoint, independent observation grant, and profile/coverage validation required."),
            Feature("registered_scenarios",CapabilityState.Supported,"cooperative registered harness","Scenario input/version, correctness, reset, thread affinity, and quiescence remain action prerequisites."),
            Feature("native_startup",CapabilityState.Supported,"ICorProfilerInfo12; nongeneric noncollectible module/method subset","Approved startup integration and separately granted inspection/mutation required."),
            Feature("native_attach",windows?CapabilityState.Supported:CapabilityState.Unsupported,windows?"completed Windows attach with low event mask 0x40024":"none",
                windows?"Frozen approved binary/runtime identity and one-activation grant required; attach does not recover prior caller history.":"Linux attach activation is not qualified."),
            Feature("startup_rejit",CapabilityState.Supported,"disposable static Int32 bodies and frozen caller closure","At most four Int32 arguments, 4 KiB bodies, bounded existing Int32 locals, and complete scoped setup oracle."),
            Feature("attach_rejit",windows?CapabilityState.Supported:CapabilityState.Unsupported,windows?"verified no-inline static Int32 methods":"none",
                windows?"Pre-attach caller history is not collected; inlineable methods and attach probes are excluded.":"Requires an independently qualified attach adapter."),
            Feature("selected_probes",CapabilityState.Supported,"startup synchronous subset with approved managed helper","Count/elapsed aggregation, supported recursion/returns and qualified exceptional paths; no universal async timing."),
            Feature("attach_probes",CapabilityState.Unsupported,"none","Attach helper/probe installation requires independent qualification."),
            Feature("normal_build_confirmation",CapabilityState.Supported,"flat pinned managed application bundles; fresh owned processes","Independent frozen plan, build grant, input/correctness gates, and complete comparison budget; source equivalence remains a review assertion."),
            Feature("mutation_private_commit_budget",windows?CapabilityState.Supported:CapabilityState.Unsupported,windows?"sampled 128 MiB growth above first authorized preflight":"not_collected",
                windows?"Includes other target allocations; sampled boundaries do not impose an operating-system allocation cap.":"Linux private-commit accounting requires independent qualification.")
        ]);
    }
    private static CompatibilityPlatform DevelopmentPlatform()=>new("windows","x64",RuntimeSupport.Net12Development,
        "managed_CoreRun_fixture; exact_runtime_images_retained; complete_platform_certification_not_established",
        [new("registered_scenarios",CapabilityState.Supported,"quiet-validation only; leased CoreLib/CoreCLR/JIT image hashes",
            "Exact development version, epoch zero, inactive probes, and no profiler, debugger, hooks, or added dependencies required."),
         new("eventpipe",CapabilityState.Unsupported,"none","Development-runtime provider/parser qualification is separate."),
         new("native_startup",CapabilityState.Unsupported,"none","Development-runtime profiler qualification is separate."),
         new("native_attach",CapabilityState.Unsupported,"none","Development-runtime profiler qualification is separate."),
         new("startup_rejit",CapabilityState.Unsupported,"none","Development-runtime mutation is not qualified."),
         new("attach_rejit",CapabilityState.Unsupported,"none","Development-runtime mutation is not qualified."),
         new("selected_probes",CapabilityState.Unsupported,"none","Development-runtime probe qualification is separate."),
         new("normal_build_confirmation",CapabilityState.Unsupported,"none","CoreRun runtime-image treatment is outside flat entry-assembly confirmation.")]);
    private static string[] Restrictions()=>[
        "Dynamic/reflection-emitted, collectible, generic, or ambiguous module/method selections require separate qualification.",
        "Missing compiled native bodies are not permission to invoke arbitrary methods; snapshots do not prove earlier-run code.",
        "Replacement preserves method/signature/type layout and existing metadata; arbitrary foreign tokens, fields, types, methods, and unmanaged calls are excluded.",
        "Caller closure is frozen and bounded to 31 callers; unknown/incomplete coverage blocks mutation.",
        "Mutation sessions permit at most 32 candidates, 8 MiB cumulative observed native regions, and 8192 region identities; restoration does not reset budgets.",
        "Original-version restoration does not undo heap, files, network, or external effects; prior candidates require explicit reapplication.",
        "Supported probe timing is synchronous elapsed observation with stated observer footprint; unsupported async wrappers cannot establish completion duration.",
        "A loaded profiler, restored body, or quiet profile does not establish pristine runtime state or normal-build promotion eligibility.",
        "ARM64, x86, non-CoreCLR runtimes, CoreCLR prior to .NET 6.0, and general profiler stacking/unloading require separate qualification."
    ];
    private static CompatibilityDependency Dependency(Assembly assembly)
    {
        string? hash=null,reason=null; Availability availability=Availability.Available;
        try
        {
            using var file=new FileStream(assembly.Location,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(file.Length>33554432) throw new LabException("resource_limit","Dependency identity exceeds the 32 MiB read budget.");
            hash=Convert.ToHexStringLower(SHA256.HashData(file));
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or LabException or ArgumentException)
        { availability=Availability.NotCollected; reason=error is LabException lab?lab.Code:"dependency_identity_unavailable"; }
        return new(assembly.GetName().Name!,assembly.GetName().Version!.ToString(),assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
            assembly.ManifestModule.ModuleVersionId.ToString(),hash,availability,reason);
    }
    private static CompatibilityNativeBinary NativeBinary(string path)
    {
        if(!OperatingSystem.IsWindows()) return new("configured Windows loader binary",Availability.NotApplicable,null,null,"Windows loader identity is not a Linux native-binary qualification.","not_collected");
        try
        {
            using var binary=ProfilerAttachmentIdentity.OpenBinary(path);
            return new("configured Windows loader binary; hash identifies available bytes, not target memory",Availability.Available,ProfilerAttachmentIdentity.Hash(binary),binary.Length,null,"not_collected");
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or LabException)
        { return new("configured Windows loader binary",Availability.NotCollected,null,null,error is LabException lab?lab.Code:"binary_identity_unavailable","not_collected"); }
    }
}

public sealed partial class LabService
{
    public CompatibilityReport Compatibility(string? targetId=null)=>CompatibilityReports.Create(approvedProfilerPath,targetId is null?null:Target(targetId));
}
