@echo off
title Steam Stopper
cd /d "%~dp0"
set "PATH=C:\Program Files\dotnet;%PATH%"

if exist "src\SteamStopper\bin\Release\appsui\SteamStopper.exe" (
  start "" "src\SteamStopper\bin\Release\appsui\SteamStopper.exe"
  exit /b 0
)

if exist "src\SteamStopper\bin\Release\tools\SteamStopper.exe" (
  start "" "src\SteamStopper\bin\Release\tools\SteamStopper.exe"
  exit /b 0
)

if exist "src\SteamStopper\bin\Release\ok\SteamStopper.exe" (
  start "" "src\SteamStopper\bin\Release\ok\SteamStopper.exe"
  exit /b 0
)

if exist "src\SteamStopper\bin\Release\fixed\SteamStopper.exe" (
  start "" "src\SteamStopper\bin\Release\fixed\SteamStopper.exe"
  exit /b 0
)

if exist "src\SteamStopper\bin\Release\dns\SteamStopper.exe" (
  start "" "src\SteamStopper\bin\Release\dns\SteamStopper.exe"
  exit /b 0
)

if exist "src\SteamStopper\bin\Release\latest\SteamStopper.exe" (
  start "" "src\SteamStopper\bin\Release\latest\SteamStopper.exe"
  exit /b 0
)

if exist "src\SteamStopper\bin\Release\net8.0-windows\SteamStopper.exe" (
  start "" "src\SteamStopper\bin\Release\net8.0-windows\SteamStopper.exe"
  exit /b 0
)

dotnet build "src\SteamStopper\SteamStopper.csproj" -c Release
if errorlevel 1 (
  echo Build failed. Install the .NET 8 SDK and try again.
  pause
  exit /b 1
)
start "" "src\SteamStopper\bin\Release\net8.0-windows\SteamStopper.exe"
