"C:\Program Files\Microsoft Visual Studio\18\Community\MSBuild\Current\Bin\MSBuild.exe" ^
    SubControlMAUI.csproj ^
    /restore ^
    /t:Publish ^
    /p:Configuration=Release ^
    /p:TargetFramework=net10.0-windows10.0.19041.0 ^
    /p:RuntimeIdentifier=win-x64 ^
    /p:WindowsPackageType=None ^
    /p:WindowsAppSDKSelfContained=true ^
    /p:PublishDir=bin\Release\net10.0-windows10.0.19041.0\win-x64\dist\
pause
