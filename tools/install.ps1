<#
.SYNOPSIS
    Installs Tedd.DotNetLab and configures Codex, Claude Code, Cursor, and/or Google Antigravity (MCP server + agent skill).

.DESCRIPTION
    Supports installing from a GitHub Release archive (default, falling back to source build if no release is published yet)
    or building directly from source (-FromSource). Can be invoked locally or via one-liner:
      irm https://raw.githubusercontent.com/tedd/Tedd.DotNetLab/main/tools/install.ps1 | iex

.PARAMETER Client
    AI client(s) to configure: 'all' (default), 'codex', 'claude', 'cursor', 'antigravity' ('agy'), comma-separated list (e.g. 'cursor,antigravity'), or 'none' (binaries + CLI shim only).

.PARAMETER Scope
    Configuration scope: 'user' (default) or 'project'.

.PARAMETER ProjectDir
    Target project directory when -Scope project is used (defaults to current directory).

.PARAMETER InstallDir
    Destination directory for binaries (default: %LOCALAPPDATA%\Tedd.DotNetLab\bin).

.PARAMETER Workspace
    Tedd.DotNetLab catalog/artifact workspace path.

.PARAMETER Repo
    GitHub repository slug (default: tedd/Tedd.DotNetLab).

.PARAMETER Version
    Release tag to download (default: latest).

.PARAMETER FromSource
    Build and publish from local checkout (if run inside repo) or a shallow Git clone instead of downloading a release.

.PARAMETER EnableNativeExtension
    Also expose the optional lab.patch native CIL/ReJIT MCP tool (--enable-native-extension).

.PARAMETER SkipPath
    Do not add the install directory (containing dotnetlab.cmd) to the user PATH.
#>
param(
    [string]$Client = $(if ($env:DOTNETLAB_CLIENT) { $env:DOTNETLAB_CLIENT } else { 'all' }),
    [ValidateSet('user', 'project')]
    [string]$Scope = $(if ($env:DOTNETLAB_SCOPE) { $env:DOTNETLAB_SCOPE } else { 'user' }),
    [string]$ProjectDir = $(if ($env:DOTNETLAB_PROJECT_DIR) { $env:DOTNETLAB_PROJECT_DIR } else { '' }),
    [string]$InstallDir = $(if ($env:DOTNETLAB_INSTALL_DIR) { $env:DOTNETLAB_INSTALL_DIR } else { '' }),
    [string]$Workspace = $(if ($env:DOTNETLAB_WORKSPACE) { $env:DOTNETLAB_WORKSPACE } else { '' }),
    [string]$Repo = $(if ($env:DOTNETLAB_REPO) { $env:DOTNETLAB_REPO } else { 'tedd/Tedd.DotNetLab' }),
    [string]$Version = $(if ($env:DOTNETLAB_VERSION) { $env:DOTNETLAB_VERSION } else { 'latest' }),
    [string]$CodexHome = $(if ($env:DOTNETLAB_CODEX_HOME) { $env:DOTNETLAB_CODEX_HOME } else { '' }),
    [string]$ClaudeHome = $(if ($env:DOTNETLAB_CLAUDE_HOME) { $env:DOTNETLAB_CLAUDE_HOME } else { '' }),
    [string]$ClaudeConfig = $(if ($env:DOTNETLAB_CLAUDE_CONFIG) { $env:DOTNETLAB_CLAUDE_CONFIG } else { '' }),
    [string]$CursorHome = $(if ($env:DOTNETLAB_CURSOR_HOME) { $env:DOTNETLAB_CURSOR_HOME } else { '' }),
    [string]$AntigravityHome = $(if ($env:DOTNETLAB_ANTIGRAVITY_HOME) { $env:DOTNETLAB_ANTIGRAVITY_HOME } else { '' }),
    [switch]$FromSource = ($env:DOTNETLAB_FROM_SOURCE -eq '1' -or $env:DOTNETLAB_FROM_SOURCE -eq 'true'),
    [switch]$EnableNativeExtension = ($env:DOTNETLAB_ENABLE_NATIVE -eq '1' -or $env:DOTNETLAB_ENABLE_NATIVE -eq 'true'),
    [switch]$SkipPath = ($env:DOTNETLAB_SKIP_PATH -eq '1' -or $env:DOTNETLAB_SKIP_PATH -eq 'true')
)

$ErrorActionPreference = 'Stop'
$isWindowsHost = ($PSVersionTable.PSEdition -eq 'Desktop') -or ($IsWindows -eq $true)
if (-not $isWindowsHost) {
    throw 'install.ps1 targets Windows x64. On Linux/WSL, use tools/install.sh.'
}
if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString() -ne 'X64') {
    throw 'Tedd.DotNetLab requires an x64 host architecture.'
}

function Write-Step([string]$Message) {
    Write-Host "[Tedd.DotNetLab] $Message" -ForegroundColor Cyan
}

$dotnetCmd = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnetCmd) {
    throw '.NET 10.0 or later (including .NET 11/12 previews & RCs) is required to host Tedd.DotNetLab, but `dotnet` was not found on PATH. Install .NET from https://dotnet.microsoft.com/download'
}
$dotnetExe = $dotnetCmd.Source

$runtimes = & $dotnetExe --list-runtimes 2>$null
$hasNet10Plus = $false
foreach ($line in $runtimes) {
    if ($line -match '^Microsoft\.NETCore\.App\s+(\d+)\.') {
        if ([int]$Matches[1] -ge 10) {
            $hasNet10Plus = $true
            break
        }
    }
}
if (-not $hasNet10Plus) {
    throw 'Tedd.DotNetLab controller requires Microsoft.NETCore.App 10.0 or later (any stable, RC, or preview release; target apps on .NET 6+ are supported). Run `dotnet --list-runtimes` to check installed runtimes.'
}

if (-not $InstallDir) {
    $InstallDir = Join-Path $env:LOCALAPPDATA 'Tedd.DotNetLab\bin'
}
$InstallDir = [System.IO.Path]::GetFullPath($InstallDir)
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null

function Get-ScratchRoot {
    $dir = Join-Path ([System.IO.Path]::GetDirectoryName($InstallDir)) ('.install-staging-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $dir -Force | Out-Null
    return $dir
}

function Install-FromRelease {
    $apiUrl = if ($Version -eq 'latest') {
        "https://api.github.com/repos/$Repo/releases/latest"
    } else {
        "https://api.github.com/repos/$Repo/releases/tags/$Version"
    }
    Write-Step "Checking GitHub Releases ($Repo @ $Version)..."
    $headers = @{ 'User-Agent' = 'Tedd.DotNetLab-Installer/1.0'; 'Accept' = 'application/vnd.github+json' }
    try {
        $release = Invoke-RestMethod -Uri $apiUrl -Headers $headers -Method Get
    } catch {
        if ($Version -eq 'latest' -and $_.Exception.Response -and [int]$_.Exception.Response.StatusCode -eq 404) {
            Write-Host "[Tedd.DotNetLab] No published GitHub Release found yet; falling back to source build..." -ForegroundColor Yellow
            return $false
        }
        throw
    }

    $asset = $release.assets | Where-Object { $_.name -match 'windows-x64\.zip$' } | Select-Object -First 1
    if (-not $asset) {
        throw "Release '$($release.tag_name)' does not contain a *-windows-x64.zip package asset."
    }

    $scratch = Get-ScratchRoot
    try {
        $zipPath = Join-Path $scratch $asset.name
        Write-Step "Downloading $($asset.name)..."
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $zipPath -Headers @{ 'User-Agent' = 'Tedd.DotNetLab-Installer/1.0' }

        $actualHash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        $expectedHash = $null
        if ($asset.PSObject.Properties['digest'] -and $asset.digest -match '^sha256:([0-9a-fA-F]{64})$') {
            $expectedHash = $Matches[1].ToLowerInvariant()
        } else {
            $sumsAsset = $release.assets | Where-Object { $_.name -eq 'SHA256SUMS.txt' } | Select-Object -First 1
            if ($sumsAsset) {
                $sumsPath = Join-Path $scratch 'SHA256SUMS.txt'
                Invoke-WebRequest -Uri $sumsAsset.browser_download_url -OutFile $sumsPath -Headers @{ 'User-Agent' = 'Tedd.DotNetLab-Installer/1.0' }
                foreach ($line in Get-Content -LiteralPath $sumsPath) {
                    if ($line -match '^([0-9a-fA-F]{64})\s+\*?(.+)$' -and $Matches[2].Trim() -eq $asset.name) {
                        $expectedHash = $Matches[1].ToLowerInvariant()
                        break
                    }
                }
            }
        }
        if ($expectedHash -and $actualHash -ne $expectedHash) {
            throw "SHA-256 checksum mismatch for $($asset.name). Expected $expectedHash, got $actualHash."
        }

        $extractDir = Join-Path $scratch 'extracted'
        Expand-Archive -LiteralPath $zipPath -DestinationPath $extractDir -Force
        Copy-Item -Path (Join-Path $extractDir '*') -Destination $InstallDir -Recurse -Force
        return $true
    } finally {
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

function Install-FromSourceBuild {
    $sdks = & $dotnetExe --list-sdks 2>$null
    $hasSdk10Plus = $false
    foreach ($sdkLine in $sdks) {
        if ($sdkLine -match '^(\d+)\.' -and [int]$Matches[1] -ge 10) {
            $hasSdk10Plus = $true
            break
        }
    }
    if (-not $hasSdk10Plus) {
        throw 'Building from source requires a .NET 10 or newer SDK (including .NET 11/12 preview or RC SDKs). Install from https://dotnet.microsoft.com/download'
    }

    $localRepo = $null
    if ($PSScriptRoot) {
        $candidate = Split-Path -Parent $PSScriptRoot
        if (Test-Path -LiteralPath (Join-Path $candidate 'src\Tedd.DotNetLab.Host\Tedd.DotNetLab.Host.csproj')) {
            $localRepo = $candidate
        }
    }
    if (-not $localRepo -and (Test-Path -LiteralPath (Join-Path (Get-Location).Path 'src\Tedd.DotNetLab.Host\Tedd.DotNetLab.Host.csproj'))) {
        $localRepo = (Get-Location).Path
    }

    $scratch = Get-ScratchRoot
    try {
        $sourceRoot = $localRepo
        if (-not $sourceRoot) {
            $gitCmd = Get-Command git -ErrorAction SilentlyContinue
            if (-not $gitCmd) { throw 'git is required to clone Tedd.DotNetLab when installing from source outside a repository checkout.' }
            $sourceRoot = Join-Path $scratch 'repo'
            Write-Step "Cloning https://github.com/$Repo.git ..."
            & $gitCmd.Source clone --depth 1 "https://github.com/$Repo.git" $sourceRoot
            if ($LASTEXITCODE -ne 0) { throw 'git clone failed.' }
        } else {
            Write-Step "Building from local checkout at $sourceRoot ..."
        }

        $buildRoot = Join-Path $scratch 'build'
        $nativeScript = Join-Path $sourceRoot 'native\build-native.ps1'
        if (Test-Path -LiteralPath $nativeScript) {
            try {
                Write-Step 'Attempting optional native C++ profiler build...'
                & $nativeScript -BuildRoot (Join-Path $buildRoot 'native') | Out-Host
            } catch {
                Write-Host "[Tedd.DotNetLab] Native C++ toolchain not available ($($_.Exception.Message)); continuing with full managed profiling & benchmarking support." -ForegroundColor Yellow
            }
        }

        $hostProj = Join-Path $sourceRoot 'src\Tedd.DotNetLab.Host\Tedd.DotNetLab.Host.csproj'
        $publishStaging = Join-Path $scratch 'publish'
        Write-Step 'Restoring and publishing Tedd.DotNetLab.Host...'
        & $dotnetExe restore $hostProj --locked-mode "-p:DotNetLabBuildRoot=$buildRoot"
        if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
        & $dotnetExe publish $hostProj -c Release --no-restore --output $publishStaging "-p:DotNetLabBuildRoot=$buildRoot" -p:UseAppHost=false -p:SelfContained=false -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false
        if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

        Copy-Item -Path (Join-Path $publishStaging '*') -Destination $InstallDir -Recurse -Force
    } finally {
        if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($FromSource) {
    Install-FromSourceBuild
} else {
    $installedRelease = Install-FromRelease
    if (-not $installedRelease) {
        Install-FromSourceBuild
    }
}

$hostDll = Join-Path $InstallDir 'Tedd.DotNetLab.Host.dll'
if (-not (Test-Path -LiteralPath $hostDll)) {
    throw "Installation failed: expected host assembly at '$hostDll'."
}

$shimPath = Join-Path $InstallDir 'dotnetlab.cmd'
$shimContent = "@echo off`r`nsetlocal`r`nset `"DOTNET_ROLL_FORWARD_TO_PRERELEASE=1`"`r`n`"$dotnetExe`" --roll-forward Major `"%~dp0Tedd.DotNetLab.Host.dll`" %*`r`n"
[System.IO.File]::WriteAllText($shimPath, $shimContent, [System.Text.UTF8Encoding]::new($false))

if (-not $SkipPath) {
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    $entries = if ($userPath) { $userPath -split ';' | Where-Object { $_ } } else { @() }
    if (-not ($entries | Where-Object { $_.TrimEnd('\') -ieq $InstallDir.TrimEnd('\') })) {
        $newUserPath = if ($userPath) { "$($userPath.TrimEnd(';'));$InstallDir" } else { $InstallDir }
        [Environment]::SetEnvironmentVariable('Path', $newUserPath, 'User')
        $env:Path = "$env:Path;$InstallDir"
        Write-Step "Added '$InstallDir' (dotnetlab.cmd) to your User PATH."
    }
}

if ($Client -ne 'none') {
    Write-Step "Configuring AI client(s): $Client (scope: $Scope)..."
    $installArgs = @('--roll-forward', 'Major', $hostDll, 'install', $Client, '--scope', $Scope, '--dotnet', $dotnetExe)
    if ($Workspace) { $installArgs += @('--workspace', $Workspace) }
    if ($ProjectDir) { $installArgs += @('--project-dir', $ProjectDir) }
    if ($CodexHome) { $installArgs += @('--codex-home', $CodexHome) }
    if ($ClaudeHome) { $installArgs += @('--claude-home', $ClaudeHome) }
    if ($ClaudeConfig) { $installArgs += @('--claude-config', $ClaudeConfig) }
    if ($CursorHome) { $installArgs += @('--cursor-home', $CursorHome) }
    if ($AntigravityHome) { $installArgs += @('--antigravity-home', $AntigravityHome) }
    if ($EnableNativeExtension) { $installArgs += '--enable-native-extension' }

    $prevRollPre = $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE
    try {
        $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = '1'
        $jsonOut = & $dotnetExe @installArgs
        if ($LASTEXITCODE -ne 0) {
            throw "dotnetlab install failed with exit code $LASTEXITCODE."
        }
        Write-Output $jsonOut
    } finally {
        $env:DOTNET_ROLL_FORWARD_TO_PRERELEASE = $prevRollPre
    }
}

Write-Step 'Installation complete! Restart Codex / Claude Code / Cursor / Antigravity so the new MCP server and `dotnetlab` skill load.'
