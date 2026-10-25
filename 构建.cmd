@echo off
setlocal
set "TEMP=%~dp0tmp"
set "TMP=%~dp0tmp"
if not exist "%TEMP%" mkdir "%TEMP%"
node "%~dp0src\build.mjs" >nul
if errorlevel 1 exit /b 1
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" @"%~dp0tmp\build.rsp"
exit /b %errorlevel%
