param([string]$BuildRoot=$(if(Test-Path 'D:\Workspaces\AI'){'D:\Workspaces\AI\dotnetlab-implementation\native'}else{Join-Path (Split-Path -Parent $PSScriptRoot) 'artifacts\native'}))
$ErrorActionPreference='Stop'
$taskEnvironment=[System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
Get-ChildItem Env: | ForEach-Object { $taskEnvironment[$_.Name]=$_.Value }
try {
$taskBuildRoot=[IO.Path]::GetFullPath($BuildRoot)
if((Test-Path 'D:\Workspaces\AI') -and -not $taskBuildRoot.StartsWith('D:\Workspaces\AI\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Native scratch build must be beneath D:\Workspaces\AI.' }
New-Item -ItemType Directory -Path $taskBuildRoot,(Join-Path $taskBuildRoot 'tmp') -Force | Out-Null
$env:TEMP=Join-Path $taskBuildRoot 'tmp'
$env:TMP=$env:TEMP
$taskVswhere='C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$taskVsRoot=& $taskVswhere -latest -prerelease -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if(-not $taskVsRoot) { throw 'No qualified C++ toolchain found (including prerelease Visual Studio).' }
& (Join-Path $taskVsRoot 'Common7\Tools\Launch-VsDevShell.ps1') -Arch amd64 -HostArch amd64 -SkipAutomaticLocation
$taskSdkRoot='C:\Program Files (x86)\Windows Kits\10'
$taskSdkVersion=Get-ChildItem -LiteralPath (Join-Path $taskSdkRoot 'Lib') -Directory | Where-Object {
    (Test-Path (Join-Path $_.FullName 'um\x64\kernel32.lib')) -and (Test-Path (Join-Path $_.FullName 'ucrt\x64\ucrt.lib'))
} | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty Name
if(-not $taskSdkVersion) { throw 'No complete Windows x64 SDK libraries found.' }
$env:LIB=(Join-Path $taskSdkRoot "Lib\$taskSdkVersion\um\x64")+';'+(Join-Path $taskSdkRoot "Lib\$taskSdkVersion\ucrt\x64")+';'+$env:LIB
$env:INCLUDE=(Join-Path $taskSdkRoot "Include\$taskSdkVersion\shared")+';'+(Join-Path $taskSdkRoot "Include\$taskSdkVersion\um")+';'+(Join-Path $taskSdkRoot "Include\$taskSdkVersion\ucrt")+';'+$env:INCLUDE
Write-Output "Native SDK: $taskSdkVersion"
$taskCmake=Join-Path $taskVsRoot 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$taskCompiler=Join-Path $taskVsRoot 'VC\Tools\Llvm\x64\bin\clang-cl.exe'
$taskCache=Join-Path $taskBuildRoot 'CMakeCache.txt'
if(Test-Path -LiteralPath $taskCache) {
    $taskExpected=('CMAKE_HOME_DIRECTORY:INTERNAL='+($PSScriptRoot.Replace('\','/')))
    if(-not (Select-String -LiteralPath $taskCache -SimpleMatch $taskExpected -Quiet)) {
        Remove-Item -LiteralPath $taskCache -Force
        $taskCmakeFiles=Join-Path $taskBuildRoot 'CMakeFiles'
        if(Test-Path -LiteralPath $taskCmakeFiles) { Remove-Item -LiteralPath $taskCmakeFiles -Recurse -Force }
    }
}
& $taskCmake -S $PSScriptRoot -B $taskBuildRoot -G Ninja "-DCMAKE_CXX_COMPILER=$taskCompiler" -DCMAKE_BUILD_TYPE=Release
if($LASTEXITCODE) { throw "Native configure failed: $LASTEXITCODE" }
& $taskCmake --build $taskBuildRoot
if($LASTEXITCODE) { throw "Native build failed: $LASTEXITCODE" }
& $taskCmake --build $taskBuildRoot --target test
if($LASTEXITCODE) { throw "Native tests failed: $LASTEXITCODE" }
} finally {
    Get-ChildItem Env: | Where-Object { -not $taskEnvironment.ContainsKey($_.Name) } | ForEach-Object { Remove-Item -LiteralPath ('Env:'+ $_.Name) }
    foreach($taskEnvironmentKey in $taskEnvironment.Keys) { Set-Item -LiteralPath ('Env:'+ $taskEnvironmentKey) -Value $taskEnvironment[$taskEnvironmentKey] }
}
