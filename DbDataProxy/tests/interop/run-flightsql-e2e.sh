#!/usr/bin/env bash
# The shared Flight SQL client (DbDataKit.Connections.FlightSql) against a real `dbdataproxy server` in front of SQL Server and DuckDB (plan 13, A18).
#
# Needs: the .NET SDK, curl, and a SQL Server connection string in DBKIT_TEST_MSSQL (testenv/testenv.sh up mssql; eval "$(testenv/testenv.sh env dbdatakit)").
# Builds the proxy, starts it on a free port with a throwaway data directory and a writable DuckDB file, waits for it to be ready, runs DbDataKit.Connections.FlightSql.Tests
# with DBPROXY_E2E_* set (so its end-to-end class runs and may not skip), and stops the proxy.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../.." && pwd)"
config="${CONFIGURATION:-Release}"
: "${DBKIT_TEST_MSSQL:?set DBKIT_TEST_MSSQL to a SQL Server connection string (eval \"\$(testenv/testenv.sh env dbdatakit)\")}"
port="${DBPROXY_E2E_PORT:-18815}"
user=e2e
password="e2e-$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"

dotnet build "$root/DbDataProxy/src/DbDataProxy.Server" -c "$config" --nologo -v q
dll="$(ls "$root"/DbDataProxy/src/DbDataProxy.Server/bin/"$config"/net*/dbdataproxy.dll | head -1)"

tmp="$(mktemp -d)"
proxy_pid=""
cleanup() {
  status=$?
  [ -n "$proxy_pid" ] && kill "$proxy_pid" 2>/dev/null || true
  if [ "$status" -ne 0 ] && [ -f "$tmp/proxy.log" ]; then echo "---- proxy log ----"; tail -n "${DBPROXY_E2E_LOG_LINES:-60}" "$tmp/proxy.log"; fi
  rm -rf "$tmp"
  exit "$status"
}
trap cleanup EXIT

hash="$(printf '%s\n' "$password" | dotnet "$dll" secret hash | tail -n 1 | tr -d '\r\n')"

export DBPROXY_Proxy__SchemaVersion=2
export DBPROXY_Proxy__DataDirectory="$tmp"
export DBPROXY_Proxy__Listen__Address=127.0.0.1
export DBPROXY_Proxy__Listen__Port="$port"
export DBPROXY_Proxy__Users__0__Name="$user"
export DBPROXY_Proxy__Users__0__PasswordHash="$hash"
export DBPROXY_Proxy__Users__0__DataSources__0=duck
export DBPROXY_Proxy__Users__0__DataSources__1=mssql
export DBPROXY_Proxy__DataSources__duck__Provider=duckdb
export DBPROXY_Proxy__DataSources__duck__ConnectionString="Data Source=$tmp/e2e.duckdb"
export DBPROXY_Proxy__DataSources__duck__AllowWrites=true
export DBPROXY_Proxy__DataSources__mssql__Provider=sqlserver
export DBPROXY_Proxy__DataSources__mssql__ConnectionString="$DBKIT_TEST_MSSQL"

dotnet "$dll" server run >"$tmp/proxy.log" 2>&1 &
proxy_pid=$!

for _ in $(seq 1 60); do
  if curl -ksf --http2 "https://127.0.0.1:$port/health/ready" >/dev/null 2>&1; then ready=1; break; fi
  kill -0 "$proxy_pid" 2>/dev/null || { echo "the proxy exited before it was ready"; exit 1; }
  sleep 1
done
[ "${ready:-0}" = 1 ] || { echo "the proxy was not ready after 60 s"; exit 1; }

export DBPROXY_E2E_ADDRESS="https://127.0.0.1:$port"
export DBPROXY_E2E_USER="$user"
export DBPROXY_E2E_PASSWORD="$password"
export DBPROXY_E2E_REQUIRED=true
cd "$root/DbDataKit"
dotnet test --project tests/DbDataKit.Connections.FlightSql.Tests -c "$config" "$@"
