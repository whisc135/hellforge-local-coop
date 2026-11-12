@echo off
cd /d "%~dp0"
title HellForge Local Co-op
if not exist "%~dp0Apply-LocalCoop.ps1" (
  echo [x] Unzip the release archive fully. Apply-LocalCoop.ps1 must sit next to this file.
  pause
  exit /b 1
)
echo [*] Installing Local Co-op patch...
if "%~1"=="" (
  powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Apply-LocalCoop.ps1"
) else (
  powershell -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Apply-LocalCoop.ps1" -InstallRoot "%~1"
)
set ERR=%ERRORLEVEL%
if %ERR% neq 0 (echo [x] Install failed. Code %ERR%.) else (echo [+] Done.)
pause
exit /b %ERR%