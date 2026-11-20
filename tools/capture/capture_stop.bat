@echo off
rem Stops the capture started by capture_start.bat and converts it to capture.pcapng (open it in Wireshark, or
rem run tools\dev\pairs.py on it).
%SystemRoot%\System32\net.exe FILE > NUL 2>&1 || %SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe -ex Unrestricted -Command "Start-Process -Verb RunAs -FilePath '%ComSpec%' -ArgumentList '/c \"%~f0\" %*'" && EXIT /b
pushd %~dp0
pktmon stop
pktmon etl2pcap "%~dp0capture.etl" --out "%~dp0capture.pcapng"
pktmon filter remove >NUL
echo.
echo Saved capture.pcapng in %~dp0
pause
