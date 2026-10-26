using System.Reflection;
using System.Security.Cryptography;

namespace Tedd.DotNetLab.Harness;

public static class MethodBinding
{
    // Build once during application startup or scenario registration, outside measurement.
    public static string Scope(MethodInfo method)
    {
        if(method.Module.Assembly.IsDynamic||method.Module.Assembly.IsCollectible)
            throw new ArgumentException("Mutation bindings require a retained noncollectible module.",nameof(method));
        using var stream=File.OpenRead(method.Module.FullyQualifiedName);
        if(stream.Length>67108864) throw new ArgumentException("Module exceeds binding policy.",nameof(method));
        string hash=Convert.ToHexStringLower(SHA256.HashData(stream));
        return method.Module.ModuleVersionId.ToString("D")+"/0x"+method.MetadataToken.ToString("x8")+"/"+hash;
    }
}
