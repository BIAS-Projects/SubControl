@echo off
setlocal

set PROJECT=SubControlMAUI.csproj
set CONFIG=Release
set FRAMEWORK=net8.0-windows10.0.19041.0
set RUNTIME=win-x64

echo Cleaning...
dotnet clean "%PROJECT%"

echo Removing obj/bin...
rmdir /s /q bin 2>nul
rmdir /s /q obj 2>nul

echo Restoring Windows target...
dotnet restore "%PROJECT%" -p:TargetFramework=%FRAMEWORK%

echo Publishing single-file EXE...
dotnet publish "%PROJECT%" ^
  -c %CONFIG% ^
  -f %FRAMEWORK% ^
  -r %RUNTIME% ^
  --self-contained true ^
  /p:PublishSingleFile=true ^
  /p:PublishTrimmed=false ^
  /p:IncludeNativeLibrariesForSelfExtract=true

echo.
echo Done!
pause
endlocal
