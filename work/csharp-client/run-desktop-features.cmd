@echo off
powershell.exe -NoProfile -File "%~dp0tools\start-features.ps1" %*
set "sony_features_exit=%errorlevel%"
if not "%sony_features_exit%"=="0" pause
exit /b %sony_features_exit%
