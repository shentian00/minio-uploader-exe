@echo off
rem Show today's uploader log in real time. Press Ctrl+C to stop viewing (the uploader keeps running).
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -Command "$f = Join-Path '%~dp0data\logs' ('uploader-' + (Get-Date -Format yyyyMMdd) + '.log'); if (Test-Path $f) { Get-Content $f -Encoding UTF8 -Tail 30 -Wait } else { 'Log file not found: ' + $f; pause }"
