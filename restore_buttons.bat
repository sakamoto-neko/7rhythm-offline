@echo off
rem Turns the official-site / support buttons back on (undoes what launch_offline.bat does). Close the game first.
rem Note: launch_offline.bat turns them off again on its next start.
%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0offline\patch_buttons.ps1" -Undo
pause
