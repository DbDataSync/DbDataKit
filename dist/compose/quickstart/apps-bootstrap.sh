#!/usr/bin/env bash
# Makes the quickstart ready to try app publishing: pairs the Store with the Gateway as a publisher, publishes the sample model `territories`, and publishes the sample apps.
# Run from the repository root after: docker compose -f dist/compose/quickstart.yml -f dist/compose/quickstart-apps.yml up -d --build --wait
set -euo pipefail
files=(-f dist/compose/quickstart.yml -f dist/compose/quickstart-apps.yml)
gateway() { docker compose "${files[@]}" exec -T gateway dotnet /app/dbdatagateway.dll "$@"; }
store() { docker compose "${files[@]}" exec -T store dbdatastore "$@"; }
paired=$(gateway server pair --max-role publisher)
code=$(sed -n 's/.*Pairing code: \([^ ]*\).*/\1/p' <<<"$paired" | head -n 1)
[ -n "$code" ] || { echo "no pairing code in: $paired"; exit 1; }
store gateway add edge --url https://gateway:8816 --code "$code"
store model put --file /samples/apps/territories.yaml
store model publish territories --proxy edge
docker compose "${files[@]}" exec -T -e DBDATAGATEWAY="dotnet /app/dbdatagateway.dll" gateway bash /samples/apps/publish-samples.sh
echo "Done. Sign in at https://localhost:8817 (first administrator: docker compose ... exec gateway cat /data/identity/FIRST-RUN.txt), then see docs/apps-testing.md."
