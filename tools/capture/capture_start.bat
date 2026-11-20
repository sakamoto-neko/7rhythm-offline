@echo off
rem Starts a packet capture of the game's online traffic (the game API on TCP port 80 and the 7BEAT login on TCP
rem port 58001), using Windows' built-in pktmon. Close other programs that use plain HTTP while capturing.
rem Run capture_stop.bat when finished; it saves capture.pcapng next to this file.
%SystemRoot%\System32\net.exe FILE > NUL 2>&1 || %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -ex Unrestricted -Command "Start-Process -Verb RunAs -FilePath '%ComSpec%' -ArgumentList '/c \"%~f0\" %*'" && EXIT /b
pushd %~dp0
pktmon stop >NUL 2>&1
pktmon filter remove >NUL
pktmon filter add GameApi -t TCP -p 80
pktmon filter add GameLogin -t TCP -p 58001
pktmon start --capture --comp nics --pkt-size 0 --file-name "%~dp0capture.etl"
if %errorlevel% neq 0 (
  echo pktmon with --comp failed, retrying without it...
  pktmon start --capture --pkt-size 0 --file-name "%~dp0capture.etl"
)
echo.
echo Capture is running. Start the game in online mode and use the screens you want to capture.
echo When finished, run capture_stop.bat.
pause
