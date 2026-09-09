@echo off
title Steam Stopper
cd /d "%~dp0"
set "PATH=C:\Program Files\dotnet;%PATH%"
set "EXE=src\SteamStopper\bin\Release\net8.0-windows\SteamStopper.exe"

if exist "%EXE%" (
  start "" "%EXE%"
  exit /b 0
)

dotnet build "src\SteamStopper\SteamStopper.csproj" -c Release
if errorlevel 1 (
  echo Build failed. Install the .NET 8 SDK and try again.
  pause
  exit /b 1
)
start "" "%EXE%"
