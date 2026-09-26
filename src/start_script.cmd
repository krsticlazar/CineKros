@echo off
setlocal

net session >nul 2>&1
if %errorlevel% neq 0 (
  powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)

set "SERVER_DIR=%~dp0backend\CineKros.Api"
set "CLIENT_DIR=%~dp0frontend"
set "PATH=%ProgramFiles%\dotnet;%ProgramFiles%\nodejs;%PATH%"

start "CineKros Backend" cmd /k "cd /d ""%SERVER_DIR%"" && dotnet run --launch-profile CineKros.Api"

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