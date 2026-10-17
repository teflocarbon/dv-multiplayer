@echo off
rem Starts a Derail Valley dedicated server with the watchdog next to this file
rem (run-dedicated-host.ps1). It asks which save to host; any of its options can follow,
rem e.g. Start Dedicated Server.cmd -Save "My Career" -Visibility public
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0run-dedicated-host.ps1" %*
pause
