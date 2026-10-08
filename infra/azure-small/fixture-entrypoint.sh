#!/bin/sh
set -eu
docker-entrypoint.sh rabbitmq-server &
broker=$!
dotnet /app/Elwood.M6.FixtureHost.dll &
app=$!
trap 'kill "$app" "$broker" 2>/dev/null || true; wait' TERM INT EXIT
while kill -0 "$app" 2>/dev/null && kill -0 "$broker" 2>/dev/null; do sleep 2; done
exit 1
