@echo off
title OpenDriverUpdater - compilar y abrir
cd /d "%~dp0"
echo ==== %date% %time% > build.log
echo [where dotnet] >> build.log
where dotnet >> build.log 2>&1
echo [dotnet --list-sdks] >> build.log
dotnet --list-sdks >> build.log 2>&1
dotnet --list-sdks 2>nul | findstr /r "[0-9]" >nul
if errorlevel 1 goto nosdk
echo Compilando OpenDriverUpdater...
echo [dotnet build] >> build.log
dotnet build src\OpenDriverUpdater -c Release -nologo >> build.log 2>&1
if errorlevel 1 (
  type build.log
  echo.
  echo La compilacion ha fallado. El detalle esta en build.log
  pause
  exit /b 1
)
echo Compilacion OK >> build.log
echo Abriendo el programa (Windows pedira permisos de administrador)...
start "" "src\OpenDriverUpdater\bin\Release\net8.0-windows\OpenDriverUpdater.exe"
exit /b 0
:nosdk
echo NO SE ENCONTRO NINGUN SDK >> build.log
type build.log
echo.
echo No se ha encontrado el SDK de .NET (solo el Runtime, o nada).
echo Descargalo de https://dotnet.microsoft.com/download (elige "SDK x64"),
echo instalalo, CIERRA esta ventana y vuelve a abrir Ejecutar.bat.
pause
exit /b 1
