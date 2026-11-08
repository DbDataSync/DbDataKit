#!/usr/bin/env bash
# Publishes the three sample apps and makes their instances and a navigation set, on the Gateway whose data directory this machine can reach.
#   DBDATAGATEWAY="dotnet dbdatagateway.dll"  ./publish-samples.sh [<samples folder, default: this script's folder>] [-- <extra args for every command, e.g. --repo /data>]
# In the quickstart (docs/apps-testing.md): docker compose -f dist/compose/quickstart.yml -f dist/compose/quickstart-apps.yml exec gateway /samples/apps/publish-samples.sh
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
samples="${1:-$here}"
shift || true
[ "${1:-}" = "--" ] && shift
gateway=${DBDATAGATEWAY:-dotnet /app/dbdatagateway.dll}

apps() { echo "+ dbdatagateway apps $*"; $gateway apps "$@" ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"}; }
EXTRA_ARGS=("$@")

apps validate --dir "$samples/territory-notes"
apps publish --dir "$samples/territory-notes" --note "sample: a code app"
apps instance-save --name notes --app territory-notes --title "Territory notes" --bind territories=default
apps validate --dir "$samples/front-portal"
apps publish --dir "$samples/front-portal" --note "sample: a declared portal"
apps instance-save --name front --app front-portal --title "Front door"
apps validate --dir "$samples/territory-views"
apps publish --dir "$samples/territory-views" --note "sample: an app of views only"
apps instance-save --name views --app territory-views --title "Territory views" --bind territories=default
apps nav-save --name main --title Main --item "notes=Territory notes,views=Territory views"
apps instance-list
