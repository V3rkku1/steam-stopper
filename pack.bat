@echo off
setlocal
title Pack Steam Stopper
cd /d "%~dp0"
set "PATH=C:\Program Files\dotnet;%PATH%"
set "VER=2.1.0"

if exist dist\app rmdir /s /q dist\app

echo Publishing Steam Stopper %VER%...
dotnet publish "src\SteamStopper\SteamStopper.csproj" -c Release -p:Version=%VER% -p:AssemblyVersion=%VER% -p:FileVersion=%VER% -p:SelfContained=false -o "dist\app"
if errorlevel 1 (
  echo Publish failed.
  pause
  exit /b 1
)

copy /Y "Install.bat" "dist\app\Install.bat" >nul
copy /Y "src\SteamStopper\FeedUrl.txt" "dist\app\FeedUrl.txt" >nul

echo Creating zip packages...
if not exist dist mkdir dist
powershell -NoProfile -Command "Compress-Archive -Path 'dist\app\*' -DestinationPath 'dist\SteamStopper.zip' -Force"
powershell -NoProfile -Command "Compress-Archive -Path 'dist\app\*' -DestinationPath 'dist\Steam Stopper Setup.zip' -Force"
copy /Y "dist\Steam Stopper Setup.zip" "%USERPROFILE%\Desktop\Steam Stopper Setup.zip" >nul

echo.
echo Desktop: Steam Stopper Setup.zip
echo Release asset: dist\SteamStopper.zip
echo Tag: v%VER%
echo.
pause
