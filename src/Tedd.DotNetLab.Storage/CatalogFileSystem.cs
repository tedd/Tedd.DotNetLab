using System.Runtime.InteropServices;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Storage;

public static class CatalogFileSystem
{
    // Linux x64 struct statfs: fifteen 64-bit words, with f_type first.
    [StructLayout(LayoutKind.Sequential,Size=120)]
    private struct FileSystemStatus { public long Type; }
    [DllImport("libc",EntryPoint="statfs",SetLastError=true)]
    private static extern int StatFs([MarshalAs(UnmanagedType.LPUTF8Str)] string path,out FileSystemStatus result);

    public static void Validate(string directory)
    {
        if(!OperatingSystem.IsLinux()) return;
        if(RuntimeInformation.ProcessArchitecture!=Architecture.X64||StatFs(directory,out var status)!=0)
            throw new LabException("catalog_filesystem_unsupported","Catalog filesystem identity could not be verified.",
                "Use a local Linux x64 filesystem with supported SQLite WAL shared-memory semantics.");
        ValidateLinuxType(status.Type);
    }
    public static void ValidateLinuxType(long type)
    {
        // Kernel filesystem identities for ext4, XFS, Btrfs, and local overlayfs.
        // v9fs/DrvFS, network mounts, FUSE, volatile filesystems, and unknown types
        // cannot establish the catalog's persistent shared-memory contract.
        if(type is not (0xEF53 or 0x58465342 or 0x9123683E or 0x794c7630))
            throw new LabException("catalog_filesystem_unsupported","Catalog requires a supported native local Linux filesystem.",
                "Place the catalog on ext4, XFS, Btrfs, or local overlayfs. WSL Windows-backed paths are unsupported for SQLite WAL catalogs.");
    }
}
