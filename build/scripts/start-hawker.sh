#!/usr/bin/env bash
# Starts Hawker and opens the dashboard. Press Ctrl+C (or close the terminal) to stop Hawker.
cd "$(dirname "$0")"
exec ./Hawker.Web --Hawker:OpenDashboardOnStart=true
