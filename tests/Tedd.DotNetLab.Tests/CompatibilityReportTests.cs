using System.Diagnostics;
using System.Text.Json;
using System.Xml.Linq;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Mcp;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class CompatibilityReportTests
{
    [Fact,Case("CT-COMPAT-001","FR-001,FR-004,FR-074","M7","Contract")]
    public void BuildPinsReflectTheActualPinnedBuildAndCentralPackageManifest()
    {
        var pins=CompatibilityReports.BuildPins();
        Assert.True(RuntimeSupport.SupportsController(pins.CoreClrVersion,"windows"));
        Assert.Equal(System.Reflection.CustomAttributeExtensions.GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>(typeof(CompatibilityReportTests).Assembly)
            .Single(item=>item.Key=="TestSdkVersion").Value,pins.SdkVersion);
        // Repository manifest is an independently read source; build metadata cannot supply both sides of this assertion.
        string source=Path.Combine(SourceRoot(),"Directory.Packages.props");
        var expected=XDocument.Load(source).Descendants("PackageVersion").ToDictionary(item=>item.Attribute("Include")!.Value,item=>item.Attribute("Version")!.Value,StringComparer.Ordinal);
        Assert.Equal(expected.Count,pins.Packages.Count); foreach(var item in expected) Assert.Equal(item.Value,pins.Packages[item.Key]);
    }
    [Theory,InlineData("windows","x64","10.0.12","coreclr",true),InlineData("linux","x64","10.0.12","coreclr",true),
        InlineData("windows","arm64","10.0.12","coreclr",false),InlineData("windows","x86","10.0.12","coreclr",false),
        InlineData("macos","x64","10.0.12","coreclr",false),InlineData("windows","x64","10.0.13","coreclr",true),
        InlineData("windows","x64","11.0.0-rc.2.26504.105","coreclr",true),InlineData("windows","x64","11.0.0","coreclr",true),
        InlineData("linux","x64","11.0.0-rc.2.26504.105","coreclr",true),InlineData("windows","x64","12.0.0","coreclr",true),
        InlineData("windows","x64","10.0.14","coreclr",true),InlineData("linux","x64","10.0.14","coreclr",true),
        InlineData("windows","x64","10.0.11","coreclr",true),InlineData("windows","x64","9.0.21","coreclr",false),
        InlineData("windows","x64","10.0.12-preview.1","coreclr",true),InlineData("windows","x64","5.0.17","coreclr",false),
        InlineData("windows","x64","latest","coreclr",false),
        InlineData("linux","x64","10.0.12","mono",false),InlineData("windows","x64","10.0.12","unknown",false),
        Case("UT-COMPAT-001","FR-001,FR-004","M7","Unit")]
    public void ControllerCompatibilityIsIndependentOfExactTargetQualification(string os,string arch,string version,string engine,bool supported)
    {
        var result=CompatibilityReports.Controller(CompatibilityReports.BuildPins(),os,arch,version,"test-runtime",engine);
        Assert.Equal(supported?CapabilityState.Supported:CapabilityState.Unsupported,result.State); Assert.NotEmpty(result.Reason);
        var changed=CompatibilityReports.BuildPins() with { CoreClrVersion="10.0.13" };
        Assert.False(CompatibilityReports.Controller(changed,"linux","x64","10.0.12","test-runtime","coreclr").BuildPinsMatchQualification);
        Assert.Equal(CapabilityState.Supported,CompatibilityReports.Controller(changed,"windows","x64","10.0.12","test-runtime","coreclr").State);
    }
    private static string SourceRoot([System.Runtime.CompilerServices.CallerFilePath] string source="")=>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(source)!,"..",".."));
    [Fact,Case("CT-COMPAT-002","FR-001,FR-004,FR-070","M7","Contract")]
    public void MatrixSeparatesStartupAttachAndUnavailablePrivateCommitQualification()
    {
        var report=CompatibilityReports.Create(ProfilerTests.ProfilerPath);
        Assert.False(report.PermissionsGranted); Assert.False(report.FullPlatformCertification); Assert.Null(report.Target);
        var windows=Assert.Single(report.Platforms,item=>item.OperatingSystem=="windows"&&item.RuntimeVersion==RuntimeSupport.Net10); var linux=Assert.Single(report.Platforms,item=>item.OperatingSystem=="linux");
        Assert.Equal("10.0.12",windows.RuntimeVersion); Assert.Equal("x64",windows.Architecture);
        Assert.Equal(CapabilityState.Supported,Assert.Single(windows.Features,item=>item.Name=="native_attach").State);
        Assert.Equal(CapabilityState.Unsupported,Assert.Single(linux.Features,item=>item.Name=="native_attach").State);
        Assert.Equal(CapabilityState.Unsupported,Assert.Single(windows.Features,item=>item.Name=="attach_probes").State);
        Assert.Equal(CapabilityState.Unsupported,Assert.Single(linux.Features,item=>item.Name=="mutation_private_commit_budget").State);
        Assert.Contains(report.MethodRestrictions,item=>item.Contains("generic",StringComparison.Ordinal));
        Assert.All(report.Dependencies,item=>{ Assert.Equal(64,item.BinaryHash!.Length); Assert.Equal(Availability.Available,item.IdentityAvailability); Assert.NotEmpty(item.ModuleVersionId); });
    }
    [Fact,Case("CT-COMPAT-003","FR-001,FR-060,FR-070,FR-074","M7","Integration")]
    public async Task DiscoveryReportsRetainedTargetCapabilitiesWithoutEndpointAccessOrImplicitAuthorization()
    {
        using var workspace=new TestWorkspace(); await using var service=new LabService(workspace.Root,approvedProfilerPath:Path.Combine(workspace.Root,"missing","Tedd.DotNetLab.Profiler.dll"));
        // This nonexistent process deliberately cannot answer a handshake. Reporting must remain a retained-data read.
        var target=new TargetInfo(new("retained-target",int.MaxValue,123,"runtime","unknown","unknown"),"fixture",
            new Dictionary<string,Capability>{{"eventpipe",new(CapabilityState.Unknown,"Endpoint permission was not validated.")},{"rejit",new(CapabilityState.Blocked,"Unresolved restoration.")}});
        service.Catalog.Put(target.Identity.Id,"target",target);
        using var read=new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=Path.Combine(workspace.Root,"catalog.sqlite"),Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString()); read.Open();
        long Count(string table) { using var query=read.CreateCommand(); query.CommandText=table=="AuditEvent"?"SELECT COUNT(*) FROM AuditEvent":"SELECT COUNT(*) FROM Operation"; return Convert.ToInt64(query.ExecuteScalar()); }
        long audit=Count("AuditEvent");
        var response=await new LabTools(service).Discover(new DiscoverCompatibility(target.Identity.Id)); Assert.Equal("ok",response.Status);
        var report=response.Data!.Value.Deserialize<CompatibilityReport>(Wire.Json)!;
        Assert.Equal(target.Identity,report.Target!.Identity); Assert.False(report.Target.ProcessIdentityRevalidated); Assert.False(report.Target.ProfilerHandshakeRevalidated);
        Assert.Equal(CapabilityState.Blocked,report.Target.Capabilities["rejit"].State); Assert.Equal(CapabilityState.Unknown,report.Target.Capabilities["eventpipe"].State);
        Assert.False(report.PermissionsGranted); Assert.Equal(audit,Count("AuditEvent"));
        Assert.Empty(service.Catalog.List("grant").Rows); Assert.Equal(0,Count("Operation"));
        Assert.Equal(Availability.NotCollected,report.NativeBinary.Availability); Assert.Equal("not_collected",report.NativeBinary.SignatureStatus);
        Assert.Equal("invalid_request",(await new LabTools(service).InvokeAsync("lab.discover","{\"action\":\"compatibility\",\"binary_path\":\"arbitrary.dll\"}")).Code);
    }
    [Fact,Case("CT-COMPAT-004","FR-001,FR-004,FR-070","M7","Integration")]
    public async Task CliCompatibilityDoesNotOpenCatalogAndUsesActualLoadedDependencyIdentities()
    {
        using var workspace=new TestWorkspace(); var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=workspace.Root };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll")); start.ArgumentList.Add("compatibility");
        using var process=Process.Start(TestWorkspace.PinRuntime(start))!; var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); Assert.True(process.ExitCode==0,await stderr);
        var report=Wire.Deserialize<CompatibilityReport>(await stdout); Assert.Equal(CompatibilityReports.Revision,report.QualificationRevision);
        Assert.Equal(RuntimeSupport.Current,report.Controller.RuntimeVersion); Assert.Equal("coreclr",report.Controller.RuntimeEngine);
        Assert.False(report.FullPlatformCertification); Assert.False(report.PermissionsGranted); Assert.Null(report.Target);
        Assert.All(report.Dependencies,item=>Assert.NotEmpty(item.InformationalVersion!)); Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }
    [Fact,Case("CT-COMPAT-005","FR-001,FR-060,FR-070","M7","Integration")]
    public async Task InvalidCompatibilityCliArgumentsAreRejectedBeforeWorkspaceCreation()
    {
        using var workspace=new TestWorkspace(); string forbidden=Path.Combine(workspace.Root,"must-not-be-created");
        var start=new ProcessStartInfo("dotnet") { UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=workspace.Root };
        start.ArgumentList.Add(Path.Combine(TestWorkspace.TaskRoot,"bin","Tedd.DotNetLab.Host","Release","net10.0","Tedd.DotNetLab.Host.dll"));
        start.ArgumentList.Add("compatibility"); start.ArgumentList.Add(forbidden);
        using var process=Process.Start(TestWorkspace.PinRuntime(start))!; var stdout=process.StandardOutput.ReadToEndAsync(); var stderr=process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(0,process.ExitCode); Assert.Empty(await stdout); Assert.Equal("invalid_request",Wire.Deserialize<LabResponse>(await stderr).Code);
        Assert.Empty(Directory.EnumerateFileSystemEntries(workspace.Root));
    }
    [Fact,Case("CT-COMPAT-006","FR-001,FR-004,FR-070","M7","Integration")]
    public void InvalidConfiguredProfilerImageIsReportedWithoutLoadingOrCertification()
    {
        if(!OperatingSystem.IsWindows()) return;
        using var workspace=new TestWorkspace(); string path=Path.Combine(workspace.Root,"Tedd.DotNetLab.Profiler.dll"); File.WriteAllBytes(path,new byte[32]);
        var report=CompatibilityReports.Create(path); Assert.Equal(Availability.NotCollected,report.NativeBinary.Availability);
        Assert.Equal("invalid_profiler_binary",report.NativeBinary.Reason); Assert.Null(report.NativeBinary.BinaryHash);
        Assert.Equal("not_collected",report.NativeBinary.SignatureStatus); Assert.False(report.FullPlatformCertification); Assert.False(report.PermissionsGranted);
    }
}
