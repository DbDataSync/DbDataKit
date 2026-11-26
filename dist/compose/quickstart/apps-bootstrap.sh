#!/usr/bin/env bash
# Makes the quickstart ready to try app publishing: publishes the sample apps. The sample model `territories` they read is published from a store project of the Workspace (plan 33 SC23 deleted
# the Store server this script paired with the Gateway); until it is, the apps answer with no rows.
# Run from the repository root after: docker compose -f dist/compose/quickstart.yml -f dist/compose/quickstart-apps.yml up -d --build --wait
set -euo pipefail
files=(-f dist/compose/quickstart.yml -f dist/compose/quickstart-apps.yml)
docker compose "${files[@]}" exec -T -e DBDATAGATEWAY="dotnet /app/dbdatagateway.dll" gateway bash /samples/apps/publish-samples.sh
echo "Done. Sign in at https://localhost:7823 (first administrator: docker compose ... exec gateway cat /data/identity/FIRST-RUN.txt), then see docs/apps-testing.md."
