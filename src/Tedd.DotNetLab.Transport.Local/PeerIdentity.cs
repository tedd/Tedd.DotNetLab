using System.IO.Pipes;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Tedd.DotNetLab.Contracts;

namespace Tedd.DotNetLab.Transport.Local;

public static class PeerIdentity
{
    [DllImport("kernel32",SetLastError=true)]
    [return:MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNamedPipeServerProcessId(SafePipeHandle pipe,out uint processId);
    [StructLayout(LayoutKind.Sequential)]
    private struct LinuxCredentials
    {
        public int ProcessId;
        public uint UserId;
        public uint GroupId;
    }
    [DllImport("libc",EntryPoint="getsockopt",SetLastError=true)]
    private static extern int GetPeerCredentials(SafePipeHandle pipe,int level,int option,
        out LinuxCredentials credentials,ref uint length);
    [DllImport("libc")]
    private static extern uint geteuid();
    public static void VerifyServer(NamedPipeClientStream pipe,TargetIdentity target)
    {
        uint processId;
        if(OperatingSystem.IsWindows())
        {
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle,out processId))
                throw new LabException("transport_identity_unavailable","Cannot verify the local pipe server process.");
        }
        else if(OperatingSystem.IsLinux())
        {
            // Linux AF_UNIX sockets expose kernel credentials captured at connect/listen.
            // NamedPipeClientStream.SafePipeHandle is the connected socket descriptor.
            uint length=(uint)Marshal.SizeOf<LinuxCredentials>();
            if(GetPeerCredentials(pipe.SafePipeHandle,1,17,out var credentials,ref length)!=0||
                length!=Marshal.SizeOf<LinuxCredentials>()||credentials.ProcessId<=0)
                throw new LabException("transport_identity_unavailable","Cannot verify the local socket server credentials.");
            if(credentials.UserId!=geteuid())
                throw new LabException("permission_denied","Local socket server belongs to a different user.");
            processId=(uint)credentials.ProcessId;
        }
        else
            throw new LabException("capability_unsupported","Pipe peer identity requires separate platform qualification.");
        if(processId!=target.ProcessId)
            throw new LabException("target_identity_changed","Pipe server process differs from the selected target.");
        try
        {
            using var process=Process.GetProcessById(target.ProcessId);
            if(process.HasExited||ProcessIdentity.StartTimeUtcTicks(process)!=target.StartTimeUtcTicks)
                throw new LabException("target_identity_changed","Pipe server process start differs from the selected target.");
        }
        catch(Exception error) when(error is ArgumentException or InvalidOperationException)
        { throw new LabException("target_gone","Pipe server process exited before identity verification."); }
        catch(System.ComponentModel.Win32Exception)
        { throw new LabException("transport_identity_unavailable","Cannot verify the pipe server process start identity."); }
    }
}
