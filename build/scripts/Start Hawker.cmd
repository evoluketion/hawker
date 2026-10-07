@echo off
rem Starts Hawker and opens the dashboard. Close this window to stop Hawker.
cd /d "%~dp0"
title Hawker
rem Give the server a few seconds to start before opening the browser.
start "" /b cmd /c "timeout /t 4 /nobreak >nul & start "" http://localhost:5080"
Hawker.Web.exe
