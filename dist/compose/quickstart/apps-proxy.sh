#!/bin/sh
# The Proxy container's entry point when the apps host is on (quickstart-apps.yml): register the apps host as a client of the identity home once, then start the server.
set -eu
dll=${DBDATAPROXY_DLL:-/app/dbdataproxy.dll}
data=${DBPROXY_Proxy__DataDirectory:-/data}
if [ ! -s "$data/apps-client-id" ]; then
  dotnet "$dll" identity client-add --name "Apps host" --public --redirect "https://localhost:8818/apps/-/callback" --format json \
    | sed -n 's/.*"client_id": *"\([^"]*\)".*/\1/p' | head -n 1 > "$data/apps-client-id"
  [ -s "$data/apps-client-id" ] || { echo "apps-proxy.sh: identity client-add gave no client id" >&2; rm -f "$data/apps-client-id"; exit 1; }
fi
DBPROXY_Proxy__Apps__Home__ClientId="$(cat "$data/apps-client-id")"
export DBPROXY_Proxy__Apps__Home__ClientId
exec dotnet "$dll" server run
