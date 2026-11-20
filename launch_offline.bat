@echo off
rem 7RHYTHM offline launcher - uses the local server in the "offline" folder instead of the online server.

rem check admin
%SystemRoot%\System32\net.exe FILE > NUL 2>&1 || %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -ex Unrestricted -Command "Start-Process -Verb RunAs -FilePath '%ComSpec%' -ArgumentList '/c \"%~f0\" %*'" && EXIT /b

taskkill /F /IM 7rhythm.exe >NUL 2>&1
rem stop any offline server still running from an earlier launch (only one may run at a time)
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like '*offline_server.ps1*' -and $_.ProcessId -ne $PID } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }" >NUL 2>&1
timeout /t 1 /nobreak >NUL

pushd %~dp0
if not exist offline\logs mkdir offline\logs

rem turn off the official-site / support buttons (they cannot work offline); skipped if already done
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0offline\patch_buttons.ps1"

rem the game looks up pcgame.7rhythm.jp at every start: point the name at its original address, which the
rem offline server makes local while it runs (an entry pointing anywhere else is changed to it)
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -Command "$h=$env:SystemRoot+'\System32\drivers\etc\hosts'; $want='52.69.127.28 pcgame.7rhythm.jp'; $lines=@(Get-Content -Path $h); $out=@(); $found=$false; foreach ($l in $lines) { if ($l -match '^\s*[0-9.]+\s+pcgame\.7rhythm\.jp(\s|$)') { if (-not $found) { $out+=$want }; $found=$true } else { $out+=$l } }; if (-not $found) { $out+=$want }; if (($out -join '|') -ne ($lines -join '|')) { Set-Content -Path $h -Value $out -Encoding ASCII; ipconfig /flushdns | Out-Null; Write-Host 'hosts file: pcgame.7rhythm.jp now points to 52.69.127.28' }"

rem allow local connections to the offline server through Windows Firewall (local ports 45558-45560 only)
netsh advfirewall firewall delete rule name="7RHYTHM Offline Server" >NUL 2>&1
netsh advfirewall firewall add rule name="7RHYTHM Offline Server" dir=in action=allow protocol=TCP localport=45558,45559,45560 >NUL

rem start the offline server (game API, 7BEAT login and asset server)
if exist "%~dp0offline\save\server.ready" del "%~dp0offline\save\server.ready"
start "7RHYTHM Offline Server" %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0offline\offline_server.ps1"

rem wait until the server is listening (it writes offline\save\server.ready), at most 60 seconds
set /a waited=0
:waitserver
timeout /t 1 /nobreak >NUL
set /a waited+=1
if not exist "%~dp0offline\save\server.ready" if %waited% lss 60 goto waitserver

start /max 7rhythm 7BEATKEY OfflineMode00000 POS 400,400
