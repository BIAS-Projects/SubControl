rmdir /s /q bin
rmdir /s /q obj

"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" ^
    SubControlMAUI.csproj ^
    /restore ^
    /t:Publish ^
    /p:Configuration=Release ^
    /p:TargetFramework=net10.0-windows10.0.19041.0 ^
    /p:RuntimeIdentifier=win-x64 ^
    /p:WindowsPackageType=None ^
    /p:SelfContained=true ^
    /p:WindowsAppSDKSelfContained=true ^
    /p:PublishTrimmed=false ^
    /p:EnableMsixTooling=true ^
    /p:DebugSymbols=false ^
    /p:DebugType=none ^
    /p:AllowedReferenceRelatedFileExtensions=none ^
    /p:MauiHybridWebViewSupported=false ^
    /p:PublishDir=bin\Release\net10.0-windows10.0.19041.0\win-x64\dist\

:: Forcefully clear out any lingering package symbol files
del /f /q "bin\Release\net10.0-windows10.0.19041.0\win-x64\dist\*.pdb"

:: ====================================================================
:: DEPLOY FFMPEG SCRIPT STAGE
:: ====================================================================
set "FFMPEG_SRC=D:\repository\SubControl\SubControlMAUI\FFmpeg"
set "DIST_TARGET=bin\Release\net10.0-windows10.0.19041.0\win-x64\dist"

echo Deploying core FFmpeg dependencies to target folder...

:: Copy the required binaries and engine dlls
xcopy "%FFMPEG_SRC%\ffmpeg.exe" "%DIST_TARGET%\" /y /q
xcopy "%FFMPEG_SRC%\ffprobe.exe" "%DIST_TARGET%\" /y /q
xcopy "%FFMPEG_SRC%\*.dll" "%DIST_TARGET%\" /y /q

:: Copy your operational batch scripts over to the distribution root
xcopy "%FFMPEG_SRC%\*.bat" "%DIST_TARGET%\" /y /q

echo FFmpeg configuration successfully copied.
:: ====================================================================

pause
