using System.Text;
using System.Text.RegularExpressions;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Artifacts;

public enum SealStage { BeforeFlush,ReceiptFlushed,Moved }
public record SealReceipt(string Version,Artifact Artifact,string ManifestHash);
public record RecoveryFile(string RelativePath,string Kind,long? Length=null,string? Hash=null);

public sealed partial class ArtifactStore
{
    private static async Task WriteSealReceiptAsync(string incoming,Artifact artifact,CancellationToken cancellation)
    {
        var receipt=new SealReceipt("seal-receipt/v1",artifact,Wire.Hash(artifact));
        byte[] bytes=Encoding.UTF8.GetBytes(Wire.Serialize(receipt));
        if(bytes.Length>16384) throw new LabException("resource_limit","Seal receipt metadata exceeds 16 KiB.");
        await using var output=new FileStream(incoming+".seal.json",FileMode.CreateNew,FileAccess.Write,FileShare.None);
        await output.WriteAsync(bytes,cancellation); output.Flush(true);
    }
    public IEnumerable<RecoveryFile> RecoveryFiles(int maximumEntries)
    {
        if(maximumEntries is <1 or >10000) throw new LabException("resource_limit","Recovery entry budget must be 1..10000.");
        var directories=new Stack<(string Path,string Kind,int Depth)>();
        directories.Push((Path.Combine(Root,"artifacts","sha256"),"sealed",0));
        directories.Push((Path.Combine(Root,"incoming"),"incoming",0));
        int visited=0;
        while(directories.TryPop(out var directory))
        {
            VerifyAncestors(directory.Path);
            foreach(string path in Directory.EnumerateFileSystemEntries(directory.Path))
            {
                if(visited++==maximumEntries) { yield return new("","scan_truncated"); yield break; }
                string relative=Path.GetRelativePath(Root,path),name=Path.GetFileName(path); var attributes=File.GetAttributes(path);
                if((attributes&FileAttributes.ReparsePoint)!=0) { yield return new(relative,"unsafe_path"); continue; }
                if((attributes&FileAttributes.Directory)!=0)
                {
                    bool permitted=directory.Depth==0&&Regex.IsMatch(name,directory.Kind=="incoming"?@"\A[a-z0-9-]{1,100}\z":@"\A[0-9a-f]{2}\z");
                    yield return new(relative,permitted?"directory":"unexpected_path");
                    if(permitted) directories.Push((path,directory.Kind,1));
                    continue;
                }
                string kind="unexpected_path"; string? hash=null;
                if(directory.Depth==1&&directory.Kind=="incoming")
                {
                    if(Regex.IsMatch(name,@"\A[0-9a-f]{32}\.partial\z")) kind="partial_write";
                    if(Regex.IsMatch(name,@"\A[0-9a-f]{32}\.partial\.seal\.json\z")) kind="seal_receipt";
                }
                if(directory.Depth==1&&directory.Kind=="sealed"&&Regex.IsMatch(name,@"\A[0-9a-f]{64}\z")&&name[..2]==Path.GetFileName(directory.Path))
                { kind="sealed_file"; hash=name; }
                yield return new(relative,kind,new FileInfo(path).Length,hash);
            }
        }
    }
    public async Task<SealReceipt> ReadSealReceiptAsync(RecoveryFile entry,CancellationToken cancellation)
    {
        if(entry.Kind!="seal_receipt"||entry.Length is null or <1 or >16384)
            throw new LabException("artifact_corrupt","Seal receipt has an invalid type or length.");
        string path=ReceiptPath(entry);
        await using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>16384) throw new LabException("resource_limit","Seal receipt exceeds 16 KiB.");
        byte[] bytes=new byte[checked((int)input.Length)]; await input.ReadExactlyAsync(bytes,cancellation);
        var receipt=Wire.Deserialize<SealReceipt>(Encoding.UTF8.GetString(bytes));
        if(receipt.Artifact is null) throw new LabException("artifact_corrupt","Seal receipt has no artifact manifest.");
        var artifact=receipt.Artifact;
        if(receipt.Version!="seal-receipt/v1"||receipt.ManifestHash!=Wire.Hash(artifact)||artifact.Id!="art-"+artifact.Hash||
            !Regex.IsMatch(artifact.Hash,@"\A[0-9a-f]{64}\z")||artifact.Length<0||artifact.SchemaVersion!=Wire.Version)
            throw new LabException("artifact_corrupt","Seal receipt manifest, hash, or schema is invalid.");
        return receipt;
    }
    private string ReceiptPath(RecoveryFile entry)
    {
        if(entry.Kind!="seal_receipt"||!Regex.IsMatch(entry.RelativePath,@"\Aincoming[\\/][a-z0-9-]{1,100}[\\/][0-9a-f]{32}\.partial\.seal\.json\z"))
            throw new LabException("invalid_path","Receipt paths must have the generated incoming layout.");
        string path=Path.GetFullPath(Path.Combine(Root,entry.RelativePath));
        string incomingRoot=Path.Combine(Root,"incoming")+Path.DirectorySeparatorChar;
        if(!path.StartsWith(incomingRoot,StringComparison.OrdinalIgnoreCase)) throw new LabException("invalid_path","Receipt is outside incoming storage.");
        VerifyAncestors(Path.GetDirectoryName(path)!);
        if((File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0) throw new LabException("invalid_path","Receipt reparse points are forbidden.");
        return path;
    }
    public bool ReceiptHasPartial(RecoveryFile entry)
    {
        string receipt=ReceiptPath(entry);
        return File.Exists(receipt[..^".seal.json".Length]);
    }
}
