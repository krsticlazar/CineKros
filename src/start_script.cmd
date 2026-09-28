@echo off
setlocal

for %%I in ("%~dp0..") do set "REPO_DIR=%%~fI"
set "STARTUP_DIR=%~dp0"
set "SERVER_DIR=%REPO_DIR%\src\backend\CineKros.Api"
set "CLIENT_DIR=%REPO_DIR%\src\frontend"
set "COMPOSE_FILE=%REPO_DIR%\database\docker\compose.yaml"
set "PATH=%ProgramFiles%\dotnet;%ProgramFiles%\nodejs;%PATH%"

if /i not "%~1"=="--bootstrap-ready" (
  powershell -NoProfile -ExecutionPolicy Bypass -File "%REPO_DIR%\src\load-startup-env.ps1" -LauncherPath "%~f0"
  exit /b %errorlevel%
)

rem Docker Desktop is started only through its explicit executable path.
docker.exe info >nul 2>&1
if not errorlevel 1 goto docker_ready

if not exist "%ProgramFiles%\Docker\Docker\Docker Desktop.exe" (
  echo GRESKA: Docker CLI engine nije dostupan, a eksplicitna Docker Desktop putanja ne postoji.
  pause
  exit /b 1
)
start "" "%ProgramFiles%\Docker\Docker\Docker Desktop.exe"

for /l %%I in (1,1,60) do (
  docker.exe info >nul 2>&1
  if not errorlevel 1 goto docker_ready
  timeout /t 2 /nobreak >nul
)

echo GRESKA: Docker engine nije postao dostupan u roku od 120 sekundi.
pause
exit /b 1

:docker_ready
if not defined CINEKROS_POSTGRES_PASSWORD (
  echo GRESKA: Nedostaje procesna promenljiva CINEKROS_POSTGRES_PASSWORD.
  pause
  exit /b 1
)

docker.exe compose -f "%COMPOSE_FILE%" up -d cinekros-postgres
if errorlevel 1 (
  echo GRESKA: PostgreSQL Compose servis nije mogao da se pokrene.
  pause
  exit /b 1
)

for /l %%I in (1,1,45) do (
  for /f "delims=" %%H in ('docker.exe inspect --format "{{.State.Health.Status}}" cinekros-postgres 2^>nul') do if /i "%%H"=="healthy" goto database_ready
  timeout /t 2 /nobreak >nul
)

echo GRESKA: PostgreSQL nije postao zdrav u roku od 90 sekundi.
pause
exit /b 1

:database_ready
if not defined CINEKROS_RECOMMENDATION_MODE set "CINEKROS_RECOMMENDATION_MODE=real"
if /i "%CINEKROS_RECOMMENDATION_MODE%"=="fake" (
  echo Rezim preporuka: Development fake ^(bez Gemini parsera i realne pretrage^).
) else if /i "%CINEKROS_RECOMMENDATION_MODE%"=="real" (
  if not defined DATABASE_CONNECTION_STRING (
    echo GRESKA: Nedostaje procesna promenljiva DATABASE_CONNECTION_STRING.
    pause
    exit /b 1
  )
  if not defined GEMINI_API_KEY (
    echo GRESKA: Nedostaje procesna promenljiva GEMINI_API_KEY.
    pause
    exit /b 1
  )
  if not defined CINEKROS_E5_MODEL_DIR set "CINEKROS_E5_MODEL_DIR=%REPO_DIR%\database\data\models\e5-base-v2\f52bf8ec8c7124536f0efb74aca902b2995e5bcd"
  if not exist "%CINEKROS_E5_MODEL_DIR%\model_qint8_avx512_vnni.onnx" (
    echo GRESKA: Nije pronadjen lokalni E5 model na CINEKROS_E5_MODEL_DIR.
    pause
    exit /b 1
  )
  echo Rezim preporuka: real.
) else (
  echo GRESKA: CINEKROS_RECOMMENDATION_MODE mora biti fake ili real.
  pause
  exit /b 1
)

start "CineKros Backend" cmd /k "cd /d ""%SERVER_DIR%"" && set ""ASPNETCORE_ENVIRONMENT=Development"" && dotnet run --launch-profile CineKros.Api"

for /l %%I in (1,1,60) do (
  powershell -NoProfile -Command "$c=New-Object Net.Sockets.TcpClient; try {$c.Connect('127.0.0.1',5179); $c.Close(); exit 0} catch {exit 1}"
  if not errorlevel 1 goto backend_ready
  timeout /t 1 /nobreak >nul
)

echo.
echo GRESKA: Backend nije postao dostupan u roku od 60 sekundi.
pause
exit /b 1

:backend_ready
start "CineKros Frontend" cmd /k "cd /d ""%CLIENT_DIR%"" && npm run dev"

for /l %%I in (1,1,60) do (
  powershell -NoProfile -Command "try { Invoke-WebRequest 'http://localhost:5173' -UseBasicParsing -TimeoutSec 1 | Out-Null; exit 0 } catch { exit 1 }"
  if not errorlevel 1 (
    start "" "http://localhost:5173"
    exit /b 0
  )
  timeout /t 1 /nobreak >nul
)

echo.
echo GRESKA: Frontend nije postao dostupan u roku od 60 sekundi.
pause
exit /b 1
