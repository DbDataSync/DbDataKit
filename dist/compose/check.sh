#!/usr/bin/env bash
# Starts a compose file of this folder, waits for its server to announce itself to the console, confirms it, and checks the console lists it.
# usage: dist/compose/check.sh <compose file> <service name as announced>
set -euo pipefail
file=$1 service=$2
console() { docker compose -f "$file" exec -T console dotnet /app/dbdatakit.dll "$@"; }
trap 'docker compose -f "$file" logs --no-color | tail -60; docker compose -f "$file" down -v -t 2' EXIT
docker compose -f "$file" up -d --build --wait
for _ in $(seq 1 60); do
  if console instance pending 2>&1 | grep -q "^$service "; then break; fi
  sleep 2
done
console instance pending | grep -q "^$service " || { echo "$service never announced itself"; exit 1; }
console instance list --offline | grep -q "^$service " && { echo "$service was paired before anyone confirmed it"; exit 1; }
console instance confirm "$service"
console instance list | grep "^$service " | grep -q ready
echo "$service is paired and ready"
