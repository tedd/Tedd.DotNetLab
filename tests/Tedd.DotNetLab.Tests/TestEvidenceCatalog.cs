using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using Tedd.DotNetLab.Contracts;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public record DeclaredTestEvidence(string Method,IReadOnlyList<string> Cases,IReadOnlyList<string> Requirements,string Milestone,string Level);
public record TestDeclarationCatalog(string SchemaVersion,string AssemblyPath,string AssemblyHash,IReadOnlyList<DeclaredTestEvidence> Tests,
    string Meaning="Declared test relationships; these tags alone do not prove requirement completion or runtime qualification.");

public static class TestEvidenceCatalog
{
    public static TestDeclarationCatalog Describe()
    {
        var assembly=typeof(TestEvidenceCatalog).Assembly; var tests=new List<DeclaredTestEvidence>();
        foreach(var type in assembly.GetTypes().OrderBy(t=>t.FullName,StringComparer.Ordinal))
        foreach(var method in type.GetMethods(BindingFlags.Public|BindingFlags.Instance|BindingFlags.Static|BindingFlags.DeclaredOnly).OrderBy(m=>m.Name,StringComparer.Ordinal))
        {
            if(method.GetCustomAttribute<FactAttribute>() is null) continue;
            var tag=method.GetCustomAttribute<CaseAttribute>()??throw new InvalidDataException("Executable test lacks requirement metadata: "+type.FullName+"."+method.Name);
            var traits=CaseTraitDiscoverer.Tags(tag.Id,tag.Requirement,tag.Milestone,tag.Level).ToArray();
            tests.Add(new(type.FullName+"."+method.Name,traits.Where(t=>t.Key=="Case").Select(t=>t.Value).ToArray(),
                traits.Where(t=>t.Key=="Requirement").Select(t=>t.Value).ToArray(),tag.Milestone,tag.Level));
        }
        return new("test-declarations/v1",assembly.Location,Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly.Location))),tests);
    }
    [ModuleInitializer]
    public static void EmitWhenRequested()
    {
        string? requested=Environment.GetEnvironmentVariable("DOTNETLAB_TEST_METADATA_PATH"); if(string.IsNullOrWhiteSpace(requested)) return;
        string path=Path.GetFullPath(requested);
        string taskPrefix=OperatingSystem.IsWindows()?@"D:\Workspaces\AI\":"/mnt/d/Workspaces/AI/";
        if(!path.StartsWith(taskPrefix,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal)||Path.GetExtension(path)!=".json")
            throw new InvalidOperationException("Test declaration output must be a JSON file in the designated AI workspace.");
        string directory=Path.GetDirectoryName(path)!;
        for(var ancestor=new DirectoryInfo(directory);ancestor is not null;ancestor=ancestor.Parent)
            if(ancestor.Exists&&(ancestor.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidOperationException("Test metadata output cannot traverse reparse points.");
        Directory.CreateDirectory(directory); string incoming=path+"."+Guid.NewGuid().ToString("N")+".partial";
        try
        {
            using(var file=new FileStream(incoming,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            { byte[] bytes=System.Text.Encoding.UTF8.GetBytes(Wire.Serialize(Describe())); file.Write(bytes); file.Flush(true); }
            File.Move(incoming,path,true);
        }
        finally { if(File.Exists(incoming)) File.Delete(incoming); }
    }
}
