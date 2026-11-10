#!/usr/bin/env bash
# The shared Flight SQL client (DbDataKit.Connections.FlightSql) against a real `dbdatagateway server` in front of SQL Server and DuckDB (plan 13, A18).
#
# Needs: the .NET SDK, curl, and a SQL Server connection string in DBKIT_TEST_MSSQL (testenv/testenv.sh up mssql; eval "$(testenv/testenv.sh env dbdatakit)").
# Builds the gateway, starts it on a free port with a throwaway data directory and a writable DuckDB file, waits for it to be ready, runs DbDataKit.Connections.FlightSql.Tests
# with DBGATEWAY_E2E_* set (so its end-to-end class runs and may not skip), and stops the gateway.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
config="${CONFIGURATION:-Release}"
: "${DBKIT_TEST_MSSQL:?set DBKIT_TEST_MSSQL to a SQL Server connection string (eval \"\$(testenv/testenv.sh env dbdatakit)\")}"
port="${DBGATEWAY_E2E_PORT:-18815}"
user=e2e

dotnet build "$root/DbDataGateway/src/DbDataGateway.Server" -c "$config" --nologo -v q
dll="$(ls "$root"/DbDataGateway/src/DbDataGateway.Server/bin/"$config"/net*/dbdatagateway.dll | head -1)"

tmp="$(mktemp -d)"
gateway_pid=""
cleanup() {
  status=$?
  if [ -n "$gateway_pid" ]; then
    kill "$gateway_pid" 2>/dev/null || true
    # the gateway writes its state and log while it shuts down: remove the directory only after it has gone (bounded wait, then a hard stop)
    for _ in $(seq 1 20); do kill -0 "$gateway_pid" 2>/dev/null || break; sleep 0.5; done
    kill -9 "$gateway_pid" 2>/dev/null || true
    wait "$gateway_pid" 2>/dev/null || true
  fi
  if [ "$status" -ne 0 ] && [ -f "$tmp/gateway.log" ]; then echo "---- gateway log ----"; tail -n "${DBGATEWAY_E2E_LOG_LINES:-60}" "$tmp/gateway.log"; fi
  rm -rf "$tmp" || { sleep 2; rm -rf "$tmp" || true; }   # a leftover temp directory is not a failure of the test
  exit "$status"
}
trap cleanup EXIT

export DBDATAKIT_Gateway__SchemaVersion=2
export DBDATAKIT_DATA_GATEWAY="$tmp"
export DBDATAKIT_Gateway__Listen__Address=127.0.0.1
export DBDATAKIT_Gateway__Listen__Port="$port"
export DBDATAKIT_Gateway__DataSources__duck__Provider=duckdb
export DBDATAKIT_Gateway__DataSources__duck__ConnectionString="Data Source=$tmp/e2e.duckdb"
export DBDATAKIT_Gateway__DataSources__duck__AllowWrites=true
# who may use a data source is a matter of the identity home's groups (P13); the e2e user is a service account of the home, so the sources are open to everyone
export DBDATAKIT_Gateway__DataSources__duck__Everyone=true
export DBDATAKIT_Gateway__DataSources__mssql__Everyone=true
export DBDATAKIT_Gateway__DataSources__mssql__Provider=sqlserver
export DBDATAKIT_Gateway__DataSources__mssql__ConnectionString="$DBKIT_TEST_MSSQL"

# the e2e user: a service account of the identity home (in the gateway's state, under the data directory) and its personal access token, which is the password
dotnet "$dll" identity service-account-add --name "$user" --repo "$tmp" >/dev/null
password="$(dotnet "$dll" identity token-create --account "$user" --name e2e --repo "$tmp" | grep -o 'ddk_[A-Za-z0-9_-]*' | head -n 1)"
[ -n "$password" ] || { echo "no token was made for the e2e service account"; exit 1; }

dotnet "$dll" server run >"$tmp/gateway.log" 2>&1 &
gateway_pid=$!

for _ in $(seq 1 60); do
  if curl -ksf --http2 "https://127.0.0.1:$port/health/ready" >/dev/null 2>&1; then ready=1; break; fi
  kill -0 "$gateway_pid" 2>/dev/null || { echo "the gateway exited before it was ready"; exit 1; }
  sleep 1
done
[ "${ready:-0}" = 1 ] || { echo "the gateway was not ready after 60 s"; exit 1; }

export DBGATEWAY_E2E_ADDRESS="https://127.0.0.1:$port"
export DBGATEWAY_E2E_USER="$user"
export DBGATEWAY_E2E_PASSWORD="$password"
export DBGATEWAY_E2E_REQUIRED=true
cd "$root/DbDataKit"
dotnet test --project tests/DbDataKit.Connections.FlightSql.Tests -c "$config" "$@"
