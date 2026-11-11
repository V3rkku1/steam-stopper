@echo off
setlocal
title Install Steam Multitool
cd /d "%~dp0"

set "RUNTIME=%ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App"
dir /b /ad "%RUNTIME%\8.*" >nul 2>&1
if errorlevel 1 (
  echo Downloading .NET 8 Desktop Runtime. This is only needed once...
  curl -L --retry 3 -o "%TEMP%\windowsdesktop-runtime-8.exe" "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
  if exist "%TEMP%\windowsdesktop-runtime-8.exe" (
    "%TEMP%\windowsdesktop-runtime-8.exe" /install /quiet /norestart
  ) else (
    echo Could not download the runtime. Install .NET 8 Desktop Runtime, then run this again.
    start "" "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
    pause
    exit /b 1
  )
)

set "DEST=%LOCALAPPDATA%\SteamStopper"
echo Installing to "%DEST%" ...
if not exist "%DEST%" mkdir "%DEST%"
xcopy /E /Y /Q "%~dp0*" "%DEST%\" >nul
echo installed> "%DEST%\.installed"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$dest = Join-Path $env:LOCALAPPDATA 'SteamStopper'; $exe = Join-Path $dest 'SteamStopper.exe'; $ws = New-Object -ComObject WScript.Shell; $desk = [Environment]::GetFolderPath('Desktop'); $start = [Environment]::GetFolderPath('StartMenu'); Remove-Item (Join-Path $desk 'Steam Stopper.lnk'),(Join-Path $start 'Steam Stopper.lnk') -ErrorAction SilentlyContinue; $s1 = $ws.CreateShortcut((Join-Path $desk 'Steam Multitool.lnk')); $s1.TargetPath = $exe; $s1.WorkingDirectory = $dest; $s1.IconLocation = $exe; $s1.Save(); $s2 = $ws.CreateShortcut((Join-Path $start 'Steam Multitool.lnk')); $s2.TargetPath = $exe; $s2.WorkingDirectory = $dest; $s2.IconLocation = $exe; $s2.Save()"

echo Installed. Launching Steam Multitool...
start "" "%DEST%\SteamStopper.exe"
exit /b 0
