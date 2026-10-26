using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Tedd.DotNetLab.Contracts;
using System.Text.Json.Serialization;

namespace Tedd.DotNetLab.Harness;

public record RuntimeImageIdentity(string CoreLibHash,string CoreClrHash,string JitHash,string CoreLibModuleVersionId);
public record RuntimeFootprint(string EntryAssemblyHash,IReadOnlyDictionary<string,string> AssemblyHashes,string OperatingSystem,
    string Architecture,long StopwatchFrequency,bool DebuggerAttached,bool NativeProfilerPresent,string DiagnosticsEnabled,
    string ProfilingEnabled,string StartupHooks,string AdditionalDependencies,bool OwnedProcess=false,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? RuntimeConfigurationHash=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? OperatingSystemFamily=null,
    [property:JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] RuntimeImageIdentity? RuntimeImages=null);

internal static class FootprintReader
{
    // Retain read leases for the process lifetime so identities cannot silently
    // change between handshakes. Hashing is outside measured work and occurs once.
    private static readonly List<FileStream> imageLeases=[];
    private static readonly Lazy<RuntimeImageIdentity?> runtimeImages=new(ReadRuntimeImages);
    private static RuntimeImageIdentity? ReadRuntimeImages()
    {
        if(RuntimeSupport.Supports(RuntimeSupport.Current)||!RuntimeSupport.IsSelectableManagedVersion(RuntimeSupport.Current)||
            !OperatingSystem.IsWindows()||RuntimeInformation.ProcessArchitecture!=Architecture.X64) return null;
        using var process=Process.GetCurrentProcess();
        if(process.Modules.Count>1024) throw new LabException("resource_limit","Runtime module inventory exceeds its identity budget.");
        string Module(string name)=>process.Modules.Cast<ProcessModule>().SingleOrDefault(module=>
            module.ModuleName.Equals(name,StringComparison.OrdinalIgnoreCase))?.FileName??
            throw new LabException("capability_unsupported","A loaded CoreCLR and JIT are required for development-runtime execution.");
        string HashImage(string path)
        {
            var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            try
            {
                if(stream.Length is <=0 or >134217728) throw new LabException("resource_limit","Runtime image exceeds the identity budget.");
                string hash=Convert.ToHexStringLower(SHA256.HashData(stream));
                imageLeases.Add(stream); return hash;
            }
            catch { stream.Dispose(); throw; }
        }
        return new(HashImage(typeof(object).Assembly.Location),HashImage(Module("coreclr.dll")),
            HashImage(Module("clrjit.dll")),typeof(object).Module.ModuleVersionId.ToString());
    }
    public static RuntimeFootprint Read()
    {
        string entry=Assembly.GetEntryAssembly()?.Location??"";
        if(entry.Length==0) throw new LabException("capability_unsupported","A retained entry assembly is required for build identity.");
        var files=Directory.GetFiles(Path.GetDirectoryName(entry)!,"*.dll").Order(StringComparer.Ordinal).ToArray();
        if(files.Length>64||files.Sum(p=>new FileInfo(p).Length)>134217728)
            throw new LabException("resource_limit","Application assembly inventory exceeds the footprint budget.");
        string Hash(string path)=>Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
        string Env(string name)=>Environment.GetEnvironmentVariable(name)??"";
        var hashes=files.ToDictionary(path=>Path.GetFileName(path)!,Hash,StringComparer.OrdinalIgnoreCase);
        string configuration=Path.ChangeExtension(entry,"runtimeconfig.json");
        if(File.Exists(configuration)&&new FileInfo(configuration).Length>1048576)
            throw new LabException("resource_limit","Runtime configuration exceeds its identity budget.");
        return new(Hash(entry),hashes,RuntimeInformation.OSDescription,RuntimeInformation.ProcessArchitecture.ToString(),Stopwatch.Frequency,
            Debugger.IsAttached,NativeCoordination.Present,Env("DOTNET_EnableDiagnostics"),Env("CORECLR_ENABLE_PROFILING"),
            Env("DOTNET_STARTUP_HOOKS"),Env("DOTNET_ADDITIONAL_DEPS"),RuntimeConfigurationHash:File.Exists(configuration)?Hash(configuration):null,
            OperatingSystemFamily:OperatingSystem.IsWindows()?"windows":OperatingSystem.IsLinux()?"linux":"unsupported",
            RuntimeImages:runtimeImages.Value);
    }
}
