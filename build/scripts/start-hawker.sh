#!/usr/bin/env bash
# Starts Hawker and opens the dashboard. Press Ctrl+C (or close the terminal) to stop Hawker.
# With --headless, Hawker runs in the background with only a tray icon, and is quit from the icon's menu.
cd "$(dirname "$0")"
if [[ "${1:-}" == "--headless" ]]; then
  shift
  # setsid detaches Hawker from this terminal, so closing it leaves Hawker running.
  setsid ./Hawker.Web --headless "$@" >/dev/null 2>&1 < /dev/null &
  exit 0
fi
exec ./Hawker.Web --Hawker:OpenDashboardOnStart=true "$@"
