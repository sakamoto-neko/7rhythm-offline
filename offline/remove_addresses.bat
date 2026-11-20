@echo off
rem Removes the game server addresses the offline server adds to the loopback adapter (normally removed
rem automatically when the server window closes; they also disappear on reboot). Also clears host routes left by
rem older versions of the offline server.
%SystemRoot%\System32\net.exe FILE > NUL 2>&1 || %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -ex Unrestricted -Command "Start-Process -Verb RunAs -FilePath '%ComSpec%' -ArgumentList '/c \"%~f0\" %*'" && EXIT /b
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -Command "foreach ($ip in @('210.128.4.130','52.69.127.28','52.196.206.7')) { Remove-NetIPAddress -IPAddress $ip -Confirm:$false -ErrorAction SilentlyContinue; Remove-NetRoute -DestinationPrefix ($ip + '/32') -Confirm:$false -ErrorAction SilentlyContinue }; Write-Host 'Offline server addresses removed.'"
pause
