@echo off
setlocal

REM ==========================
REM CONFIG
REM ==========================
set PROJECT_PATH=SubControlMAUI.csproj
set CONFIGURATION=Release
set FRAMEWORK=net10.0-windows10.0.19041.0
set RUNTIME=win-x64
set OUTPUT_DIR=publish

REM ==========================
REM CLEAN
REM ==========================
if exist "%OUTPUT_DIR%" (
    rmdir /s /q "%OUTPUT_DIR%"
)

REM ==========================
REM PUBLISH
REM ==========================
dotnet publish "%PROJECT_PATH%" ^
    -c %CONFIGURATION% ^
    -f %FRAMEWORK% ^
    -r %RUNTIME% ^
    --self-contained true ^
    -o "%OUTPUT_DIR%" ^
    /p:PublishSingleFile=true ^
    /p:IncludeNativeLibrariesForSelfExtract=true ^
    /p:EnableCompressionInSingleFile=true ^
    /p:UseMonoRuntime=false

pause
endlocal
