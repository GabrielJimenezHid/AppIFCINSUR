@echo off
dotnet build "%~dp0backend\IfcPlatform.sln" -v q --nologo
start "Servidor de plugins" cmd /k "cd /d %~dp0backend\src\Insur.Host && dotnet run --no-build"
cd /d "%~dp0viewer"
start "" http://localhost:5173/
npm run dev
