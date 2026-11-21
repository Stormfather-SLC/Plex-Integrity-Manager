@echo off
rem Double-click to update, build and start PIM (Start-PIM.ps1), then open it
rem in the browser. Close this window or press Ctrl+C to stop PIM.
title PIM
cd /d "%~dp0"

rem PIM already running: just open it.
powershell -NoProfile -Command "try { Invoke-WebRequest 'http://localhost:5183/' -UseBasicParsing -TimeoutSec 2 | Out-Null; exit 0 } catch { exit 1 }"
if errorlevel 1 goto start
echo PIM is already running. Opening it in your browser...
start "" "http://localhost:5183/"
"%SystemRoot%\System32\timeout.exe" /t 3 >nul
exit /b 0

:start
rem Opens the browser once PIM answers; gives up after about 4 minutes.
start "" /min powershell -NoProfile -WindowStyle Hidden -Command "for ($i = 0; $i -lt 120; $i++) { try { Invoke-WebRequest 'http://localhost:5183/' -UseBasicParsing -TimeoutSec 2 | Out-Null; Start-Process 'http://localhost:5183/'; break } catch { Start-Sleep -Seconds 2 } }"

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Start-PIM.ps1"

echo.
echo PIM has stopped. Any error is shown above.
pause
