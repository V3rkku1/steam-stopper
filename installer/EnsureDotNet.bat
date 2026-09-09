@echo off
setlocal
set "RUNTIME=%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App"
dir /b /ad "%RUNTIME%\8.*" >nul 2>&1
if not errorlevel 1 exit /b 0

echo Installing .NET 8 Desktop Runtime...
curl -L --retry 3 -o "%TEMP%\windowsdesktop-runtime-8.exe" "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
if not exist "%TEMP%\windowsdesktop-runtime-8.exe" exit /b 0
"%TEMP%\windowsdesktop-runtime-8.exe" /install /quiet /norestart
exit /b 0
