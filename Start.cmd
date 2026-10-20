@echo off
cd /d "%~dp0"
if not exist "CodexQuota.exe" powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0build.ps1"
if exist "CodexQuota.exe" start "" "%~dp0CodexQuota.exe"
