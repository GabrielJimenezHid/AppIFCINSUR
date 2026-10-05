@echo off
where node >nul 2>nul
if errorlevel 1 (
  echo FALTA Node.js. Instalalo desde https://nodejs.org y vuelve a ejecutar este archivo.
  pause
  exit /b 1
)
where dotnet >nul 2>nul
if errorlevel 1 (
  echo FALTA el SDK de .NET 10. Instalalo desde https://dotnet.microsoft.com/download y vuelve a ejecutar este archivo.
  pause
  exit /b 1
)

if not exist "%~dp0viewer\node_modules" (
  echo Instalando dependencias del visor, solo la primera vez...
  pushd "%~dp0viewer"
  call npm install
  popd
)

echo Compilando servidor de plugins...
dotnet build "%~dp0backend\IfcPlatform.slnx" -v q --nologo
if errorlevel 1 (
  echo La compilacion del servidor ha fallado.
  pause
  exit /b 1
)

start "Servidor de plugins" cmd /k "cd /d %~dp0backend\src\Insur.Host && dotnet run --no-build"
start "" cmd /c "timeout /t 6 /nobreak >nul & start http://localhost:5173/"

cd /d "%~dp0viewer"
call npm run dev
pause
