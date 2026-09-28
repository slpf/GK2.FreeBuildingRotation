@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0build-release.ps1"
exit /b %ERRORLEVEL%
