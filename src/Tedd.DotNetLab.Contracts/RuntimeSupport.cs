using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Tedd.DotNetLab.Contracts;

public static class RuntimeSupport
{
    public const string Net10="10.0.12",Net10Patch="10.0.13",Net11="11.0.0-rc.2.26504.105";
    public const string Net12Development="12.0.0-dev";

    public static string Current=>RuntimeInformation.FrameworkDescription.StartsWith(".NET ",StringComparison.Ordinal)
        ?RuntimeInformation.FrameworkDescription[5..]:Environment.Version.ToString();

    public static string OperatingSystemFamily=>OperatingSystem.IsWindows()?"windows":OperatingSystem.IsLinux()?"linux":"other";

    // Any valid CoreCLR runtime >= 6.0 (including previews, RCs such as .NET 11 RC2, and GA releases)
    // is supported on Windows x64 and Linux x64. Net12Development ("12.0.0-dev") remains reserved for
    // raw Core_Root / corerun.exe development fixtures without an installed shared framework host.
    public static bool IsSelectableManagedVersion(string version)=>version.Length<=128&&Regex.IsMatch(version,
        @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?\z",
        RegexOptions.CultureInvariant)&&Version.TryParse(version.Split('-', '+')[0],out var parsed)&&parsed.Major>=6;

    public static bool Supports(string version,string os)=>
        os is "windows" or "linux"&&IsSelectableManagedVersion(version)&&version!=Net12Development;

    public static bool Supports(string version)=>Supports(version,OperatingSystemFamily);

    public static bool SupportsNormalBuild(string version,string os)=>Supports(version,os);
    public static bool SupportsNormalBuild(string version)=>SupportsNormalBuild(version,OperatingSystemFamily);

    public static bool SupportsManagedExecution(string version,string os)=>
        Supports(version,os)||os=="windows"&&version==Net12Development;

    // The controller binary targets net10.0 with RollForward=Major, so any CoreCLR 10.0.0+ runtime
    // (including 10.0.x, .NET 11 previews/RCs/GA, .NET 12+, etc.) on Windows/Linux x64 can host it.
    public static bool SupportsController(string version,string os)=>
        os is "windows" or "linux"&&IsSelectableManagedVersion(version)&&
        Version.Parse(version.Split('-', '+')[0]).Major>=10;

    public static bool ControllerSupported=>
        RuntimeInformation.ProcessArchitecture==Architecture.X64&&SupportsController(Current,OperatingSystemFamily);

    public static bool HostSupported=>ControllerSupported;

    public static string Framework(string version)
    {
        if(Version.TryParse(version.Split('-', '+')[0],out var parsed)&&parsed.Major>=6)
            return $"net{parsed.Major}.{parsed.Minor}";
        return "net10.0";
    }

    public static IReadOnlyList<string> CandidateDotnetRoots()
    {
        var candidates=new List<string>();
        void AddIfValid(string? path)
        {
            if(!string.IsNullOrWhiteSpace(path)&&Path.IsPathFullyQualified(path))
                candidates.Add(Path.GetFullPath(path));
        }

        AddIfValid(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..",".."));
        AddIfValid(Environment.GetEnvironmentVariable("DOTNET_ROOT"));
        AddIfValid(Environment.GetEnvironmentVariable("DOTNET_ROOT_X64"));

        string? pathEnv=Environment.GetEnvironmentVariable("PATH");
        if(!string.IsNullOrEmpty(pathEnv))
        {
            string exeName=OperatingSystem.IsWindows()?"dotnet.exe":"dotnet";
            foreach(string entry in pathEnv.Split(Path.PathSeparator,StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    if(Path.IsPathFullyQualified(entry)&&File.Exists(Path.Combine(entry,exeName)))
                        AddIfValid(entry);
                }
                catch(ArgumentException) { }
            }
        }

        string userProfile=Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if(!string.IsNullOrEmpty(userProfile))
            AddIfValid(Path.Combine(userProfile,".dotnet"));

        if(OperatingSystem.IsWindows())
            AddIfValid(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"dotnet"));
        else if(OperatingSystem.IsLinux())
        {
            AddIfValid("/usr/share/dotnet");
            AddIfValid("/usr/lib/dotnet");
            AddIfValid("/snap/dotnet-sdk/current");
        }

        return candidates.Distinct(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal).ToArray();
    }

    public static string ResolveRuntimeDirectory(string version,string? rollForward,out string resolvedVersion)
    {
        if(!SupportsNormalBuild(version))
            throw new LabException("capability_unsupported","Runtime is outside the supported CoreCLR x64 platform matrix.");

        string coreClrFile=OperatingSystem.IsWindows()?"coreclr.dll":"libcoreclr.so";
        var roots=CandidateDotnetRoots();

        // 1. Exact version match always wins first.
        foreach(string root in roots)
        {
            string exactDir=Path.Combine(root,"shared","Microsoft.NETCore.App",version);
            if(File.Exists(Path.Combine(exactDir,coreClrFile)))
            {
                resolvedVersion=version;
                return exactDir;
            }
        }

        string policy=string.IsNullOrEmpty(rollForward)?"Minor":rollForward;
        if(!policy.Equals("Disable",StringComparison.OrdinalIgnoreCase))
        {
            var reqBase=Version.Parse(version.Split('-', '+')[0]);
            bool reqPre=version.Contains('-',StringComparison.Ordinal);
            var installed=new List<(string Root,string Directory,string Version,Version Base,bool IsPre)>();

            foreach(string root in roots)
            {
                string sharedApp=Path.Combine(root,"shared","Microsoft.NETCore.App");
                if(!Directory.Exists(sharedApp)) continue;
                foreach(string dir in Directory.GetDirectories(sharedApp))
                {
                    string name=Path.GetFileName(dir);
                    if(!IsSelectableManagedVersion(name)||name==Net12Development||!File.Exists(Path.Combine(dir,coreClrFile)))
                        continue;
                    var candBase=Version.Parse(name.Split('-', '+')[0]);
                    installed.Add((root,dir,name,candBase,name.Contains('-',StringComparison.Ordinal)));
                }
            }

            static bool SatisfiesFloor(Version candBase,bool candPre,string candFull,Version reqBase,bool reqPre,string reqFull)
            {
                if(candBase>reqBase) return true;
                if(candBase<reqBase) return false;
                if(!candPre&&reqPre) return true;
                if(candPre&&!reqPre) return false;
                return string.CompareOrdinal(candFull,reqFull)>=0;
            }

            IEnumerable<(string Root,string Directory,string Version,Version Base,bool IsPre)> PickCandidates(bool allowHigherMinor,bool allowHigherMajor,bool preferLowestMajorOrMinor)
            {
                var matches=installed.Where(c=>
                {
                    if(!SatisfiesFloor(c.Base,c.IsPre,c.Version,reqBase,reqPre,version)) return false;
                    if(c.Base.Major!=reqBase.Major) return allowHigherMajor&&c.Base.Major>reqBase.Major;
                    if(c.Base.Minor!=reqBase.Minor) return allowHigherMinor&&c.Base.Minor>reqBase.Minor;
                    return true;
                }).ToArray();

                if(matches.Length==0) return [];

                // Prefer stable releases when available unless requested version is prerelease in the same major.minor;
                // if only prereleases exist (e.g., .NET 11 RC2), admit them automatically.
                var nonPre=matches.Where(c=>!c.IsPre).ToArray();
                var pool=nonPre.Length>0?nonPre:matches;

                if(preferLowestMajorOrMinor)
                {
                    int targetMajor=pool.Min(c=>c.Base.Major);
                    pool=pool.Where(c=>c.Base.Major==targetMajor).ToArray();
                    int targetMinor=pool.Min(c=>c.Base.Minor);
                    pool=pool.Where(c=>c.Base.Minor==targetMinor).ToArray();
                }

                return pool
                    .OrderByDescending(c=>c.Base)
                    .ThenByDescending(c=>!c.IsPre)
                    .ThenByDescending(c=>c.Version,StringComparer.Ordinal);
            }

            var best=policy switch
            {
                "Patch" or "LatestPatch"=>PickCandidates(false,false,true).FirstOrDefault(),
                "Minor"=>PickCandidates(true,false,true).FirstOrDefault(),
                "LatestMinor"=>PickCandidates(true,false,false).FirstOrDefault(),
                "Major"=>PickCandidates(true,true,true).FirstOrDefault(),
                "LatestMajor"=>PickCandidates(true,true,false).FirstOrDefault(),
                _=>default
            };

            if(best.Directory is not null)
            {
                resolvedVersion=best.Version;
                return best.Directory;
            }
        }

        throw new LabException("capability_unsupported",
            $"No compatible installed Microsoft.NETCore.App runtime was found for '{version}' (rollForward={policy}).");
    }

    public static string RuntimeDirectory(string version,string? rollForward="Disable")=>
        ResolveRuntimeDirectory(version,rollForward,out _);

    public static ProcessStartInfo StartInfo(string version)=>
        StartInfo(version,"Disable",out _);

    public static ProcessStartInfo StartInfo(string version,string? rollForward,out string resolvedVersion)
    {
        string runtimeDir=ResolveRuntimeDirectory(version,rollForward,out resolvedVersion);
        string executable=Path.GetFullPath(Path.Combine(runtimeDir,"..","..","..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet"));
        if(!File.Exists(executable)) throw new LabException("capability_unsupported","Runtime host executable is unavailable.");
        var start=new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true };
        AddRuntimeArguments(start,resolvedVersion);
        return start;
    }

    public static ProcessStartInfo ControllerStartInfo()
    {
        if(!ControllerSupported)
            throw new LabException("capability_unsupported","Controller requires Windows/Linux x64 and CoreCLR .NET 10.0 or later.");
        // Reuse the host that supplied the loaded runtime, including any preview/RC suffix.
        string executable=Path.GetFullPath(Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(),"..","..","..",OperatingSystem.IsWindows()?"dotnet.exe":"dotnet"));
        if(!File.Exists(executable)) throw new LabException("capability_unsupported","Controller runtime host is unavailable.");
        var start=new ProcessStartInfo(executable) { UseShellExecute=false,CreateNoWindow=true };
        AddRuntimeArguments(start);
        return start;
    }

    public static void AddRuntimeArguments(ProcessStartInfo start,string? version=null)
    {
        start.ArgumentList.Add("exec");
        start.ArgumentList.Add("--fx-version");
        start.ArgumentList.Add(version??Current);
    }

    public static string Producer(string version)=>$"coreclr-{version}/{OperatingSystemFamily}-x64/1";
}
