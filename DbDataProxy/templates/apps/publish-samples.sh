#!/usr/bin/env bash
# Publishes the two sample apps and makes their instances and a navigation set, on the Proxy whose data directory this machine can reach.
#   DBDATAPROXY="dotnet dbdataproxy.dll"  ./publish-samples.sh [<samples folder, default: this script's folder>] [-- <extra args for every command, e.g. --repo /data>]
# In the quickstart (docs/apps-testing.md): docker compose -f dist/compose/quickstart.yml -f dist/compose/quickstart-apps.yml exec proxy /samples/apps/publish-samples.sh
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
samples="${1:-$here}"
shift || true
[ "${1:-}" = "--" ] && shift
proxy=${DBDATAPROXY:-dotnet /app/dbdataproxy.dll}

apps() { echo "+ dbdataproxy apps $*"; $proxy apps "$@" ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"}; }
EXTRA_ARGS=("$@")

apps validate --dir "$samples/territory-notes"
apps publish --dir "$samples/territory-notes" --note "sample: a code app"
apps instance-save --name notes --app territory-notes --title "Territory notes" --bind territories=default
apps validate --dir "$samples/front-portal"
apps publish --dir "$samples/front-portal" --note "sample: a declared portal"
apps instance-save --name front --app front-portal --title "Front door"
apps nav-save --name main --title Main --item notes="Territory notes"
apps instance-list
