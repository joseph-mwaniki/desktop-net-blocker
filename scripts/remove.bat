@echo off
:: Batch wrapper to launch uninstall-service.ps1 with administrator privileges
echo ==========================================================
echo  Desktop Net Blocker - Windows 11 Uninstaller
echo ==========================================================
echo Requesting administrator privileges...

net session >nul 2>&1
if %errorLevel% NEQ 0 (
    echo Elevating privileges...
    powershell -NoProfile -ExecutionPolicy Bypass -Command "Start-Process powershell -ArgumentList '-NoProfile -ExecutionPolicy Bypass -File \"%~dp0uninstall-service.ps1\"' -Verb RunAs"
    exit /b
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall-service.ps1"
pause
