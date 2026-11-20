@echo off
rem Exports the game's local settings (Unity PlayerPrefs) to prefs.reg. Used to find which values hold the
rem song-select state that the offline server resets on a fresh save.
pushd %~dp0
reg export "HKCU\Software\SammyNetworks\nana-rhythm" "%~dp0prefs.reg" /y
if %errorlevel% neq 0 (
  echo Could not find the game settings in the registry.
) else (
  echo Saved prefs.reg in %~dp0
)
pause
