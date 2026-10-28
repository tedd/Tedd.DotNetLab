param(
    [string]$OutputRoot='D:\Workspaces\AI\dotnetlab-client-evaluation',
    [string]$Python='python',
    [switch]$RepositoryRegression,
    [switch]$Codex,
    [string]$Cases='sum-squares,csv,membership,byte-scan,join,deduplicate,prefix,async-cache,no-op',
    [int]$TimeoutSeconds=600,
    [int]$Repetitions=1,
    [ValidateSet('repository','installed','none')][string]$SkillMode='repository',
    [string]$Model,
    [switch]$Pregrant,
    [switch]$PreparedVariants
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($OutputRoot)
if(-not $taskRoot.StartsWith('D:\Workspaces\AI\',[StringComparison]::OrdinalIgnoreCase)) {
    throw 'Evaluation outputs must be strictly beneath D:\Workspaces\AI.'
}
for($taskAncestor=[IO.DirectoryInfo]::new($taskRoot);$null-ne $taskAncestor;$taskAncestor=$taskAncestor.Parent) {
    if($taskAncestor.Exists-and ($taskAncestor.Attributes-band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Evaluation outputs cannot traverse reparse points.'
    }
}
$taskRoot=Join-Path $taskRoot ('session-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
$taskBuild=Join-Path $taskRoot 'build'
$taskTemp=Join-Path $taskRoot 'tmp'
$taskRepo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
New-Item -ItemType Directory -Path $taskRoot,$taskTemp -Force | Out-Null
$taskOldEnvironment=@{}
foreach($taskKey in @('TEMP','TMP','PYTHONDONTWRITEBYTECODE','DOTNETLAB_TEST_BUILD_ROOT','DOTNET_ROLL_FORWARD')) {
    $taskOldEnvironment[$taskKey]=[Environment]::GetEnvironmentVariable($taskKey)
}
try {
    $env:TEMP=$taskTemp; $env:TMP=$taskTemp; $env:PYTHONDONTWRITEBYTECODE='1'
    $env:DOTNETLAB_TEST_BUILD_ROOT=$taskBuild; $env:DOTNET_ROLL_FORWARD='LatestPatch'
    $taskRuntime=& dotnet --list-runtimes | ForEach-Object {
        if($_ -match '^Microsoft.NETCore.App (10\.0\.\d+) ') { [version]$Matches[1] }
    } | Sort-Object -Descending | Select-Object -First 1
    if(-not $taskRuntime) { throw 'Install a stable .NET 10 runtime and SDK before running this fixture.' }
    if($RepositoryRegression) {
        & (Join-Path $taskRepo 'native\build-native.ps1') -BuildRoot (Join-Path $taskBuild 'native')
        if($LASTEXITCODE) { throw 'Native prerequisite build failed.' }
    }
    & dotnet build (Join-Path $taskRepo 'tests\Tedd.DotNetLab.Tests\Tedd.DotNetLab.Tests.csproj') -c Release "-p:DotNetLabBuildRoot=$taskBuild" "-p:RuntimeFrameworkVersion=$taskRuntime" -p:RestoreLockedMode=true --disable-build-servers
    if($LASTEXITCODE) { throw 'Managed build failed.' }
    & $Python -B -m unittest discover -s $PSScriptRoot -p test_evaluate.py
    if($LASTEXITCODE) { throw 'Evaluator contract tests failed.' }
    if($RepositoryRegression) {
        & dotnet test (Join-Path $taskRepo 'tests\Tedd.DotNetLab.Tests\Tedd.DotNetLab.Tests.csproj') -c Release --no-build "-p:DotNetLabBuildRoot=$taskBuild" --blame-hang-timeout 180s --blame-hang-dump-type none --logger 'trx;LogFileName=regression.trx' --results-directory (Join-Path $taskRoot 'regression') -- xUnit.ParallelizeTestCollections=false
        $taskRegressionExit=$LASTEXITCODE
    }
    $taskHost=Join-Path $taskBuild 'bin\Tedd.DotNetLab.Host\Release\net10.0\Tedd.DotNetLab.Host.dll'
    & $Python -B (Join-Path $PSScriptRoot 'evaluate.py') reference --root (Join-Path $taskRoot 'runs') --host $taskHost
    $taskReferenceExit=$LASTEXITCODE
    if($Codex) {
        $taskArguments=@('-B',(Join-Path $PSScriptRoot 'evaluate.py'),'codex','--root',(Join-Path $taskRoot 'runs'),'--host',$taskHost,'--cases',$Cases,'--timeout',$TimeoutSeconds,'--repetitions',$Repetitions,'--skill-mode',$SkillMode)
        if($Model) { $taskArguments+=@('--model',$Model) }
        if($Pregrant) { $taskArguments+='--pregrant' }
        if($PreparedVariants) { $taskArguments+='--prepared-variants' }
        & $Python @taskArguments
        $taskCodexExit=$LASTEXITCODE
    }
    Write-Output "Evaluation evidence: $taskRoot"
    if($taskReferenceExit-or $taskRegressionExit-or $taskCodexExit) { throw 'One or more evaluation layers failed; inspect retained summaries and TRX.' }
} finally {
    foreach($taskKey in $taskOldEnvironment.Keys) { [Environment]::SetEnvironmentVariable($taskKey,$taskOldEnvironment[$taskKey]) }
}
