using System.Reflection;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Tedd.DotNetLab.Host;

internal sealed record ReleaseStatus(string InstalledVersion,string CheckStatus,string? LatestVersion,bool UpdateAvailable)
{
    public string UpgradeUrl => "https://github.com/tedd/Tedd.DotNetLab#upgrading";
    public string ReleasesUrl => "https://github.com/tedd/Tedd.DotNetLab/releases/latest";
    public string Instructions => $"Tedd.DotNetLab installed version: {InstalledVersion}. " +
        (UpdateAvailable
            ? $"A newer stable Tedd.DotNetLab version ({LatestVersion}) is available. Inform the user once per chat and offer to upgrade. Include upgrade instructions: {UpgradeUrl} and release notes: {ReleasesUrl}. Obtain the user's agreement before upgrading. "
            : CheckStatus=="unavailable" ? "The GitHub update check was unavailable; do not claim this is the latest version. " : "No newer stable GitHub release is available. ");
}

internal static class ReleaseUpdates
{
    internal const string Endpoint="https://api.github.com/repos/tedd/Tedd.DotNetLab/releases/latest";
    internal static string InstalledVersion { get; }=typeof(ReleaseUpdates).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    private static readonly HttpClient Client=new(new HttpClientHandler { AllowAutoRedirect=false })
        { Timeout=TimeSpan.FromSeconds(3),MaxResponseContentBufferSize=1024*1024 };
    private static readonly object Gate=new();
    private static Task<ReleaseStatus>? cached;
    private static DateTimeOffset expires;

    internal static Task<ReleaseStatus> GetAsync()
    {
        lock(Gate)
        {
            if(cached is null||DateTimeOffset.UtcNow>=expires)
            {
                expires=DateTimeOffset.UtcNow.AddHours(6);
                cached=CheckAsync(Client,InstalledVersion);
            }
            return cached;
        }
    }

    internal static async Task<ReleaseStatus> CheckAsync(HttpClient client,string installedVersion)
    {
        try
        {
            using var request=new HttpRequestMessage(HttpMethod.Get,Endpoint);
            request.Headers.UserAgent.ParseAdd("Tedd.DotNetLab/"+InstalledVersion);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response=await client.SendAsync(request);
            response.EnsureSuccessStatusCode();
            using var document=JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            var release=document.RootElement;
            string? tag=release.GetProperty("tag_name").GetString();
            if(release.GetProperty("draft").GetBoolean()||release.GetProperty("prerelease").GetBoolean()||
                tag is null||!TryVersion(tag,out var latest)||tag.Contains('-')||
                !TryVersion(installedVersion,out var installed))
                return new(installedVersion,"unavailable",null,false);
            bool newer=latest>installed||(latest==installed&&installedVersion.Contains('-'));
            return new(installedVersion,"checked",tag,newer);
        }
        catch(Exception error) when(error is HttpRequestException or OperationCanceledException or JsonException or
            InvalidOperationException or KeyNotFoundException or FormatException)
        {
            return new(installedVersion,"unavailable",null,false);
        }
    }

    private static bool TryVersion(string value,out Version version)
    {
        string core=value.TrimStart('v').Split('+')[0].Split('-')[0];
        if(Version.TryParse(core,out var parsed)&&parsed.Build>=0&&parsed.Revision<0)
        {
            version=parsed;
            return true;
        }
        version=new Version(0,0,0);
        return false;
    }

    internal static void Configure(McpServerOptions options,ReleaseStatus status)
    {
        options.ServerInfo=new Implementation { Name="Tedd.DotNetLab",Version=InstalledVersion };
        options.ServerInstructions=status.Instructions;
    }
}
