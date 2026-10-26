param(
    [string]$Sdk10Dotnet = 'dotnet',
    [string]$Sdk11MsBuild = '',
    [string]$RuntimeRoot = '',
    [string]$Net10Version = '',
    [string]$Net11Version = '',
    [string]$BuildRoot = $(if (Test-Path 'D:\Workspaces\AI') { 'D:\Workspaces\AI\dotnetlab-dual-runtime-build' } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\dual-runtime-build' }),
    [string]$Filter
)
$ErrorActionPreference = 'Stop'
$build = [IO.Path]::GetFullPath($BuildRoot)
if ((Test-Path 'D:\Workspaces\AI') -and -not $build.StartsWith('D:\Workspaces\AI\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Qualification output must be beneath the designated AI workspace.'
}
for ($ancestor = [IO.DirectoryInfo]::new($build); $null -ne $ancestor; $ancestor = $ancestor.Parent) {
    if ($ancestor.Exists -and ($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Qualification output cannot traverse reparse points.'
    }
}
$repo = Split-Path -Parent $PSScriptRoot
$priorEnvironment = [Collections.Generic.Dictionary[string, string]]::new([StringComparer]::OrdinalIgnoreCase)
Get-ChildItem Env: | ForEach-Object { $priorEnvironment[$_.Name] = $_.Value }
Push-Location -LiteralPath $repo
try {
    New-Item -ItemType Directory -Path $build, (Join-Path $build 'tmp'), (Join-Path $build 'results') -Force | Out-Null
    $env:TEMP = Join-Path $build 'tmp'; $env:TMP = $env:TEMP
    $env:DOTNET_CLI_HOME = Join-Path $build 'cli-home'; $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNETLAB_TEST_BUILD_ROOT = $build; $env:PYTHONDONTWRITEBYTECODE = '1'

    $dotnetCmd = (Get-Command $Sdk10Dotnet -ErrorAction Stop).Source
    $dotnetDir = if ($RuntimeRoot) { [IO.Path]::GetFullPath($RuntimeRoot) } else { Split-Path -Parent $dotnetCmd }
    $env:DOTNET_ROOT = $dotnetDir; $env:DOTNET_ROOT_X64 = $dotnetDir
    $runtimeHost = Join-Path $dotnetDir 'dotnet.exe'

    $sdkVersion = (& $dotnetCmd --version).Trim()
    $installedRuntimes = & $runtimeHost --list-runtimes | Where-Object { $_ -match '^Microsoft\.NETCore\.App\s+(\S+)\s+' } | ForEach-Object { $Matches[1] }

    if (-not $Net10Version) {
        $Net10Version = $installedRuntimes | Where-Object { $_.StartsWith('10.', [StringComparison]::Ordinal) } | Select-Object -Last 1
    }
    if (-not $Net11Version) {
        $Net11Version = $installedRuntimes | Where-Object { $_.StartsWith('11.', [StringComparison]::Ordinal) } | Select-Object -Last 1
    }
    foreach ($version in @($Net10Version, $Net11Version)) {
        if (-not $version -or -not ($installedRuntimes -contains $version)) {
            throw "Missing required installed .NET 10 / .NET 11 runtime (found: $($installedRuntimes -join ', '))."
        }
    }

    if (-not $Sdk11MsBuild) {
        $sdk11Dir = Get-ChildItem -LiteralPath (Join-Path $dotnetDir 'sdk') -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name.StartsWith('11.', [StringComparison]::Ordinal) } |
            Select-Object -Last 1
        if ($sdk11Dir) { $Sdk11MsBuild = Join-Path $sdk11Dir.FullName 'MSBuild.dll' }
    }
    if (-not $Sdk11MsBuild -or -not (Test-Path -LiteralPath $Sdk11MsBuild)) {
        throw 'Missing .NET 11 SDK MSBuild.dll. Pass -Sdk11MsBuild explicitly or install a .NET 11 SDK.'
    }

    & (Join-Path $repo 'native\build-native.ps1') -BuildRoot (Join-Path $build 'native')
    & $dotnetCmd restore Tedd.DotNetLab.sln --locked-mode "-p:DotNetLabBuildRoot=$build" -v:quiet
    if ($LASTEXITCODE) { throw 'Locked restore failed.' }
    & $dotnetCmd build Tedd.DotNetLab.sln -c Release --no-restore "-p:DotNetLabBuildRoot=$build" -p:UseSharedCompilation=false -nodeReuse:false -v:quiet
    if ($LASTEXITCODE) { throw 'Solution build failed.' }

    foreach ($project in @('Tedd.DotNetLab.Net11Workload', 'Tedd.DotNetLab.NormalBaseline', 'Tedd.DotNetLab.NormalCandidate')) {
        & $runtimeHost exec --fx-version $Net11Version $Sdk11MsBuild (Join-Path $PSScriptRoot "$project\$project.csproj") -restore -t:Build -p:Configuration=Release "-p:DotNetLabBuildRoot=$build" -p:DotNetLabNet11Qualification=true -p:BuildProjectReferences=false -p:UseSharedCompilation=false -nodeReuse:false -v:quiet
        if ($LASTEXITCODE) { throw ('NET11 fixture build failed: ' + $project) }
    }

    $sdkRoot = Join-Path $dotnetDir "sdk\$sdkVersion"
    $assembly = Join-Path $build 'bin\Tedd.DotNetLab.Tests\Release\net10.0\Tedd.DotNetLab.Tests.dll'
    foreach ($version in @($Net10Version, $Net11Version)) {
        $env:DOTNET_ROLL_FORWARD = 'Major'
        $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = '1'
        $arguments = @('exec', '--fx-version', $version, (Join-Path $sdkRoot 'vstest.console.dll'), $assembly, "/ResultsDirectory:$(Join-Path $build 'results')", "/Logger:trx;LogFileName=net-$version.trx")
        if ($Filter) { $arguments += '/TestCaseFilter:' + $Filter }
        $arguments += @('--', 'xUnit.ParallelizeTestCollections=false')
        & $runtimeHost @arguments
        if ($LASTEXITCODE) { throw ('Runtime qualification failed: ' + $version) }
        & (Join-Path $PSScriptRoot 'test-mcp-connection.ps1') -Dotnet $runtimeHost -HostAssembly (Join-Path $build 'bin\Tedd.DotNetLab.Host\Release\net10.0\Tedd.DotNetLab.Host.dll') -RuntimeVersion $version -ScratchRoot (Join-Path $build 'mcp-tests')
    }
} finally {
    Pop-Location
    Get-ChildItem Env: | Where-Object { -not $priorEnvironment.ContainsKey($_.Name) } | ForEach-Object { Remove-Item -LiteralPath ('Env:' + $_.Name) }
    foreach ($key in $priorEnvironment.Keys) { Set-Item -LiteralPath ('Env:' + $key) -Value $priorEnvironment[$key] }
}
