@echo off
setlocal
title Restart Codex

rem Match Codex Desktop by installation path, not the shared ChatGPT.exe name.
echo Requesting a normal Codex exit...
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; $package=Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1; if(-not $package) { throw 'Codex Desktop is not installed for this user.' }; $root=$package.InstallLocation.TrimEnd('\')+'\'; Get-Process -Name ChatGPT -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { [void]$_.CloseMainWindow() }"
if errorlevel 1 goto failed

echo Waiting 5 seconds...
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "Start-Sleep -Seconds 5"

echo Force-stopping remaining Codex Desktop processes and their children...
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "$ErrorActionPreference='Stop'; $package=Get-AppxPackage -Name OpenAI.Codex | Select-Object -First 1; if(-not $package) { throw 'Codex Desktop is not installed for this user.' }; $root=$package.InstallLocation.TrimEnd('\')+'\'; $localRoot=[Environment]::GetFolderPath('LocalApplicationData')+'\OpenAI\Codex\'; $owned=@(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and ($_.ExecutablePath.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath.StartsWith($localRoot,[StringComparison]::OrdinalIgnoreCase)) }); foreach($item in $owned) { $live=Get-Process -Id $item.ProcessId -ErrorAction SilentlyContinue; if($live -and $live.Path -eq $item.ExecutablePath) { & taskkill.exe /PID $item.ProcessId /T /F 2>&1 | Out-Null } }; $remaining=@(Get-CimInstance Win32_Process | Where-Object { $_.ExecutablePath -and ($_.ExecutablePath.StartsWith($root,[StringComparison]::OrdinalIgnoreCase) -or $_.ExecutablePath.StartsWith($localRoot,[StringComparison]::OrdinalIgnoreCase)) }); if($remaining.Count) { throw 'Some Codex processes could not be stopped. Restart was aborted.' }"
if errorlevel 1 goto failed

echo Waiting 2 seconds...
powershell.exe -NoLogo -NoProfile -NonInteractive -Command "Start-Sleep -Seconds 2"

echo Starting Codex Desktop...
start "" explorer.exe "shell:AppsFolder\OpenAI.Codex_2p2nqsd0c76g0!App"
exit /b 0

:failed
echo Codex restart failed. See the error above.
pause
exit /b 1
