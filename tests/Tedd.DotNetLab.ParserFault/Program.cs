using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Security.Cryptography;
using Tedd.DotNetLab.Analysis.Runtime;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Transport.Local;

using var parent=ParserParentGuard.Start();
var request=await Frames.ReadAsync<TraceWorkerRequest>(Console.OpenStandardInput(),CancellationToken.None);
switch(args.Single())
{
    case "stall": await Task.Delay(Timeout.Infinite); break;
    case "descendants":
        using(var child=Process.Start(new ProcessStartInfo("/bin/sleep","300") { UseShellExecute=false })!)
        {
            await File.WriteAllTextAsync(request.OutputPath+".ready.tmp",Wire.Serialize(new {
                worker=Environment.ProcessId,descendant=child.Id,group=Environment.GetEnvironmentVariable("DOTNETLAB_PARSER_OWNED_GROUP") }));
            File.Move(request.OutputPath+".ready.tmp",request.OutputPath+".ready");
            await Task.Delay(Timeout.Infinite);
        }
        break;
    case "scratch":
        string scratch=Path.Combine(Path.GetDirectoryName(request.OutputPath)!,"stack-conversion-"+Path.GetFileNameWithoutExtension(request.OutputPath));
        Directory.CreateDirectory(scratch); await File.WriteAllBytesAsync(Path.Combine(scratch,"trace.etlx"),new byte[2097152]);
        await Task.Delay(Timeout.Infinite); break;
    case "crash": Environment.Exit(17); break;
    case "identity":
        await Frames.WriteAsync(Console.OpenStandardOutput(),new TraceWorkerReply(Wire.Version,"ok",null,"hash",1,Environment.ProcessId+1,0,0,0),CancellationToken.None); break;
    case "source": case "hash":
        var summary=new TraceSummary("analysis-fault",args[0]=="source"?"wrong-source":request.Capture.Id,request.SourceArtifact.Id,request.Capture.Profile.Hash,
            TraceAnalyzer.Version,new("partial","unknown",TraceAnalyzer.Version,[]),[],[],[],[],[],0,new Dictionary<string,long>());
        byte[] bytes=System.Text.Encoding.UTF8.GetBytes(Wire.Serialize(summary)); await File.WriteAllBytesAsync(request.OutputPath,bytes);
        using(var process=Process.GetCurrentProcess())
            await Frames.WriteAsync(Console.OpenStandardOutput(),new TraceWorkerReply(Wire.Version,"ok",null,args[0]=="hash"?new string('0',64):Convert.ToHexStringLower(SHA256.HashData(bytes)),
                bytes.Length,process.Id,ProcessIdentity.StartTimeUtcTicks(process),0,0),CancellationToken.None); break;
    case "stack_source": case "stack_policy":
        var stack=new StackParserResult(args[0]=="stack_source"?"wrong-source":request.Capture.Id,request.SourceArtifact.Id,request.Capture.Profile.Hash,
            StackParser.Version,args[0]=="stack_policy"?request.StackPolicy! with { MaximumDepth=request.StackPolicy!.MaximumDepth+1 }:request.StackPolicy!,
            new StackAccumulator().Snapshot(),new("partial","unknown",StackParser.Version,[]),0,new Dictionary<string,long>());
        byte[] stackBytes=System.Text.Encoding.UTF8.GetBytes(Wire.Serialize(stack)); await File.WriteAllBytesAsync(request.OutputPath,stackBytes);
        using(var process=Process.GetCurrentProcess())
            await Frames.WriteAsync(Console.OpenStandardOutput(),new TraceWorkerReply(Wire.Version,"ok",null,Convert.ToHexStringLower(SHA256.HashData(stackBytes)),
                stackBytes.Length,process.Id,ProcessIdentity.StartTimeUtcTicks(process),0,0),CancellationToken.None); break;
    case "memory":
        if(OperatingSystem.IsLinux())
        {
            var retained=new List<nint>();
            for(int index=0;index<256;index++)
            {
                nint block=Marshal.AllocHGlobal(16777216);
                for(int page=0;page<16777216;page+=4096) Marshal.WriteByte(block,page,1);
                retained.Add(block);
            }
            throw new InvalidOperationException("Linux containment did not stop growth.");
        }
        var blocks=new List<nint>(); bool rejected=false;
        try
        {
            for(int index=0;index<=request.Policy.MaximumCommittedMemoryBytes/16777216;index++)
            {
                nint block=Native.Allocate(0,16777216,0x3000,0x04);
                if(block==0) { rejected=true; break; } blocks.Add(block);
            }
        }
        finally { foreach(nint block in blocks) Native.Free(block,0,0x8000); }
        await Frames.WriteAsync(Console.OpenStandardOutput(),new TraceWorkerReply(Wire.Version,"error",rejected?"parser_memory_limit":"containment_unenforced",null,0,Environment.ProcessId,0,0,0),CancellationToken.None); break;
    default: throw new InvalidOperationException("Unknown fault fixture.");
}
internal static class Native
{
    [DllImport("kernel32.dll",EntryPoint="VirtualAlloc",SetLastError=true)] public static extern nint Allocate(nint address,nuint size,uint allocation,uint protection);
    [DllImport("kernel32.dll",EntryPoint="VirtualFree",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] public static extern bool Free(nint address,nuint size,uint type);
}
