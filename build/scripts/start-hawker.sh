#!/usr/bin/env bash
# Starts Hawker and opens the dashboard. Press Ctrl+C (or close the terminal) to stop Hawker.
cd "$(dirname "$0")"
( sleep 4; xdg-open http://localhost:5080 >/dev/null 2>&1 || true ) &
exec ./Hawker.Web
