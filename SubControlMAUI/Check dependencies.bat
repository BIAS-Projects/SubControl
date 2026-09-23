@echo off
echo Checking .NET Desktop Runtime...
dotnet --list-runtimes | findstr "Microsoft.WindowsDesktop.App"
if errorlevel 1 (
    echo   NOT FOUND - .NET 10 Desktop Runtime is missing.
) else (
    echo   Found.
)

echo.
echo Checking Windows App SDK Runtime...
powershell -Command "if (Get-AppxPackage -Name 'Microsoft.WindowsAppRuntime.*') { Write-Host '  Found.' } else { Write-Host '  NOT FOUND - Windows App SDK Runtime is missing.' }"

pause