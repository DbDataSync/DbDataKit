#!/usr/bin/env bash
# Makes the quickstart ready to try app publishing: pairs the Store with the Proxy as a publisher, publishes the sample model `territories`, and publishes the sample apps.
# Run from the repository root after: docker compose -f dist/compose/quickstart.yml -f dist/compose/quickstart-apps.yml up -d --build --wait
set -euo pipefail
files=(-f dist/compose/quickstart.yml -f dist/compose/quickstart-apps.yml)
proxy() { docker compose "${files[@]}" exec -T proxy dotnet /app/dbdataproxy.dll "$@"; }
store() { docker compose "${files[@]}" exec -T store dbdatastore "$@"; }
paired=$(proxy server pair --max-role publisher)
code=$(sed -n 's/.*Pairing code: \([^ ]*\).*/\1/p' <<<"$paired" | head -n 1)
[ -n "$code" ] || { echo "no pairing code in: $paired"; exit 1; }
store proxy add edge --url https://proxy:8816 --code "$code"
store model put --file /samples/apps/territories.yaml
store model publish territories --proxy edge
docker compose "${files[@]}" exec -T -e DBDATAPROXY="dotnet /app/dbdataproxy.dll" proxy bash /samples/apps/publish-samples.sh
echo "Done. Sign in at https://localhost:8817 (first administrator: docker compose ... exec proxy cat /data/identity/FIRST-RUN.txt), then see docs/apps-testing.md."
