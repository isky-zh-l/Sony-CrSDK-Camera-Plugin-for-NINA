@echo off
powershell.exe -NoProfile -File "%~dp0tools\start-desktop.ps1" %*
set "sony_desktop_exit=%errorlevel%"
if not "%sony_desktop_exit%"=="0" pause
exit /b %sony_desktop_exit%
