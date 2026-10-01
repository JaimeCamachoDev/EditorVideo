@echo off
rem Doble clic para instalar el Editor de Video en este equipo.
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0instalar.ps1"
echo.
pause
