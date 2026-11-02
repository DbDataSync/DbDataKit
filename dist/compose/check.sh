#!/usr/bin/env bash
# Starts a compose file of this folder, waits for its server to announce itself to the console, confirms it, and checks the console lists it.
# usage: dist/compose/check.sh <compose file> <service name as announced>...
set -euo pipefail
file=$1; shift
console() { docker compose -f "$file" exec -T console dotnet /app/dbdatakit.dll "$@"; }
trap 'docker compose -f "$file" logs --no-color | tail -60; docker compose -f "$file" down -v -t 2' EXIT
docker compose -f "$file" up -d --build --wait
# Output is captured and matched in the shell: with pipefail, "cmd | grep -q" fails when grep exits on its first match and the writer is cut off by SIGPIPE.
for service in "$@"; do
  for _ in $(seq 1 60); do
    pending=$(console instance pending 2>&1 || true)
    if [[ $pending == *$'\n'"$service "* || $pending == "$service "* ]]; then break; fi
    sleep 2
  done
  [[ $pending == *$'\n'"$service "* || $pending == "$service "* ]] || { echo "$service never announced itself"; exit 1; }
  paired=$(console instance list --offline 2>&1 || true)
  if [[ $paired == *$'\n'"$service "* || $paired == "$service "* ]]; then echo "$service was paired before anyone confirmed it"; exit 1; fi
  console instance confirm "$service"
  listed=$(console instance list)
  [[ $listed =~ (^|$'\n')"$service "[^$'\n']*ready ]] || { echo "$service is not listed as ready"; exit 1; }
  echo "$service is paired and ready"
done
