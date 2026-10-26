using System.Security.Cryptography;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Artifacts;

public sealed partial class ArtifactStore
{
    public string Root { get; }
    private readonly Func<string,bool>? expired;
    private readonly Action<SealStage>? sealStage;
    public ArtifactStore(string workspace,Func<string,bool>? expired=null,Action<SealStage>? sealStage=null)
    {
        this.expired=expired;
        this.sealStage=sealStage;
        Root = Path.GetFullPath(workspace);
        VerifyAncestors(Root);
        Directory.CreateDirectory(Path.Combine(Root, "incoming"));
        Directory.CreateDirectory(Path.Combine(Root, "artifacts", "sha256"));
        VerifyAncestors(Path.Combine(Root,"incoming")); VerifyAncestors(Path.Combine(Root,"artifacts","sha256"));
    }
    public string CreateIncoming(string operationId)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(operationId, @"\A[a-z0-9-]{1,100}\z"))
            throw new LabException("invalid_path", "Invalid incoming operation identity.");
        string dir = Path.Combine(Root, "incoming", operationId);
        VerifyAncestors(dir);
        Directory.CreateDirectory(dir);
        VerifyAncestors(dir);
        return Path.Combine(dir, Guid.NewGuid().ToString("N") + ".partial");
    }
    public async Task<Artifact> SealAsync(string incoming, string mediaType, string producer, bool complete,
        long maximumBytes, CancellationToken cancellation = default)
    {
        string path = Path.GetFullPath(incoming);
        string prefix = Path.Combine(Root, "incoming") + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !path.EndsWith(".partial", StringComparison.Ordinal))
            throw new LabException("invalid_path", "Artifact must originate in this workspace.");
        VerifyAncestors(Path.GetDirectoryName(path)!);
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new LabException("invalid_path", "Reparse points are forbidden.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        if (file.Length > maximumBytes) throw new LabException("resource_limit", "Artifact exceeds its byte budget.");
        sealStage?.Invoke(SealStage.BeforeFlush);
        file.Flush(true);
        string hash = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation));
        if(expired?.Invoke("art-"+hash)==true) throw new LabException("artifact_expired","This content has a committed deletion tombstone.");
        long length = file.Length;
        await file.DisposeAsync();
        var artifact=new Artifact("art-"+hash,hash,length,mediaType,Wire.Version,producer,complete);
        await WriteSealReceiptAsync(path,artifact,cancellation);
        sealStage?.Invoke(SealStage.ReceiptFlushed);
        string destination = PathForHash(hash);
        VerifyAncestors(Path.GetDirectoryName(destination)!);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        VerifyAncestors(Path.GetDirectoryName(destination)!);
        try { File.Move(path, destination, false); }
        catch (IOException) when (File.Exists(destination))
        {
            await VerifyAsync(hash, length, cancellation);
            File.Delete(path);
        }
        sealStage?.Invoke(SealStage.Moved);
        return artifact;
    }
    private string PathForHash(string hash)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(hash, @"\A[0-9a-f]{64}\z"))
            throw new LabException("invalid_path", "Invalid content hash.");
        return Path.Combine(Root, "artifacts", "sha256", hash[..2], hash);
    }
    public async Task VerifyAsync(string hash, long length, CancellationToken cancellation = default)
    {
        string path = PathForHash(hash);
        if(expired?.Invoke("art-"+hash)==true) throw new LabException("artifact_expired","Raw artifact deletion has been committed.");
        VerifyAncestors(Path.GetDirectoryName(path)!);
        if (!File.Exists(path)) throw new LabException("artifact_expired", "Raw artifact is no longer retained.");
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new LabException("invalid_path","Artifact reparse points are forbidden.");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != length || Convert.ToHexStringLower(await SHA256.HashDataAsync(file, cancellation)) != hash)
            throw new LabException("artifact_corrupt", "Artifact length or hash differs from its manifest.");
    }
    public async Task<string> VerifiedPathAsync(Artifact artifact, CancellationToken cancellation = default)
    {
        await VerifyAsync(artifact.Hash, artifact.Length, cancellation);
        return PathForHash(artifact.Hash);
    }
    public async Task<Artifact> WriteJsonAsync<T>(string operationId, T value, string producer, long limit = 16777216,CancellationToken cancellation=default)
    {
        string incoming = CreateIncoming(operationId);
        await File.WriteAllTextAsync(incoming, Wire.Serialize(value),cancellation);
        return await SealAsync(incoming, "application/json", producer, true, limit,cancellation);
    }
    public void Delete(Artifact artifact)
    {
        string path=PathForHash(artifact.Hash); VerifyAncestors(Path.GetDirectoryName(path)!);
        if(File.Exists(path)&&(File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new LabException("invalid_path","Artifact reparse points are forbidden.");
        File.Delete(path);
    }
    private static void VerifyAncestors(string path)
    {
        for(var directory=new DirectoryInfo(Path.GetFullPath(path));directory is not null;directory=directory.Parent)
            if(directory.Exists&&(directory.Attributes&FileAttributes.ReparsePoint)!=0)
                throw new LabException("invalid_path","Artifact paths cannot traverse reparse points.");
    }
}
