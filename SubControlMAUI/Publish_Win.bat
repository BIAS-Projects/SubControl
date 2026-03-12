dotnet publish SubControlMAUI.csproj ^
  -c Release ^
  -f net10.0-windows10.0.19041.0 ^
  --self-contained false ^
  /p:PublishSingleFile=true

pause