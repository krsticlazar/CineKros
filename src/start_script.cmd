@echo off
setlocal EnableExtensions
set "STARTUP_DIR=%~dp0"
set "LAUNCHER_PATH=%~f0"
set "START_OPTIONS="
set "START_INTERACTIVE=1"
title CineKros - Startup
:options
if "%~1"=="" goto launch
if /i "%~1"=="--bootstrap-ready" goto bootstrap
if /i "%~1"=="--v5" (set "START_OPTIONS=%START_OPTIONS% -V5") else if /i "%~1"=="--headless" (set "START_OPTIONS=%START_OPTIONS% -Headless" & set "START_INTERACTIVE=0") else if /i "%~1"=="--stop" (set "START_OPTIONS=%START_OPTIONS% -Stop" & set "START_INTERACTIVE=0") else (
  echo GRESKA: Nepoznata opcija. Koristi --v5, --headless ili --stop.
  exit /b 1
)
shift
goto options
:launch
set "PATH=%ProgramFiles%\dotnet;%ProgramFiles%\nodejs;%PATH%"
powershell -NoProfile -ExecutionPolicy Bypass -File "%STARTUP_DIR%load-startup-env.ps1" -LauncherPath "%LAUNCHER_PATH%" %START_OPTIONS%
set "START_EXIT=%errorlevel%"
if not "%START_EXIT%"=="0" if "%START_INTERACTIVE%"=="1" pause
exit /b %START_EXIT%

:bootstrap
for %%I in ("%STARTUP_DIR%..") do set "REPO_DIR=%%~fI"
if /i not "%CINEKROS_RUNTIME_PROFILE%"=="actor-v6-isolated" if /i not "%CINEKROS_RUNTIME_PROFILE%"=="v5" (echo GRESKA: Neispravan runtime profil.& exit /b 1)
set "BACKEND_DIR=%REPO_DIR%\src\backend\CineKros.Api"
set "FRONTEND_DIR=%REPO_DIR%\src\frontend"
set "BACKEND_DLL=%BACKEND_DIR%\bin\Debug\net10.0\CineKros.Api.dll"
set "BACKEND_PORT=5186"
set "START_VERSION=V6"
if /i "%CINEKROS_RUNTIME_PROFILE%"=="v5" (set "BACKEND_PORT=5179" & set "START_VERSION=V5")
set "CINEKROS_BACKEND_PORT=%BACKEND_PORT%"
start "CineKros %START_VERSION% - Backend" /D "%REPO_DIR%" cmd.exe /d /k dotnet "%BACKEND_DLL%" --urls "http://127.0.0.1:%BACKEND_PORT%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$end=(Get-Date).AddSeconds(180); do { $c=[Net.Sockets.TcpClient]::new(); try { $a=$c.BeginConnect('127.0.0.1',[int]$env:CINEKROS_BACKEND_PORT,$null,$null); if($a.AsyncWaitHandle.WaitOne(500)){ $c.EndConnect($a); exit 0 } } catch {} finally { $c.Dispose() }; Start-Sleep -Milliseconds 250 } while((Get-Date)-lt $end); exit 1"
if errorlevel 1 (echo GRESKA: Backend nije postao spreman; proveri njegovu konzolu.& exit /b 1)
start "CineKros %START_VERSION% - Frontend" /D "%FRONTEND_DIR%" cmd.exe /d /k node node_modules/vite/bin/vite.js --host 127.0.0.1 --port 5173 --strictPort
powershell -NoProfile -ExecutionPolicy Bypass -Command "$end=(Get-Date).AddSeconds(60); do { try { $r=Invoke-WebRequest -Uri 'http://127.0.0.1:5173/' -UseBasicParsing -TimeoutSec 2; if($r.StatusCode -eq 200){ exit 0 } } catch {}; Start-Sleep -Milliseconds 250 } while((Get-Date)-lt $end); exit 1"
if errorlevel 1 (echo GRESKA: Frontend nije vratio HTTP 200; proveri njegovu konzolu.& exit /b 1)
start "" "http://127.0.0.1:5173/"
echo CineKros V6 je spreman. Servisne konzole su nezavisne; zatvori ih rucno ili pritisni CTRL+C u svakoj za gasenje.
exit /b 0
