@echo off
rem Starts Hawker and opens the dashboard. Close this window to stop Hawker.
cd /d "%~dp0"
title Hawker
Hawker.Web.exe --Hawker:OpenDashboardOnStart=true
