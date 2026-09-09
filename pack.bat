@echo off
setlocal
title Pack Steam Stopper
cd /d "%~dp0"
set "PATH=C:\Program Files\dotnet;%PATH%"
set "VER=2.1.0"
set "PUB=%~dp0dist\app\"
set "PORT=%~dp0dist\portable\"

if exist dist\app rmdir /s /q dist\app
if exist dist\portable rmdir /s /q dist\portable
if not exist dist mkdir dist

echo Publishing Steam Stopper %VER%...
dotnet publish "src\SteamStopper\SteamStopper.csproj" -c Release -p:Version=%VER% -p:AssemblyVersion=%VER% -p:FileVersion=%VER% -p:SelfContained=false -o "dist\app"
if errorlevel 1 (
  echo Publish failed.
  pause
  exit /b 1
)

copy /Y "Install.bat" "dist\app\Install.bat" >nul
copy /Y "src\SteamStopper\FeedUrl.txt" "dist\app\FeedUrl.txt" >nul
copy /Y "installer\EnsureDotNet.bat" "dist\app\EnsureDotNet.bat" >nul
echo installed> "dist\app\.installed"

echo Building MSI...
dotnet build "installer\SteamStopper.wixproj" -c Release -p:ProductVersion=%VER% -p:PublishDir="%PUB%"
if errorlevel 1 (
  echo MSI build failed. Portable zip will still be created.
) else (
  copy /Y "installer\bin\Release\SteamStopper.msi" "dist\SteamStopper.msi" >nul
  copy /Y "dist\SteamStopper.msi" "%USERPROFILE%\Desktop\SteamStopper.msi" >nul
)

echo Creating portable package...
dotnet publish "src\SteamStopper\SteamStopper.csproj" -c Release -r win-x64 --self-contained true -p:Version=%VER% -p:AssemblyVersion=%VER% -p:FileVersion=%VER% -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o "dist\portable"
if errorlevel 1 (
  echo Self-contained portable publish failed. Using the smaller runtime-dependent copy.
  mkdir dist\portable >nul 2>&1
  xcopy /E /Y /Q "dist\app\*" "dist\portable\" >nul
  del /Q "dist\portable\Install.bat" >nul 2>&1
  del /Q "dist\portable\.installed" >nul 2>&1
  copy /Y "installer\EnsureDotNet.bat" "dist\portable\EnsureDotNet.bat" >nul
)
copy /Y "installer\PortableReadme.txt" "dist\portable\README.txt" >nul
if exist "dist\portable\*.pdb" del /Q "dist\portable\*.pdb" >nul 2>&1

echo Creating zip packages...
powershell -NoProfile -Command "Compress-Archive -Path 'dist\app\*' -DestinationPath 'dist\SteamStopper.zip' -Force"
powershell -NoProfile -Command "Compress-Archive -Path 'dist\app\*' -DestinationPath 'dist\Steam Stopper Setup.zip' -Force"
powershell -NoProfile -Command "Compress-Archive -Path 'dist\portable\*' -DestinationPath 'dist\SteamStopper-portable.zip' -Force"
copy /Y "dist\Steam Stopper Setup.zip" "%USERPROFILE%\Desktop\Steam Stopper Setup.zip" >nul
copy /Y "dist\SteamStopper-portable.zip" "%USERPROFILE%\Desktop\SteamStopper-portable.zip" >nul

echo.
echo Desktop:
echo   SteamStopper.msi              MSI installer
echo   SteamStopper-portable.zip     Portable folder
echo   Steam Stopper Setup.zip       Old unzip + Install.bat
echo.
echo GitHub update asset: dist\SteamStopper.zip
echo Tag: v%VER%
echo.
pause
