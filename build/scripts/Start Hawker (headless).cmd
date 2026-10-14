@echo off
rem Starts Hawker in the background with only a tray icon. Quit it from the icon's menu.
cd /d "%~dp0"
start "" Hawker.Web.exe --headless
