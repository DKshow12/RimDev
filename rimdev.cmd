@echo off
rem RimDev 版本切换入口（第 3 期 Task 9）。真正的逻辑在 rimdev.ps1。
rem 用法：rimdev --profile 1.6 env / rimdev --profile 1.7 env / rimdev profile list / rimdev profile use 1.7
setlocal
set "PS=powershell"
where pwsh >nul 2>nul && set "PS=pwsh"
"%PS%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0rimdev.ps1" %*
exit /b %ERRORLEVEL%
