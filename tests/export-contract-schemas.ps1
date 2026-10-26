param([string]$OutputRoot=(Join-Path $PSScriptRoot '..\schemas\api\v1'))
$ErrorActionPreference='Stop'
$taskDestination=[IO.Path]::GetFullPath($OutputRoot)
$taskHost=Join-Path 'D:\Workspaces\AI\dotnetlab-implementation\bin\Tedd.DotNetLab.Host\Release\net10.0' 'Tedd.DotNetLab.Host.dll'
$taskSchemas=@{}
foreach($taskSchemaName in @('plan','measurement','capability','comparison','error')) {
    $taskSchemaText=(& dotnet $taskHost schema $taskSchemaName | Out-String).Trim()
    if($LASTEXITCODE) { throw "Schema generation failed: $taskSchemaName" }
    if([Text.Encoding]::UTF8.GetByteCount($taskSchemaText) -gt 1048576) { throw 'Schema exceeds 1 MiB.' }
    $taskDocument=$taskSchemaText | ConvertFrom-Json
    if($taskDocument.'$id' -ne "dotnetlab://schemas/v1/$taskSchemaName") { throw 'Schema identity differs.' }
    $taskSchemas[$taskSchemaName]=$taskSchemaText
}
New-Item -ItemType Directory -Path $taskDestination -Force | Out-Null
foreach($taskSchemaName in @('plan','measurement','capability','comparison','error')) {
    [IO.File]::WriteAllText((Join-Path $taskDestination "$taskSchemaName.schema.json"),$taskSchemas[$taskSchemaName]+"`n",[Text.UTF8Encoding]::new($false))
}
