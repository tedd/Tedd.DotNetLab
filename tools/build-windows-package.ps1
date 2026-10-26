param(
    [string]$Python=$(if(Test-Path 'C:\Python314\python.exe'){'C:\Python314\python.exe'}else{'python'}),
    [string]$OutputRoot=$(if(Test-Path 'D:\Workspaces\AI'){'D:\Workspaces\AI\dotnetlab-package'}else{Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\package'}),
    [string]$BuildRoot=$(if(Test-Path 'D:\Workspaces\AI'){'D:\Workspaces\AI\dotnetlab-implementation'}else{Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts'})
)
$ErrorActionPreference='Stop'
if(-not $IsWindows -or [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture -ne 'X64') {
    throw 'The Windows package requires a Windows x64 build host.'
}
function Test-TaskPath([string]$Path) {
    $taskAbsolute=[IO.Path]::GetFullPath($Path)
    if((Test-Path 'D:\Workspaces\AI') -and -not $taskAbsolute.StartsWith('D:\Workspaces\AI\',[StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package and build output must be beneath D:\Workspaces\AI.'
    }
    if([IO.Path]::GetPathRoot($taskAbsolute) -eq $taskAbsolute) {
        throw 'Package and build output cannot use a filesystem root.'
    }
    for($taskAncestor=[IO.DirectoryInfo]::new($taskAbsolute);$null-ne $taskAncestor;$taskAncestor=$taskAncestor.Parent) {
        if($taskAncestor.Exists-and ($taskAncestor.Attributes-band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Package paths cannot traverse reparse points.'
        }
    }
    return $taskAbsolute
}
$taskOutput=Test-TaskPath $OutputRoot
$taskBuild=Test-TaskPath $BuildRoot
$taskRepo=Split-Path -Parent $PSScriptRoot
Push-Location -LiteralPath $taskRepo
$taskOldTemp=$env:TEMP; $taskOldTmp=$env:TMP; $taskOldBytecode=$env:PYTHONDONTWRITEBYTECODE
try {
    # Every packaged source, pin, and notice must match the recorded revision.
    $taskDirty=& git status --porcelain --untracked-files=all -- src native tools docs schemas skills LICENSE README.md SECURITY.md Directory.Build.props Directory.Packages.props global.json NuGet.Config
    if($LASTEXITCODE -or $taskDirty) { throw 'Commit package inputs before building a versioned distribution.' }
    $taskRevision=& git rev-parse HEAD
    if($LASTEXITCODE -or $taskRevision -notmatch '^[0-9a-f]{40}$') { throw 'Cannot resolve the source revision.' }
    $taskSdk=((& dotnet --version) | Select-Object -First 1).Trim()
    if($LASTEXITCODE -or $taskSdk -notmatch '^(\d+)\.\d+\.\d+' -or [int]$Matches[1] -lt 10) {
        throw 'Package build requires a .NET 10 or newer SDK.'
    }
    $taskRun=Join-Path $taskOutput ('build-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $taskRun,(Join-Path $taskRun 'tmp'),(Join-Path $taskRun 'publish') -Force | Out-Null
    $env:TEMP=Join-Path $taskRun 'tmp'; $env:TMP=$env:TEMP; $env:PYTHONDONTWRITEBYTECODE='1'
    $taskPublish=Join-Path $taskRun 'publish'
    & (Join-Path $taskRepo 'native\build-native.ps1') -BuildRoot (Join-Path $taskBuild 'native')
    # Generation only: these projects have no database-changing build targets.
    # Locked restore prevents a dependency update; native compilation is separate.
    & dotnet restore src/Tedd.DotNetLab.Host/Tedd.DotNetLab.Host.csproj --locked-mode "-p:DotNetLabBuildRoot=$taskBuild"
    if($LASTEXITCODE) { throw 'Locked restore failed.' }
    & dotnet publish src/Tedd.DotNetLab.Host/Tedd.DotNetLab.Host.csproj -c Release --no-restore --output $taskPublish "-p:DotNetLabBuildRoot=$taskBuild" -p:UseAppHost=false -p:SelfContained=false -p:PublishSingleFile=false -p:PublishTrimmed=false -p:PublishReadyToRun=false
    if($LASTEXITCODE) { throw 'Host publication failed.' }
    & dotnet build tools/Tedd.DotNetLab.PackageHashes/Tedd.DotNetLab.PackageHashes.csproj -c Release "-p:DotNetLabBuildRoot=$taskBuild"
    if($LASTEXITCODE) { throw 'SDK package-hash worker build failed.' }
    $taskAssets=Join-Path $taskBuild 'obj\Tedd.DotNetLab.Host\project.assets.json'
    $taskNugetHashes=& dotnet (Join-Path $taskBuild 'bin\Tedd.DotNetLab.PackageHashes\Release\net10.0\Tedd.DotNetLab.PackageHashes.dll') $taskAssets
    if($LASTEXITCODE) { throw 'Locked NuGet archive integrity verification failed.' }
    $taskHashPath=Join-Path $taskRun 'nuget-hashes.json'
    $taskNugetHashes | Set-Content -LiteralPath $taskHashPath -Encoding utf8NoBOM
    $taskSigning=[ordered]@{}
    Get-ChildItem -LiteralPath $taskPublish -File -Recurse | Where-Object { $_.Extension -in '.dll','.exe' } | Sort-Object FullName | ForEach-Object {
        $taskSignature=Get-AuthenticodeSignature -LiteralPath $_.FullName
        if($taskSignature.Status -eq 'HashMismatch') { throw 'A published binary has an invalid Authenticode content hash.' }
        $taskName=[IO.Path]::GetRelativePath($taskPublish,$_.FullName).Replace('\','/')
        $taskSigning[$taskName]=@{ status=$taskSignature.Status.ToString(); signer_thumbprint=$taskSignature.SignerCertificate.Thumbprint; sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    }
    $taskSigningPath=Join-Path $taskRun 'signing.json'
    $taskSigning | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $taskSigningPath -Encoding utf8NoBOM
    $taskArchive=Join-Path $taskRun 'Tedd.DotNetLab-1.0.0-windows-x64.zip'
    $taskResult=& $Python (Join-Path $PSScriptRoot 'windows_package.py') build --repo $taskRepo --publish $taskPublish --assets $taskAssets --output $taskArchive --revision $taskRevision --signing $taskSigningPath --nuget-hashes $taskHashPath --sdk $taskSdk
    if($LASTEXITCODE) { throw "Package verification failed. Preserved build: $taskRun" }
    $taskResult | Set-Content -LiteralPath (Join-Path $taskRun 'package-result.json') -Encoding utf8NoBOM
    Write-Output $taskResult
} finally {
    $env:TEMP=$taskOldTemp; $env:TMP=$taskOldTmp; $env:PYTHONDONTWRITEBYTECODE=$taskOldBytecode
    Pop-Location
}
