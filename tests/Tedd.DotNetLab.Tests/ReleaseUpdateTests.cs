using System.Net;
using System.Text;
using ModelContextProtocol.Server;
using Tedd.DotNetLab.Host;
using Xunit;

namespace Tedd.DotNetLab.Tests;

public sealed class ReleaseUpdateTests
{
    [Theory,Case("UT-UPDATE-COMPARE","FR-060,FR-074","M7","Contract")]
    [InlineData("1.0.0","v1.0.1",true)]
    [InlineData("1.9.0","v1.10.0",true)]
    [InlineData("1.0.0","v1.0.0",false)]
    [InlineData("2.0.0","v1.0.0",false)]
    [InlineData("1.0.0-dev","v1.0.0",true)]
    [InlineData("1.1.0-preview.1+abc","v1.0.0",false)]
    [InlineData("1.0.0+abc","v1.0.0",false)]
    public async Task ComparesReleaseVersionsNumerically(string installed,string latest,bool available)
    {
        using var client=new HttpClient(new Reply($$"""{"tag_name":"{{latest}}","draft":false,"prerelease":false}"""));
        var status=await ReleaseUpdates.CheckAsync(client,installed);
        Assert.Equal("checked",status.CheckStatus);
        Assert.Equal(available,status.UpdateAvailable);
        Assert.Equal(latest,status.LatestVersion);
        Assert.Equal(installed,status.InstalledVersion);
        if(available)
        {
            Assert.Contains("offer to upgrade",status.Instructions);
            Assert.Contains(status.UpgradeUrl,status.Instructions);
        }
        else Assert.DoesNotContain("offer to upgrade",status.Instructions);
    }

    [Theory,Case("UT-UPDATE-INVALID","FR-060,FR-074","M7","Contract")]
    [InlineData("{}")] // Missing release fields.
    [InlineData("not json")]
    [InlineData("{\"tag_name\":\"v2.0.0-preview.1\",\"draft\":false,\"prerelease\":true}")]
    [InlineData("{\"tag_name\":\"v2.0.0\",\"draft\":true,\"prerelease\":false}")]
    [InlineData("{\"tag_name\":\"untrusted release text\",\"draft\":false,\"prerelease\":false}")]
    public async Task InvalidReleaseDataDoesNotInterruptStartup(string json)
    {
        using var client=new HttpClient(new Reply(json));
        var status=await ReleaseUpdates.CheckAsync(client,"1.0.0");
        Assert.Equal("unavailable",status.CheckStatus);
        Assert.False(status.UpdateAvailable);
        Assert.DoesNotContain("offer to upgrade",status.Instructions);
    }

    [Theory,Case("UT-UPDATE-NETWORK","FR-060,FR-074","M7","Contract")]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task NetworkFailureDoesNotClaimCurrentVersion(HttpStatusCode code)
    {
        using var client=new HttpClient(new Reply("",code));
        var status=await ReleaseUpdates.CheckAsync(client,"1.0.0");
        Assert.Equal("unavailable",status.CheckStatus);
        Assert.Contains("do not claim",status.Instructions);
    }

    [Fact,Case("UT-UPDATE-TIMEOUT","FR-060,FR-074","M7","Contract")]
    public async Task TimeoutDoesNotInterruptStartup()
    {
        using var client=new HttpClient(new TimeoutReply());
        Assert.Equal("unavailable",(await ReleaseUpdates.CheckAsync(client,"1.0.0")).CheckStatus);
    }

    [Fact,Case("UT-UPDATE-INITIALIZE","FR-060,FR-074","M7","Contract")]
    public void McpInitializationIncludesCompiledVersionAndUpgradeOffer()
    {
        var options=new McpServerOptions();
        var status=new ReleaseStatus(ReleaseUpdates.InstalledVersion,"checked","v99.0.0",true);
        ReleaseUpdates.Configure(options,status);
        Assert.Equal("Tedd.DotNetLab",options.ServerInfo!.Name);
        Assert.Equal(ReleaseUpdates.InstalledVersion,options.ServerInfo.Version);
        Assert.Equal(status.Instructions,options.ServerInstructions);
        Assert.Contains("agreement",options.ServerInstructions);
    }

    private sealed class Reply(string json,HttpStatusCode code=HttpStatusCode.OK):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            Assert.Equal(ReleaseUpdates.Endpoint,request.RequestUri!.AbsoluteUri);
            Assert.NotEmpty(request.Headers.UserAgent);
            return Task.FromResult(new HttpResponseMessage(code) { Content=new StringContent(json,Encoding.UTF8,"application/json") });
        }
    }
    private sealed class TimeoutReply:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
            =>throw new TaskCanceledException();
    }
}
