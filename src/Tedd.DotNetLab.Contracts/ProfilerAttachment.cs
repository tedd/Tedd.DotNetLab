using System.Diagnostics;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Tedd.DotNetLab.Contracts;

public record ProfilerAttachmentInvocation(string SchemaVersion,TargetIdentity Target,string Workspace,string BinaryPath,
    string BinaryHash,long BinaryLength,string RuntimeHash);
public record ProfilerAttachmentWorkerReply(string SchemaVersion,string Status,string? Code);
public record ProfilerAttachmentReconciliation(string Id,string SourceOperationId,string PlanId,string PlanHash,TargetIdentity Target,
    string State,DateTimeOffset ObservedAt);

public static class ProfilerAttachmentIdentity
{
    public const string ProfilerId="63ea6b80-5694-4b6b-8d4a-754cb224ae37";
    public static void RequireHost()
    {
        if(!OperatingSystem.IsWindows()||!RuntimeSupport.HostSupported)
            throw new LabException("capability_unsupported","Profiler attachment requires the pinned Windows x64 controller runtime.");
    }
    public static FileStream OpenBinary(string path)
    {
        if(!Path.IsPathFullyQualified(path)||!Path.GetFileName(path).Equals("Tedd.DotNetLab.Profiler.dll",StringComparison.OrdinalIgnoreCase))
            throw new LabException("invalid_profiler_binary","The configured profiler must be an absolute Tedd.DotNetLab.Profiler.dll path.");
        for(string? current=Path.GetFullPath(path);current is not null;current=Path.GetDirectoryName(current))
            if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                throw new LabException("invalid_profiler_binary","Profiler identity cannot traverse reparse points.");
        if(!File.Exists(path)) throw new LabException("capability_unsupported","The configured profiler binary is unavailable.");
        var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        try
        {
            if(stream.Length is <1 or >16777216) throw new LabException("resource_limit","Profiler binary exceeds the 16 MiB policy.");
            using var image=new PEReader(stream,PEStreamOptions.LeaveOpen);
            if(image.PEHeaders.CoffHeader.Machine!=Machine.Amd64||(image.PEHeaders.CoffHeader.Characteristics&Characteristics.Dll)==0||image.HasMetadata)
                throw new LabException("invalid_profiler_binary","Profiler requires a native Windows AMD64 DLL.");
            stream.Position=0; return stream;
        }
        catch(BadImageFormatException)
        { stream.Dispose(); throw new LabException("invalid_profiler_binary","Configured profiler is not a valid native Windows image."); }
        catch { stream.Dispose(); throw; }
    }
    public static string Hash(Stream stream)
    { stream.Position=0; string hash=Convert.ToHexStringLower(SHA256.HashData(stream)); stream.Position=0; return hash; }
    public static string VerifyRuntime(TargetIdentity target,bool requireUnattached)
        =>VerifyRuntime(target,requireUnattached,out _);
    public static string VerifyRuntime(TargetIdentity target,bool requireUnattached,out string runtimeVersion)
    {
        RequireHost();
        try
        {
            using var process=Process.GetProcessById(target.ProcessId);
            if(process.HasExited||ProcessIdentity.StartTimeUtcTicks(process)!=target.StartTimeUtcTicks)
                throw new LabException("target_identity_changed","Profiler attachment process identity differs.");
            if(process.Modules.Count>1024) throw new LabException("resource_limit","Target module inventory exceeds policy.");
            var modules=process.Modules.Cast<ProcessModule>().ToArray();
            if(requireUnattached&&modules.Any(m=>m.ModuleName.Equals("Tedd.DotNetLab.Profiler.dll",StringComparison.OrdinalIgnoreCase)))
                throw new LabException("profiler_conflict","The target already has the project profiler loaded.");
            var runtime=modules.Where(m=>m.ModuleName.Equals("coreclr.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
            if(runtime.Length!=1) throw new LabException("capability_unsupported","Exactly one CoreCLR runtime image is required.");
            string loadedPath=runtime[0].FileName;
            for(string? current=Path.GetFullPath(loadedPath);current is not null;current=Path.GetDirectoryName(current))
                if((File.Exists(current)||Directory.Exists(current))&&(File.GetAttributes(current)&FileAttributes.ReparsePoint)!=0)
                    throw new LabException("capability_unsupported","Target CoreCLR path cannot traverse reparse points.");
            string folderVersion=Path.GetFileName(Path.GetDirectoryName(loadedPath))??"";
            if(RuntimeSupport.Supports(folderVersion))
                runtimeVersion=folderVersion;
            else
            {
                string product=(FileVersionInfo.GetVersionInfo(loadedPath).ProductVersion??"").Split('+')[0].Trim();
                runtimeVersion=RuntimeSupport.Supports(product)?product:folderVersion;
            }
            if(!RuntimeSupport.Supports(runtimeVersion)||target.RuntimeVersion!="unknown"&&target.RuntimeVersion!=runtimeVersion)
                throw new LabException("capability_unsupported","Target runtime identity is unsupported or differs from its retained identity.");
            using var actual=new FileStream(loadedPath,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(actual.Length>134217728) throw new LabException("resource_limit","Runtime image exceeds its identity budget.");
            string hash=Hash(actual);
            try
            {
                string pinned=Path.Combine(RuntimeSupport.RuntimeDirectory(runtimeVersion),"coreclr.dll");
                if(!Path.GetFullPath(pinned).Equals(Path.GetFullPath(loadedPath),StringComparison.OrdinalIgnoreCase))
                {
                    using var expected=new FileStream(pinned,FileMode.Open,FileAccess.Read,FileShare.Read);
                    if(expected.Length>134217728) throw new LabException("resource_limit","Runtime image exceeds its identity budget.");
                    if(Hash(expected)!=hash) throw new LabException("capability_unsupported","Target CoreCLR image differs from the installed runtime.");
                }
            }
            catch(LabException error) when(error.Code=="capability_unsupported"&&error.Message.StartsWith("No compatible installed",StringComparison.Ordinal))
            { /* Custom or self-contained CoreCLR directory: sealed by actual loaded image SHA-256. */ }
            return hash;
        }
        catch(ArgumentException) { throw new LabException("target_gone","Profiler attachment target exited."); }
        catch(System.ComponentModel.Win32Exception) { throw new LabException("permission_denied","Target runtime identity is inaccessible."); }
    }
    public static bool OriginalTargetGone(TargetIdentity target)
    {
        try { using var process=Process.GetProcessById(target.ProcessId); return process.HasExited||ProcessIdentity.StartTimeUtcTicks(process)!=target.StartTimeUtcTicks; }
        catch(ArgumentException) { return true; }
    }
    public static void VerifyLoadedBinary(TargetIdentity target,string path)
    {
        using var process=Process.GetProcessById(target.ProcessId);
        if(ProcessIdentity.StartTimeUtcTicks(process)!=target.StartTimeUtcTicks) throw new LabException("target_identity_changed","Attachment target changed.");
        if(process.Modules.Count>1024) throw new LabException("resource_limit","Target module inventory exceeds policy.");
        var loaded=process.Modules.Cast<ProcessModule>().Where(m=>m.ModuleName.Equals("Tedd.DotNetLab.Profiler.dll",StringComparison.OrdinalIgnoreCase)).ToArray();
        if(loaded.Length!=1||!Path.GetFullPath(loaded[0].FileName).Equals(Path.GetFullPath(path),StringComparison.OrdinalIgnoreCase))
            throw new LabException("profiler_attach_unverified","The approved profiler path is not the unique loaded image.");
    }
}
