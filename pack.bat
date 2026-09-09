@echo off
setlocal
title Pack Steam Stopper
cd /d "%~dp0"
set "PATH=C:\Program Files\dotnet;%PATH%"
set "VER=2.1.0"

echo Publishing Steam Stopper %VER%...
dotnet publish "src\SteamStopper\SteamStopper.csproj" -c Release -p:Version=%VER% -p:AssemblyVersion=%VER% -p:FileVersion=%VER% -o "dist\app"
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
echo Send your friend this file from the Desktop:
echo   Steam Stopper Setup.zip
echo They unzip it and double-click Install.bat
echo.
echo For auto-update:
echo   1. Create a public GitHub repository
echo   2. Put its URL in src\SteamStopper\FeedUrl.txt
echo   3. Run pack.bat again
echo   4. On GitHub: Releases - New release - tag v%VER%
echo      Attach dist\SteamStopper.zip
echo After that, your friend gets new versions when they open the app.
echo.
pause
