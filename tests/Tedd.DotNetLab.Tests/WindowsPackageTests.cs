using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace Tedd.DotNetLab.Tests;

public sealed class WindowsPackageTests(ITestOutputHelper output)
{
    private static async Task<(int ExitCode,string Stdout,string Stderr)> HashPackages(string assets)
    {
        string host=OperatingSystem.IsWindows()?@"C:\Program Files\dotnet\dotnet.exe":"dotnet";
        var start=new ProcessStartInfo(host) { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.PackageHashes","Release","net10.0","Tedd.DotNetLab.PackageHashes.dll"));
        start.ArgumentList.Add(assets);
        using var process=Process.Start(TestWorkspace.PinRuntime(start))!;
        var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        return(process.ExitCode,await stdout,await stderr);
    }
    [Fact,Case("UT-PACKAGE-SIGNED-CONTENT","FR-070,FR-074","M7","Contract")]
    public async Task SignedDependency_ContentAndArchiveHashesRemainDistinct_AlteredNoticeRejects()
    {
        using var workspace=new TestWorkspace();
        using var restored=JsonDocument.Parse(File.ReadAllText(Path.Combine(TestWorkspace.TaskRoot,"obj","Tedd.DotNetLab.Host","project.assets.json")));
        var source=restored.RootElement.GetProperty("libraries").GetProperty("Iced/1.21.0");
        string relative=source.GetProperty("path").GetString()!;
        string cached=restored.RootElement.GetProperty("packageFolders").EnumerateObject().Select(p=>Path.Combine(p.Name,relative,"iced.1.21.0.nupkg")).First(File.Exists);
        string cache=Path.Combine(workspace.Root,"cache"),copy=Path.Combine(cache,relative,"iced.1.21.0.nupkg");
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!); File.Copy(cached,copy);
        string assets=Path.Combine(workspace.Root,"assets.json");
        var input=new { packageFolders=new Dictionary<string,object> { [cache]=new {} },libraries=new Dictionary<string,object>
            { ["Iced/1.21.0"]=new { type="package",path=relative,sha512=source.GetProperty("sha512").GetString() } } };
        await File.WriteAllTextAsync(assets,JsonSerializer.Serialize(input));
        var original=await HashPackages(assets); Assert.True(original.ExitCode==0,original.Stderr);
        using var hashes=JsonDocument.Parse(original.Stdout); var observed=hashes.RootElement.GetProperty("Iced/1.21.0");
        Assert.True(observed.GetProperty("signed").GetBoolean()); Assert.True(observed.GetProperty("integrity_verified").GetBoolean());
        Assert.Equal(source.GetProperty("sha512").GetString(),observed.GetProperty("content_sha512").GetString());
        Assert.Equal(Convert.ToBase64String(SHA512.HashData(await File.ReadAllBytesAsync(copy))),observed.GetProperty("archive_sha512").GetString());
        Assert.NotEqual(observed.GetProperty("content_sha512").GetString(),observed.GetProperty("archive_sha512").GetString());
        using(var archive=ZipFile.Open(copy,ZipArchiveMode.Update))
        {
            archive.GetEntry("LICENSE.txt")!.Delete();
            using var writer=new StreamWriter(archive.CreateEntry("LICENSE.txt").Open()); await writer.WriteAsync("Altered license evidence.");
        }
        var altered=await HashPackages(assets); Assert.NotEqual(0,altered.ExitCode); Assert.Empty(altered.Stdout);
        Assert.Contains("Package integrity validation failed",altered.Stderr);
    }
    [Fact,Case("UT-PACKAGE-INTEGRITY","FR-070,FR-074","M7","Contract")]
    public async Task DistributionVerifier_AlteredOrAmbiguousArtifacts_RejectsBeforeExecution()
    {
        using var workspace=new TestWorkspace();
        string python=OperatingSystem.IsWindows()&&File.Exists(@"C:\Python314\python.exe")?@"C:\Python314\python.exe":
            OperatingSystem.IsWindows()?"python":"python3";
        var start=new ProcessStartInfo(python)
            { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true };
        start.Environment["PYTHONDONTWRITEBYTECODE"]="1";
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory,"tests","windows_package_tests.py"));
        start.ArgumentList.Add("--workspace"); start.ArgumentList.Add(workspace.Root);
        using var process=Process.Start(TestWorkspace.PinRuntime(start))!;
        var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60)); }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
        string result=(await stdout)+(await stderr); output.WriteLine(result);
        Assert.True(process.ExitCode==0,result);
        Assert.Contains("OK",result);
    }
}
