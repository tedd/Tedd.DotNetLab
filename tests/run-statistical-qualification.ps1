param(
    [string]$Python='C:\Python314\python.exe',
    [string]$OutputRoot='D:\Workspaces\AI\dotnetlab-implementation\statistical-qualification',
    [string]$Design
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($OutputRoot)
if(-not $taskRoot.StartsWith('D:\Workspaces\AI\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Statistical outputs must be beneath D:\Workspaces\AI.' }
for($taskAncestor=[IO.DirectoryInfo]::new($taskRoot);$null-ne $taskAncestor;$taskAncestor=$taskAncestor.Parent) {
    if($taskAncestor.Exists-and ($taskAncestor.Attributes-band [IO.FileAttributes]::ReparsePoint)) { throw 'Output ancestors cannot be reparse points.' }
}
$taskRunRoot=Join-Path $taskRoot ('run-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRunRoot,(Join-Path $taskRunRoot 'tmp') -Force | Out-Null
$taskOldTemp=$env:TEMP; $taskOldTmp=$env:TMP; $taskOldBytecode=$env:PYTHONDONTWRITEBYTECODE
try {
    $env:TEMP=Join-Path $taskRunRoot 'tmp'; $env:TMP=$env:TEMP; $env:PYTHONDONTWRITEBYTECODE='1'
    $taskPlan=Join-Path $PSScriptRoot 'Fixtures\statistics\simulation-plan.json'
    $taskOracle=Join-Path $taskRunRoot 'oracle.json'; $taskReport=Join-Path $taskRunRoot 'report.json'
    $taskPythonArguments=@((Join-Path $PSScriptRoot 'Fixtures\statistics\simulation_reference.py'),'--plan',$taskPlan,'--output',$taskOracle)
    $taskRunnerArguments=@('run','--project',(Join-Path $PSScriptRoot 'Tedd.DotNetLab.StatisticalQualification\Tedd.DotNetLab.StatisticalQualification.csproj'),
        '-c','Release','--no-build','--','--plan',$taskPlan,'--oracle',$taskOracle,'--output',$taskReport)
    if($Design) { $taskPythonArguments+=@('--design',$Design); $taskRunnerArguments+=@('--design',$Design) }
    & $Python @taskPythonArguments
    if($LASTEXITCODE) { throw "Independent oracle failed. Preserved outputs: $taskRunRoot" }
    & dotnet @taskRunnerArguments
    if($LASTEXITCODE) { throw "Statistical qualification failed. Preserved outputs: $taskRunRoot" }
    Write-Output "Qualification report: $taskReport"
} finally {
    $env:TEMP=$taskOldTemp; $env:TMP=$taskOldTmp; $env:PYTHONDONTWRITEBYTECODE=$taskOldBytecode
}
