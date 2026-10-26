using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Application;

public sealed partial class LabService
{
    private T Record<T>(string id,string kind)
    {
        if(Catalog.Get(id).Kind!=kind) throw new LabException("invalid_request","Record kind differs from the requested operation.");
        return Catalog.Get<T>(id);
    }
    private static bool FlatName(string name)=>Regex.IsMatch(name,@"\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z")&&
        !name.Contains("..",StringComparison.Ordinal)&&!new[]{"CON","PRN","AUX","NUL","COM1","LPT1"}.Contains(name.Split('.')[0],StringComparer.OrdinalIgnoreCase);
    private static string BuildHash(string entry,IReadOnlyList<BuildFile> files,string runtime)=>Wire.Hash(new { entry,files,runtime,architecture="X64" });

    public async Task<NormalBuild> RegisterNormalBuildAsync(string entryAssembly,IReadOnlyList<BuildFile> files)
    {
        if(!FlatName(entryAssembly)||!entryAssembly.EndsWith(".dll",StringComparison.Ordinal)||files.Count is <3 or >64||
            files.Select(f=>f.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=files.Count)
            throw new LabException("invalid_build","Build requires a flat, bounded file manifest and a managed entry assembly.");
        var canonical=files.OrderBy(f=>f.Name,StringComparer.Ordinal).ToArray();
        var configFile=canonical.SingleOrDefault(file=>file.Name==Path.GetFileNameWithoutExtension(entryAssembly)+".runtimeconfig.json")
            ??throw new LabException("invalid_build","Entry assembly requires a runtime configuration.");
        var configArtifact=Record<Artifact>(configFile.ArtifactId,"artifact");
        if(configArtifact.Length>1048576) throw new LabException("resource_limit","Runtime configuration exceeds its identity budget.");
        string runtime;
        try
        {
            using var config=JsonDocument.Parse(await File.ReadAllTextAsync(await Artifacts.VerifiedPathAsync(configArtifact,CancellationToken.None)),new JsonDocumentOptions { MaxDepth=32 });
            runtime=config.RootElement.GetProperty("runtimeOptions").GetProperty("framework").GetProperty("version").GetString()
                ??throw new LabException("invalid_build","Build runtime version is missing.");
        }
        catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new LabException("invalid_build","Build runtime configuration is malformed."); }
        var build=new NormalBuild(Wire.Id("build"),entryAssembly,canonical,BuildHash(entryAssembly,canonical,runtime),runtime,"X64");
        await ValidateBuildAsync(build,CancellationToken.None);
        Catalog.Put(build.Id,"normal_build",build);
        foreach(var file in canonical) Catalog.Pin(file.ArtifactId,"normal_build");
        return build;
    }

    private async Task<Dictionary<string,string>> ValidateBuildAsync(NormalBuild build,CancellationToken cancellation)
    {
        try { return await ValidateBuildCoreAsync(build,cancellation); }
        catch(Exception e) when(e is JsonException or KeyNotFoundException or InvalidOperationException or BadImageFormatException)
        { throw new LabException("invalid_build","Managed build metadata or host manifests are malformed."); }
    }
    private async Task<Dictionary<string,string>> ValidateBuildCoreAsync(NormalBuild build,CancellationToken cancellation)
    {
        if(!RuntimeSupport.SupportsNormalBuild(build.RuntimeVersion)||build.Architecture!="X64"||build.ContentHash!=BuildHash(build.EntryAssembly,build.Files,build.RuntimeVersion))
            throw new LabException("invalid_build","Build identity differs from its immutable manifest.");
        var paths=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var assemblies=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        var references=new List<string>();
        long total=0;
        foreach(var file in build.Files)
        {
            if(!FlatName(file.Name)||!paths.TryAdd(file.Name,"")) throw new LabException("invalid_build","Invalid or duplicate file name.");
            var artifact=Record<Artifact>(file.ArtifactId,"artifact");
            total=checked(total+artifact.Length);
            if(!artifact.Complete||artifact.Length<=0||total>134217728) throw new LabException("resource_limit","Build must contain complete artifacts within 128 MiB.");
            string path=await Artifacts.VerifiedPathAsync(artifact,cancellation);
            paths[file.Name]=path;
            if(file.Name.EndsWith(".dll",StringComparison.Ordinal))
            {
                if(artifact.MediaType!="application/vnd.microsoft.portable-executable") throw new LabException("invalid_build","Managed build files require PE artifacts.");
                using var stream=File.OpenRead(path); using var pe=new PEReader(stream);
                if(!pe.HasMetadata||pe.PEHeaders.CorHeader is not { } header||header.ManagedNativeHeaderDirectory.Size!=0||
                    (header.Flags&CorFlags.ILOnly)==0)
                    throw new LabException("invalid_build","Only normal managed IL assemblies are qualified.");
                var metadata=pe.GetMetadataReader();
                if(!metadata.IsAssembly) throw new LabException("invalid_build","Netmodules are not supported.");
                string name=metadata.GetString(metadata.GetAssemblyDefinition().Name);
                if(file.Name!=name+".dll"||!assemblies.TryAdd(name,file.Name)) throw new LabException("invalid_build","Assembly identity and flat file name differ.");
                if(file.Name==build.EntryAssembly&&header.EntryPointTokenOrRelativeVirtualAddress==0)
                    throw new LabException("invalid_build","Entry assembly has no managed entry point.");
                foreach(var reference in metadata.AssemblyReferences) references.Add(metadata.GetString(metadata.GetAssemblyReference(reference).Name));
            }
            else if(!file.Name.EndsWith(".json",StringComparison.Ordinal)||artifact.MediaType!="application/json")
                throw new LabException("invalid_build","Build supports managed DLLs and its two JSON host manifests only.");
        }
        string stem=Path.GetFileNameWithoutExtension(build.EntryAssembly);
        string configName=stem+".runtimeconfig.json",depsName=stem+".deps.json";
        if(!paths.ContainsKey(build.EntryAssembly)||!paths.ContainsKey(configName)||!paths.ContainsKey(depsName)||
            paths.Keys.Any(n=>n.EndsWith(".json",StringComparison.Ordinal)&&n!=configName&&n!=depsName))
            throw new LabException("invalid_build","Entry assembly requires exactly its runtimeconfig and dependency manifests.");
        using var config=JsonDocument.Parse(await File.ReadAllTextAsync(paths[configName],cancellation),new JsonDocumentOptions { MaxDepth=32 });
        var options=config.RootElement.GetProperty("runtimeOptions");
        string rollForward=options.TryGetProperty("rollForward",out var rollForwardProp)?rollForwardProp.GetString()??"":"Minor";
        string[] allowedRollForward=["Disable","Patch","LatestPatch","Minor","LatestMinor","Major","LatestMajor"];
        if(options.GetProperty("tfm").GetString()!=RuntimeSupport.Framework(build.RuntimeVersion)||
            !allowedRollForward.Contains(rollForward,StringComparer.Ordinal)||
            options.GetProperty("framework").GetProperty("name").GetString()!="Microsoft.NETCore.App"||
            options.GetProperty("framework").GetProperty("version").GetString()!=build.RuntimeVersion)
            throw new LabException("invalid_build","Runtime must match a supported Microsoft.NETCore.App version and roll-forward policy.");
        string runtimeDirectory=RuntimeSupport.RuntimeDirectory(build.RuntimeVersion,rollForward);
        foreach(string name in references)
            if(!assemblies.ContainsKey(name)&&(!FlatName(name)||!File.Exists(Path.Combine(runtimeDirectory,name+".dll"))))
                throw new LabException("invalid_build","Managed dependency closure is incomplete.");
        if(!assemblies.ContainsKey("Tedd.DotNetLab.Harness")||!assemblies.ContainsKey("Tedd.DotNetLab.Contracts")||!assemblies.ContainsKey("Tedd.DotNetLab.Transport.Local"))
            throw new LabException("invalid_build","Build requires the cooperative harness dependency closure.");
        string[] allowedOptions=["tfm","framework","rollForward","configProperties"];
        if(options.EnumerateObject().Any(p=>!allowedOptions.Contains(p.Name))) throw new LabException("invalid_build","Additional host configuration is not qualified.");
        string[] allowedProperties=["System.GC.Server","System.GC.Concurrent","System.Runtime.TieredCompilation","System.Runtime.TieredCompilation.QuickJit",
            "System.Runtime.TieredCompilation.QuickJitForLoops","System.Runtime.TieredPGO","System.Reflection.Metadata.MetadataUpdater.IsSupported",
            "System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization"];
        if(options.TryGetProperty("configProperties",out var properties)&&properties.EnumerateObject().Any(p=>!allowedProperties.Contains(p.Name)||p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)))
            throw new LabException("invalid_build","Runtime properties must use the qualified boolean policy.");
        using var deps=JsonDocument.Parse(await File.ReadAllTextAsync(paths[depsName],cancellation),new JsonDocumentOptions { MaxDepth=32 });
        foreach(var target in deps.RootElement.GetProperty("targets").EnumerateObject())
            foreach(var library in target.Value.EnumerateObject())
            {
                if(library.Value.TryGetProperty("native",out _)||library.Value.TryGetProperty("runtimeTargets",out _)||library.Value.TryGetProperty("resources",out _))
                    throw new LabException("invalid_build","Native, RID-specific, and satellite dependencies are not qualified.");
                if(library.Value.TryGetProperty("runtime",out var runtime))
                    foreach(var asset in runtime.EnumerateObject())
                        if(!FlatName(asset.Name)||!paths.ContainsKey(asset.Name)||!asset.Name.EndsWith(".dll",StringComparison.Ordinal))
                            throw new LabException("invalid_build","Dependency assets must resolve exclusively to registered managed files.");
            }
        return paths;
    }

    private static void VerifyDirectoryAncestors(string path)
    {
        for(var directory=new DirectoryInfo(path);directory is not null;directory=directory.Parent)
            if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new LabException("invalid_path","Launch directories cannot traverse reparse points.");
    }
    private async Task<string> StageBuildAsync(NormalBuild build,string sessionId,CancellationToken cancellation)
    {
        if(!OwnedScratchPath(Catalog.Root))
            throw new LabException("invalid_path","Owned process scratch files require a non-root fully qualified workspace directory.");
        string directory=Path.Combine(Catalog.Root,"launches",sessionId);
        VerifyDirectoryAncestors(directory);
        var paths=await ValidateBuildAsync(build,cancellation);
        long bytes=build.Files.Sum(file=>Record<Artifact>(file.ArtifactId,"artifact").Length);
        if(!DiskSpace.Fits(Catalog.Root,bytes,Limits.DiskReserveBytes))
            throw new LabException("resource_limit","Build staging would violate the disk reserve.");
        if(Directory.Exists(directory)) throw new LabException("invalid_path","Build staging requires a fresh owned session directory.");
        bool owned=false;
        try
        {
            Directory.CreateDirectory(directory); owned=true; VerifyDirectoryAncestors(directory);
            foreach(var file in build.Files)
            {
                string stagedPath=Path.Combine(directory,file.Name);
                await using(var source=new FileStream(paths[file.Name],FileMode.Open,FileAccess.Read,FileShare.Read))
                await using(var destination=new FileStream(stagedPath,FileMode.CreateNew,FileAccess.Write,FileShare.None))
                { await source.CopyToAsync(destination,cancellation); destination.Flush(true); }
                normalBuildFileStaged?.Invoke(stagedPath);
            }
            // Rehash the staged copy immediately before launch; sealing evidence never trusts a path alone.
            foreach(var file in build.Files)
            {
                var expected=Record<Artifact>(file.ArtifactId,"artifact");
                await using var staged=File.OpenRead(Path.Combine(directory,file.Name));
                if(staged.Length!=expected.Length||Convert.ToHexStringLower(await System.Security.Cryptography.SHA256.HashDataAsync(staged,cancellation))!=expected.Hash)
                    throw new LabException("artifact_corrupt","Staged build differs from its sealed manifest.");
            }
            return directory;
        }
        catch(Exception error) when(error is not OutOfMemoryException)
        {
            if(owned) await RemoveOwnedLaunchDirectoryAsync(directory);
            throw;
        }
    }
    private async Task RemoveOwnedLaunchDirectoryAsync(string directory)
    {
        string full=Path.GetFullPath(directory);
        string prefix=Path.Combine(Catalog.Root,"launches")+Path.DirectorySeparatorChar;
        if(!OwnedScratchPath(full)||
            !full.StartsWith(prefix,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||!Regex.IsMatch(Path.GetFileName(full),@"\Anormal-session-[0-9a-f]{32}\z"))
            throw new LabException("invalid_path","Cleanup is restricted to this task's owned launch directory.");
        VerifyDirectoryAncestors(full);
        await Tedd.DotNetLab.Artifacts.OwnedDirectoryCleanup.DeleteAsync(Catalog.Root,full);
    }
    private static bool OwnedScratchPath(string path)
    {
        if(!(OperatingSystem.IsWindows()||OperatingSystem.IsLinux())||!Path.IsPathFullyQualified(path)) return false;
        string trimmed=Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? root=Path.GetPathRoot(trimmed);
        return !string.IsNullOrEmpty(root)&&
            !string.Equals(trimmed,Path.TrimEndingDirectorySeparator(root),
                OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)&&
            Directory.GetParent(trimmed) is not null;
    }
}
