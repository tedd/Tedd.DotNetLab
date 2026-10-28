using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;

// Read metadata as data; never load or execute the evaluated client's assembly.
public static class SourceProof
{
    public static object Inspect(string assembly, string source)
    {
        using var peStream = File.OpenRead(assembly);
        using var pe = new PEReader(peStream);
        var codeViewEntry = pe.ReadDebugDirectory().Single(e => e.Type == DebugDirectoryEntryType.CodeView);
        var codeView = pe.ReadCodeViewDebugDirectoryData(codeViewEntry);
        using var pdbStream = File.OpenRead(Path.ChangeExtension(assembly, ".pdb"));
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var reader = provider.GetMetadataReader();
        bool assemblyMatches = new Guid(reader.DebugMetadataHeader!.Id.AsSpan()[..16]) == codeView.Guid;
        var documents = reader.Documents.Select(reader.GetDocument)
            .Where(d => Path.GetFileName(reader.GetString(d.Name)) == "Candidate.cs").ToArray();
        byte[] sourceHash = SHA256.HashData(File.ReadAllBytes(source));
        bool sourceMatches = documents.Length == 1 && reader.GetBlobBytes(documents[0].Hash).SequenceEqual(sourceHash);
        return new { assembly_pdb_matches = assemblyMatches, source_matches = sourceMatches,
            entry_assembly_hash = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(assembly))) };
    }
}
