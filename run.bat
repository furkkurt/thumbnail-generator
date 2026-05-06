@echo off
REM Monorepo kökünden çalıştırın: run.bat veya çift tıklayın.
REM Ctrl+C sonrası API süreci kapatılır (run.sh'deki trap davranışına benzer).
REM Proje kökü bu betikten hesaplanır; yolu başka bir CMD oturumuna kopyalamayın.

setlocal EnableExtensions
cd /d "%~dp0"
set "ROOT=%CD%"

powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference = 'Stop'; " ^
  "$api = Join-Path $env:ROOT 'backend\src\ThumbnailGenerator.Api'; " ^
  "$fe  = Join-Path $env:ROOT 'frontend'; " ^
  "$p = Start-Process -FilePath 'dotnet' -ArgumentList 'run' -WorkingDirectory $api -PassThru -NoNewWindow; " ^
  "try { Set-Location $fe; npm run dev } " ^
  "finally { if ($p -and -not $p.HasExited) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue } }"

set "EC=%ERRORLEVEL%"
endlocal & exit /b %EC%
