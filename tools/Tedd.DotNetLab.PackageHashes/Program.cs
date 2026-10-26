using System.Security.Cryptography;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using NuGet.Packaging;

// Read-only build worker. It never restores packages, extracts archives, probes
// certificate servers, starts application code, or accesses a database.
try
{
if(args.Length!=1) throw new ArgumentException("Expected one locked-assets path.");
string assetsPath=Regular(args[0]);
if(new FileInfo(assetsPath).Length>8*1024*1024) throw new InvalidDataException("Assets exceed the input budget.");
using var assets=JsonDocument.Parse(File.ReadAllBytes(assetsPath));
string[] roots=assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(p=>p.Name).ToArray();
var libraries=assets.RootElement.GetProperty("libraries").EnumerateObject().Where(p=>p.Value.GetProperty("type").GetString()=="package").ToArray();
if(roots.Length is <1 or >8||libraries.Length is <1 or >256) throw new InvalidDataException("Dependency inventory exceeds its bound.");
var results=new SortedDictionary<string,object>(StringComparer.Ordinal);
using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(60));
foreach(var library in libraries)
{
    timeout.Token.ThrowIfCancellationRequested();
    string[] parts=library.Name.Split('/');
    if(parts.Length!=2||parts.Any(part=>!Regex.IsMatch(part,@"\A[A-Za-z0-9_.+-]+\z",RegexOptions.CultureInvariant)))
        throw new InvalidDataException("Invalid dependency identity.");
    string relative=library.Value.GetProperty("path").GetString()!;
    if(!StringComparer.OrdinalIgnoreCase.Equals(relative,parts[0]+"/"+parts[1])) throw new InvalidDataException("Invalid dependency cache path.");
    string filename=parts[0].ToLowerInvariant()+"."+parts[1].ToLowerInvariant()+".nupkg";
    string path=roots.Select(root=>Path.Combine(root,relative,filename)).First(File.Exists);
    path=Regular(path);
    using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
    if(stream.Length>64*1024*1024) throw new InvalidDataException("Package exceeds the archive budget.");
    using(var archive=new ZipArchive(stream,ZipArchiveMode.Read,leaveOpen:true))
    {
        if(archive.Entries.Count>4096||archive.Entries.Sum(e=>e.Length)>512L*1024*1024||archive.Entries.Any(e=>e.Length>64L*1024*1024)||
            archive.Entries.Where(e=>e.FullName==".signature.p7s").Any(e=>e.Length>1024*1024))
            throw new InvalidDataException("Package expansion exceeds its bounded budget.");
    }
    stream.Position=0;
    string archiveHash=Convert.ToBase64String(SHA512.HashData(stream)); stream.Position=0;
    using var reader=new PackageArchiveReader(stream,leaveStreamOpen:true);
    bool signed=await reader.IsSignedAsync(timeout.Token);
    if(signed)
    {
        var signature=await reader.GetPrimarySignatureAsync(timeout.Token)??throw new InvalidDataException("Missing primary package signature.");
        await reader.ValidateIntegrityAsync(signature.SignatureContent,timeout.Token);
    }
    string contentHash=reader.GetContentHash(timeout.Token);
    if(contentHash!=library.Value.GetProperty("sha512").GetString()) throw new InvalidDataException("Package content differs from restored assets.");
    results.Add(library.Name,new { content_sha512=contentHash,archive_sha512=archiveHash,signed,integrity_verified=true });
}
Console.WriteLine(JsonSerializer.Serialize(results));
return 0;
}
catch(Exception error)
{
    Console.Error.WriteLine("Package integrity validation failed: "+error.GetType().Name);
    return 1;
}

static string Regular(string value)
{
    string path=Path.GetFullPath(value);
    for(FileSystemInfo? item=new FileInfo(path);item is not null;item=item switch {FileInfo file=>file.Directory,DirectoryInfo directory=>directory.Parent,_=>null})
        if(item.Exists&&(item.Attributes&FileAttributes.ReparsePoint)!=0) throw new InvalidDataException("Package paths cannot traverse reparse points.");
    return path;
}
