using System.Runtime.InteropServices;

namespace Tedd.DotNetLab.Contracts;

public static class DiskSpace
{
    [StructLayout(LayoutKind.Sequential,Size=120)]
    private struct LinuxStatus
    {
        public long Type,BlockSize;
        public ulong Blocks,FreeBlocks,AvailableBlocks;
    }
    [DllImport("libc",EntryPoint="statfs",SetLastError=true)]
    private static extern int StatFs([MarshalAs(UnmanagedType.LPUTF8Str)] string path,out LinuxStatus result);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string directory,out ulong available,out ulong total,out ulong free);

    public static long AvailableBytes(string directory)
    {
        string full=Path.GetFullPath(directory);
        if(OperatingSystem.IsWindows())
        {
            if(!GetDiskFreeSpaceExW(full,out var available,out _,out _))
                throw new LabException("disk_space_unavailable","Resolved workspace volume space could not be read.");
            return available>long.MaxValue?long.MaxValue:(long)available;
        }
        if(OperatingSystem.IsLinux()&&RuntimeInformation.ProcessArchitecture==Architecture.X64&&StatFs(full,out var status)==0&&status.BlockSize>0)
            return Bytes(status.AvailableBlocks,status.BlockSize);
        throw new LabException("disk_space_unavailable","Resolved workspace volume space could not be read.");
    }
    public static long Bytes(ulong blocks,long blockSize)
    {
        if(blockSize<=0) throw new LabException("disk_space_unavailable","Filesystem block size is invalid.");
        return blocks>(ulong)(long.MaxValue/blockSize)?long.MaxValue:(long)blocks*blockSize;
    }
    public static bool Fits(long available,long required,long reserve)
    {
        if(available<0||required<0||reserve<0) throw new LabException("invalid_request","Disk budget values must be nonnegative.");
        return available>=reserve&&required<=available-reserve;
    }
    public static bool Fits(string directory,long required,long reserve)=>Fits(AvailableBytes(directory),required,reserve);
}
