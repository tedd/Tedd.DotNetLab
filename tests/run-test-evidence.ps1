param(
    [string]$Python='C:\Python314\python.exe',
    [string]$OutputRoot='D:\Workspaces\AI\dotnetlab-implementation\test-evidence',
    [string]$Filter,
    [switch]$Serial
)
$ErrorActionPreference='Stop'
$taskRoot=[IO.Path]::GetFullPath($OutputRoot)
if(-not $taskRoot.StartsWith('D:\Workspaces\AI\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Test evidence must be beneath D:\Workspaces\AI.' }
for($taskAncestor=[IO.DirectoryInfo]::new($taskRoot);$null-ne $taskAncestor;$taskAncestor=$taskAncestor.Parent) {
    if($taskAncestor.Exists-and ($taskAncestor.Attributes-band [IO.FileAttributes]::ReparsePoint)) { throw 'Evidence output cannot traverse reparse points.' }
}
$taskRun=Join-Path $taskRoot ('run-'+[DateTime]::UtcNow.ToString('yyyyMMddTHHmmss')+'-'+[Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $taskRun,(Join-Path $taskRun 'tmp') -Force | Out-Null
$taskOldTemp=$env:TEMP; $taskOldTmp=$env:TMP; $taskOldMetadata=$env:DOTNETLAB_TEST_METADATA_PATH; $taskOldBytecode=$env:PYTHONDONTWRITEBYTECODE
try {
    $env:TEMP=Join-Path $taskRun 'tmp'; $env:TMP=$env:TEMP; $env:DOTNETLAB_TEST_METADATA_PATH=Join-Path $taskRun 'declarations.json'; $env:PYTHONDONTWRITEBYTECODE='1'
    $taskArguments=@('test',(Join-Path $PSScriptRoot 'Tedd.DotNetLab.Tests\Tedd.DotNetLab.Tests.csproj'),'-c','Release','--no-build','--logger','trx;LogFileName=results.trx','--results-directory',$taskRun)
    if($Filter) { $taskArguments+=@('--filter',$Filter) }
    $taskScheduling=if($Serial) { 'serial_test_collections' } else { 'runner_default_test_collections' }
    @{ test_collection_scheduling=$taskScheduling; filter=$Filter } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $taskRun 'run-configuration.json')
    if($Serial) { $taskArguments+=@('--','xUnit.ParallelizeTestCollections=false') }
    & dotnet @taskArguments; $taskTestExit=$LASTEXITCODE
    & $Python (Join-Path $PSScriptRoot 'test_evidence_audit.py') --spec (Join-Path $PSScriptRoot '..\02-functional-system-specification.md') --test-plan (Join-Path $PSScriptRoot '..\03-unit-test-plan.md') --declarations $env:DOTNETLAB_TEST_METADATA_PATH --trx (Join-Path $taskRun 'results.trx') --output (Join-Path $taskRun 'requirement-evidence.json')
    if($LASTEXITCODE) { throw "Evidence generation failed. Preserved run: $taskRun" }
    Write-Output "Requirement evidence: $(Join-Path $taskRun 'requirement-evidence.json')"
    if($taskTestExit) { throw "Test execution failed. Preserved evidence: $taskRun" }
} finally {
    $env:TEMP=$taskOldTemp; $env:TMP=$taskOldTmp; $env:DOTNETLAB_TEST_METADATA_PATH=$taskOldMetadata; $env:PYTHONDONTWRITEBYTECODE=$taskOldBytecode
}
