@echo off
REM Portable HellForge: force Spacewar (480) so Valve steam_api Init works without HD2 ownership.
set SteamAppId=480
set SteamGameId=480
cd /d "%~dp0"
start "" "HellForge.exe" %*
