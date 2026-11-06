@echo off
setlocal
if exist "%~dp0app\ArysLauncher.exe" (
    start "" "%~dp0app\ArysLauncher.exe"
    exit /b 0
)
if exist "%~dp0.tools\dotnet\dotnet.exe" (
    set "DOTNET_ROOT=%~dp0.tools\dotnet"
)
if exist "%~dp0src\ArysLauncher\bin\Release\net10.0-windows10.0.17763.0\ArysLauncher.exe" (
    start "" "%~dp0src\ArysLauncher\bin\Release\net10.0-windows10.0.17763.0\ArysLauncher.exe"
    exit /b 0
)
echo Build the launcher with: powershell -File "%~dp0Build.ps1" -Publish
pause
