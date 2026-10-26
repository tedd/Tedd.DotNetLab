using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace Tedd.DotNetLab.Contracts;

public static class ProcessIdentity
{
    private static readonly Lazy<long> LinuxBootSeconds=new(ReadLinuxBootSeconds);
    [DllImport("libc",EntryPoint="sysconf",SetLastError=true)]
    private static extern long Sysconf(int name);

    public static long StartTimeUtcTicks(Process process)
    {
        if(!OperatingSystem.IsLinux()) return process.StartTime.ToUniversalTime().Ticks;
        // Process.StartTime estimates the boot epoch independently in each .NET
        // process. Kernel btime/starttime provide a common, exact identity basis.
        long frequency=Sysconf(2); // Linux _SC_CLK_TCK
        string stat=ReadBounded($"/proc/{process.Id}/stat",65536);
        return ParseLinuxStartTime(stat,process.Id,LinuxBootSeconds.Value,frequency);
    }

    public static long ParseLinuxStartTime(string stat,int expectedProcessId,long bootUnixSeconds,long clockFrequency)
    {
        if(stat.Length>65536||expectedProcessId<=0||bootUnixSeconds<0||clockFrequency<=0||clockFrequency>TimeSpan.TicksPerSecond)
            throw new LabException("transport_identity_unavailable","Linux process identity inputs are invalid.");
        int first=stat.IndexOf('('),last=stat.LastIndexOf(')');
        if(first<=0||last<=first||!int.TryParse(stat.AsSpan(0,first).Trim(),NumberStyles.None,CultureInfo.InvariantCulture,out int pid)||pid!=expectedProcessId)
            throw new LabException("transport_identity_unavailable","Linux process stat identity is malformed.");
        string[] fields=stat[(last+1)..].Split((char[]?)null,StringSplitOptions.RemoveEmptyEntries);
        // State is field 3; starttime is field 22. comm can contain spaces and ')'.
        if(fields.Length<20||!ulong.TryParse(fields[19],NumberStyles.None,CultureInfo.InvariantCulture,out ulong start))
            throw new LabException("transport_identity_unavailable","Linux process start counter is unavailable.");
        try
        {
            ulong elapsed=checked(start/(ulong)clockFrequency*(ulong)TimeSpan.TicksPerSecond+
                start%(ulong)clockFrequency*(ulong)TimeSpan.TicksPerSecond/(ulong)clockFrequency);
            long result=checked(DateTime.UnixEpoch.Ticks+bootUnixSeconds*TimeSpan.TicksPerSecond+(long)elapsed);
            if(result>DateTime.MaxValue.Ticks) throw new OverflowException();
            return result;
        }
        catch(OverflowException)
        { throw new LabException("transport_identity_unavailable","Linux process start counter exceeds its identity range."); }
    }

    private static long ReadLinuxBootSeconds()
    {
        string stat=ReadBounded("/proc/stat",1024*1024);
        foreach(string line in stat.Split('\n'))
            if(line.StartsWith("btime ",StringComparison.Ordinal)&&long.TryParse(line.AsSpan(6),NumberStyles.None,CultureInfo.InvariantCulture,out long value)&&value>=0)
                return value;
        throw new LabException("transport_identity_unavailable","Linux kernel boot epoch is unavailable.");
    }
    private static string ReadBounded(string path,int maximum)
    {
        try
        {
            using var input=File.OpenRead(path);
            byte[] bytes=new byte[maximum+1]; int used=0,read;
            while(used<bytes.Length&&(read=input.Read(bytes,used,bytes.Length-used))>0) used+=read;
            if(used>maximum) throw new LabException("transport_identity_unavailable","Linux process identity exceeds its read budget.");
            return Encoding.UTF8.GetString(bytes,0,used);
        }
        catch(FileNotFoundException) { throw new LabException("target_gone","Linux process identity no longer exists."); }
        catch(DirectoryNotFoundException) { throw new LabException("target_gone","Linux process identity no longer exists."); }
        catch(UnauthorizedAccessException) { throw new LabException("permission_denied","Linux process identity is inaccessible."); }
        catch(IOException) { throw new LabException("transport_identity_unavailable","Linux process identity could not be read."); }
    }
}
