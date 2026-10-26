param(
    [Parameter(Mandatory=$true)][string]$Dotnet,
    [Parameter(Mandatory=$true)][string]$HostAssembly,
    [string]$RuntimeVersion,
    [ValidateSet('serve','connect')][string]$Mode='serve',
    [string]$ScratchRoot='D:\Workspaces\AI\dotnetlab-mcp-verification'
)
$ErrorActionPreference='Stop'
$scratch=[IO.Path]::GetFullPath($ScratchRoot)
if(-not $scratch.StartsWith('D:\Workspaces\AI\',[StringComparison]::OrdinalIgnoreCase)) { throw 'MCP verification requires a designated AI workspace.' }
for($ancestor=[IO.DirectoryInfo]::new($scratch);$null-ne $ancestor;$ancestor=$ancestor.Parent) {
    if($ancestor.Exists-and ($ancestor.Attributes-band [IO.FileAttributes]::ReparsePoint)) { throw 'Verification paths cannot traverse reparse points.' }
}
if(-not [IO.Path]::IsPathFullyQualified($Dotnet)-or -not [IO.Path]::IsPathFullyQualified($HostAssembly)) { throw 'Use absolute executable and host paths.' }
$workspace=Join-Path $scratch ([Guid]::NewGuid().ToString('N'))
$process=$null
try {
    New-Item -ItemType Directory -Path $workspace -Force | Out-Null
    $start=[Diagnostics.ProcessStartInfo]::new()
    $start.FileName=$Dotnet; $start.WorkingDirectory=$workspace
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    if($RuntimeVersion) { foreach($argument in @('exec','--fx-version',$RuntimeVersion)) { $start.ArgumentList.Add($argument) } }
    foreach($argument in @($HostAssembly,$Mode,$workspace)) { $start.ArgumentList.Add($argument) }
    $process=[Diagnostics.Process]::new(); $process.StartInfo=$start
    if(-not $process.Start()) { throw 'MCP host did not start.' }
    $stderr=$process.StandardError.ReadToEndAsync()
    function Receive-Mcp([int]$Id) {
        for($attempt=0;$attempt-lt 30;$attempt++) {
            $line=$process.StandardOutput.ReadLineAsync()
            if(-not $line.Wait(20000)) { throw 'MCP response timed out.' }
            if($null-eq $line.Result) { $process.WaitForExit(); throw ('MCP stdout closed: '+$stderr.Result) }
            $reply=$line.Result | ConvertFrom-Json -Depth 100
            if($reply.id-eq $Id) { if($reply.error) { throw ($reply.error | ConvertTo-Json -Compress) }; return $reply.result }
        }
        throw 'MCP response ID was not received.'
    }
    function Send-Mcp([int]$Id,[string]$Method,$Parameters) {
        $message=@{jsonrpc='2.0';id=$Id;method=$Method;params=$Parameters} | ConvertTo-Json -Depth 30 -Compress
        $process.StandardInput.WriteLine($message)
        return Receive-Mcp $Id
    }
    function Call-Lab([int]$Id,[string]$Tool,$Request) {
        $result=Send-Mcp $Id 'tools/call' @{name=$Tool;arguments=@{request=$Request}}
        if($result.isError) { throw ($result | ConvertTo-Json -Depth 15 -Compress) }
        $envelope=$result.content[0].text | ConvertFrom-Json -Depth 100
        if($envelope.status-ne 'ok') { throw ($envelope | ConvertTo-Json -Depth 15 -Compress) }
        return $envelope.data
    }
    $initialized=Send-Mcp 1 'initialize' @{protocolVersion='2025-06-18';capabilities=@{};clientInfo=@{name='dotnetlab-connection-test';version='1.0'}}
    $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
    $advertised=Send-Mcp 2 'tools/list' @{}
    $expected=@('lab.compare','lab.discover','lab.experiment','lab.inspect','lab.query','lab.run')
    if(@($advertised.tools).Count-ne 6-or (@($advertised.tools.name | Sort-Object)-join ',')-ne ($expected-join ',')) { throw 'Unexpected MCP tool set.' }
    foreach($tool in $advertised.tools) { if(-not $tool.inputSchema.properties.request) { throw ('Missing request schema: '+$tool.name) } }
    $profiles=Call-Lab 3 'lab.discover' @{action='profiles'}
    $identity=Call-Lab 4 'lab.discover' @{action='workspace'}
    if($identity.workspace.root-ne $workspace) { throw 'MCP workspace identity does not match the isolated catalog.' }
    $compatibility=Call-Lab 5 'lab.discover' @{action='compatibility'}
    if($RuntimeVersion-and $compatibility.controller.runtime_version-ne $RuntimeVersion) { throw 'MCP host used a different runtime.' }
    if($compatibility.controller.state-ne 'supported') { throw 'Managed controller runtime was rejected.' }
    $negative=Send-Mcp 6 'tools/call' @{name='lab.inspect';arguments=@{request=@{action='record';id='missing-mcp-verification-record'}}}
    if(-not $negative.content[0].text.StartsWith('{')) {
        $process.StandardInput.Close(); [void]$process.WaitForExit(5000)
        throw ('Unexpected MCP error response: '+($negative | ConvertTo-Json -Depth 10 -Compress)+'; host diagnostics: '+$stderr.Result)
    }
    $negativeEnvelope=$negative.content[0].text | ConvertFrom-Json -Depth 30
    if(-not $negative.isError-or $negativeEnvelope.code-ne 'record_not_found') { throw 'Missing-record error contract failed.' }
    $process.StandardInput.Close()
    if(-not $process.WaitForExit(5000)) { throw 'MCP host did not stop after EOF.' }
    if($process.ExitCode-ne 0) { throw ('MCP host exit code: '+$process.ExitCode) }
    [pscustomobject]@{status='passed';server=$initialized.serverInfo.name;protocol=$initialized.protocolVersion;
        runtime=$compatibility.controller.runtime_version;controller_state=$compatibility.controller.state;mode=$Mode;
        tools=@($advertised.tools.name);profiles=@($profiles).Count;workspace_identity='passed';error_contract='passed';shutdown='passed'} | ConvertTo-Json -Depth 10 -Compress
} finally {
    if($process) { if(-not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }; $process.Dispose() }
    # Only this test created the random workspace. Let its shared controller exit
    # on idle before removing its catalog; never launch another catalog writer.
    if($Mode-eq 'connect') {
        $deadline=[DateTime]::UtcNow.AddSeconds(20)
        while(Test-Path -LiteralPath (Join-Path $workspace 'controller.lock')) {
            try {
                $released=[IO.File]::Open((Join-Path $workspace 'controller.lock'),'Open','ReadWrite','None')
                $released.Dispose(); break
            } catch [IO.IOException] {
                if([DateTime]::UtcNow-ge $deadline) { throw 'Test controller did not release its workspace.' }
                Start-Sleep -Milliseconds 100
            }
        }
    }
    $resolved=[IO.Path]::GetFullPath($workspace)
    if([IO.Path]::GetDirectoryName($resolved)-ne $scratch-or [IO.Path]::GetFileName($resolved)-notmatch '^[a-f0-9]{32}$') { throw 'Unsafe verification cleanup path.' }
    if(Test-Path -LiteralPath $resolved) { Remove-Item -LiteralPath $resolved -Recurse -Force }
}
