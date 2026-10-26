using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Mcp;

// CLI `call` retains the complete wire contract. Connected agents receive only
// the experiment/measurement surface, including during schema generation.
public static class McpCatalog
{
    public static IReadOnlySet<Type> CliOnly { get; }=new HashSet<Type>
    {
        typeof(PrepareExport),typeof(PrepareBundleImport),typeof(PrepareDeletion),typeof(PrepareRetention),typeof(PinEvidence),typeof(PrepareReferenceRelease),
        typeof(StartExport),typeof(StartBundleImport),typeof(StartImportedCaptureAnalysis),typeof(StartDeletion),typeof(StartArtifactRecovery),
        typeof(StartCatalogBackup),typeof(CheckpointCatalog),typeof(StartReferenceRelease),
        typeof(QueryExport),typeof(QueryBundleImport),typeof(QueryImportedMembers),typeof(QueryRetention),typeof(QueryArtifactRecovery),
        typeof(QueryCatalogBackup),typeof(QueryArtifactReferences)
    };
    public static JsonSerializerOptions Json { get; }=Create();
    private static JsonSerializerOptions Create()
    {
        var resolver=new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info=>
        {
            if(info.PolymorphismOptions is not {} polymorphism) return;
            for(int i=polymorphism.DerivedTypes.Count-1;i>=0;i--)
                if(CliOnly.Contains(polymorphism.DerivedTypes[i].DerivedType)) polymorphism.DerivedTypes.RemoveAt(i);
        });
        var options=new JsonSerializerOptions(Wire.Json) { TypeInfoResolver=resolver }; options.MakeReadOnly(); return options;
    }
}
