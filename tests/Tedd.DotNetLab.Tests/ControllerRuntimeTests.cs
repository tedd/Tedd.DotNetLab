using System.Diagnostics;
using Tedd.DotNetLab.Application;
using Tedd.DotNetLab.Contracts;
using Tedd.DotNetLab.Harness;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ControllerRuntimeTests
{
    [Theory,
        InlineData("10.0.14",true,true),
        InlineData("11.0.0",true,true),
        InlineData("11.0.0-rc.1.26425.128",true,true),
        InlineData("11.0.0-rc.2.26504.105",true,true),
        InlineData("12.0.0-preview.1",true,true),
        InlineData("12.0.0-dev",true,false),
        InlineData("9.0.22",false,true),
        InlineData("8.0.33",false,true),
        InlineData("6.0.36",false,true),
        InlineData("5.0.17",false,false)]
    public void ControllerRequiresNet10PlusWhileTargetsSupportNet6PlusExceptRawCoreRun(string version,bool controllerSupported,bool sharedTargetSupported)
    {
        Assert.Equal(controllerSupported,RuntimeSupport.SupportsController(version,"windows"));
        Assert.Equal(controllerSupported,RuntimeSupport.SupportsController(version,"linux"));
        Assert.Equal(sharedTargetSupported,RuntimeSupport.Supports(version,"windows"));
        Assert.Equal(sharedTargetSupported,RuntimeSupport.Supports(version,"linux"));
    }

    [Fact]
    public async Task ControllerChildPreservesTheActuallyLoadedRuntime()
    {
        var start=RuntimeSupport.ControllerStartInfo();
        Assert.True(start.CreateNoWindow); Assert.False(start.UseShellExecute);
        start.RedirectStandardOutput=true; start.RedirectStandardError=true;
        start.ArgumentList.Add(typeof(Tedd.DotNetLab.Host.SharedController).Assembly.Location);
        start.ArgumentList.Add("compatibility");
        using var process=Process.Start(start)!;
        var output=process.StandardOutput.ReadToEndAsync(); var errors=process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Assert.True(process.ExitCode==0,await errors);
            var report=Wire.Deserialize<CompatibilityReport>(await output);
            Assert.Equal(RuntimeSupport.Current,report.Controller.RuntimeVersion);
            Assert.Equal(CapabilityState.Supported,report.Controller.State);
        }
        finally { if(!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } }
    }

    [Fact]
    public void UpdatedControllerAllowsManagedExecutionWithIndependentNativeAdmission()
    {
        var handshake=new HarnessHandshake(new("target",1,2,"runtime",RuntimeSupport.Net10,"X64"),[],false);
        ExecutionQualification.Validate(handshake,"quiet-validation",false);
        if(RuntimeSupport.HostSupported) return;
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(()=>
            ExecutionQualification.Validate(handshake,"quiet-validation",true)).Code);
        Assert.Equal("capability_unsupported",Assert.Throws<LabException>(ProfilerAttachmentIdentity.RequireHost).Code);
    }
}
