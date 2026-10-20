#!/bin/sh
# "dashboard" starts the ops dashboard; anything else is passed to the runner (all, alerts, scenarios, ...).
set -e

if [ "$1" = "dashboard" ]; then
  # Run from the publish folder so the app finds its wwwroot; it finds config/ and data/ in /app above it.
  cd /app/dashboard
  exec dotnet ColdChain.Dashboard.dll --urls "http://0.0.0.0:5080"
fi

exec dotnet run --project /app/src/ColdChain.Runner -c Release --no-build -- "$@"
