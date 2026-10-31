#!/usr/bin/env bash
# The shared test engines (docs/plans/11-ci-and-testing.md): start, stop, and print the connection variables every suite needs.
#
#   testenv/testenv.sh up mssql postgres         start the engines and wait until they are healthy (`up` alone: the seven everyday ones;
#                                                `up all`: also the slow ones, mssql2025 spark bigquery flight flight-tls)
#   testenv/testenv.sh down                      stop and remove them (and their data)
#   testenv/testenv.sh status                    list the engines that are running
#   testenv/testenv.sh env [product] [--dotenv]  print the variables for what is running
#
# `env` prints the canonical DBKIT_TEST_* variables (below). With a product name it also prints that product's own variable names,
# translated by testenv/env/<product>.map, so each suite keeps reading the names it always has:
#
#   eval "$(testenv/testenv.sh env dbdataproxy)"                         in a shell
#   testenv/testenv.sh env dbdataproxy --dotenv >> "$GITHUB_ENV"         in a GitHub Actions job, for the steps after it
#
# Canonical variables, each only when its engine is running:
#   mssql     DBKIT_TEST_MSSQL            ADO.NET connection string (SqlClient)
#             DBKIT_TEST_MSSQL_SERVER     host,port       DBKIT_TEST_MSSQL_USER   DBKIT_TEST_MSSQL_PASSWORD
#             DBKIT_TEST_MSSQL_ODBC       ODBC connection string for "ODBC Driver 18 for SQL Server"
#   mssql-target  DBKIT_TEST_MSSQL_TARGET   the same ADO.NET string for the second SQL Server (port 14331)
#             DBKIT_TEST_MSSQL_TARGET_SERVER / _USER / _PASSWORD
#   postgres  DBKIT_TEST_POSTGRES         Npgsql connection string
#             DBKIT_TEST_POSTGRES_HOST / _PORT / _USER / _PASSWORD / _DB
#   postgres-logical  DBKIT_TEST_POSTGRES_LOGICAL   Npgsql connection string for the wal_level=logical server (port 15433, user dbdatasync)
#             DBKIT_TEST_POSTGRES_LOGICAL_HOST / _PORT / _USER / _PASSWORD / _DB / _JDBC_URL
#   mysql     DBKIT_TEST_MYSQL            MySqlConnector connection string (root)
#   mariadb   DBKIT_TEST_MARIADB          the same, for MariaDB
#             DBKIT_TEST_MYSQL_HOST / _PORT / _USER / _PASSWORD / _DB (and DBKIT_TEST_MARIADB_...)
#   oracle    DBKIT_TEST_ORACLE           ODP.NET connection string (user dbkit, service FREEPDB1)
#             DBKIT_TEST_ORACLE_HOST / _PORT / _SERVICE / _USER / _PASSWORD
#             DBKIT_TEST_ORACLE_KV        the same as Host=...;Port=...;User Id=...;Password=...;Service=... (Build's probe reads this form)
# The slow engines (DbDataBuild's conformance suite; `up` with no engine named leaves them out, `up all` starts everything):
#   mssql2025 DBKIT_TEST_MSSQL2025        ADO.NET connection string for SQL Server 2025 (port 14332)
#   spark     DBKIT_TEST_SPARK            Host=...;Port=10000
#   bigquery  DBKIT_TEST_BIGQUERY         Host=...;Port=9050
#   flight    DBKIT_TEST_FLIGHT           grpc://127.0.0.1:31337;user=sqlflite_username;password=...
#   flight-tls DBKIT_TEST_FLIGHT_TLS      grpc+tls://localhost:31338;user=...;password=...
#             DBKIT_TEST_FLIGHT_TLS_CA    the server's CA certificate, copied out of the container into $DBKIT_TEST_CACHE (default ~/.cache/dbdatakit-testenv)
#
# Set TESTENV_ASSUME_RUNNING="mssql postgres mysql" to print variables without Docker (testenv/selftest.sh does).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose=(docker compose -f "$here/docker-compose.yml")
password="${DBKIT_TEST_PASSWORD:-DbKit_Test_Pw1}"
host=127.0.0.1
engines_all=(mssql mssql-target postgres postgres-logical mysql mariadb oracle mssql2025 spark bigquery flight flight-tls)
engines_default=(mssql mssql-target postgres postgres-logical mysql mariadb oracle)   # what `up` with no engine named starts
cache="${DBKIT_TEST_CACHE:-$HOME/.cache/dbdatakit-testenv}"

running() {
  if [ -n "${TESTENV_ASSUME_RUNNING+x}" ]; then
    # shellcheck disable=SC2086
    printf '%s\n' $TESTENV_ASSUME_RUNNING
  else
    # --profile * makes compose consider every engine, not just the ones named.
    "${compose[@]}" --profile '*' ps --services --status running 2>/dev/null || true
  fi
}

# Sets PROFILES to the --profile arguments for the named engines; refuses an engine it does not know.
profiles_for() {
  PROFILES=()
  local e
  for e in "$@"; do
    case " ${engines_all[*]} " in
      *" $e "*) PROFILES+=(--profile "$e") ;;
      *) echo "unknown engine '$e' (known: ${engines_all[*]})" >&2; exit 2 ;;
    esac
  done
}

# name=value lines for every canonical variable whose engine is running.
canonical() {
  local up; up="$(running)"
  if grep -qx mssql <<<"$up"; then
    local server="$host,14330"
    echo "DBKIT_TEST_MSSQL_SERVER=$server"
    echo "DBKIT_TEST_MSSQL_USER=sa"
    echo "DBKIT_TEST_MSSQL_PASSWORD=$password"
    echo "DBKIT_TEST_MSSQL=Server=$server;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
    echo "DBKIT_TEST_MSSQL_ODBC=Driver={ODBC Driver 18 for SQL Server};Server=$server;UID=sa;PWD=$password;TrustServerCertificate=Yes"
  fi
  if grep -qx mssql-target <<<"$up"; then
    local tserver="$host,14331"
    echo "DBKIT_TEST_MSSQL_TARGET_SERVER=$tserver"
    echo "DBKIT_TEST_MSSQL_TARGET_USER=sa"
    echo "DBKIT_TEST_MSSQL_TARGET_PASSWORD=$password"
    echo "DBKIT_TEST_MSSQL_TARGET=Server=$tserver;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
  fi
  if grep -qx postgres <<<"$up"; then
    echo "DBKIT_TEST_POSTGRES_HOST=$host"
    echo "DBKIT_TEST_POSTGRES_PORT=15432"
    echo "DBKIT_TEST_POSTGRES_USER=postgres"
    echo "DBKIT_TEST_POSTGRES_PASSWORD=$password"
    echo "DBKIT_TEST_POSTGRES_DB=postgres"
    echo "DBKIT_TEST_POSTGRES=Host=$host;Port=15432;Username=postgres;Password=$password;Database=postgres"
  fi
  if grep -qx postgres-logical <<<"$up"; then
    echo "DBKIT_TEST_POSTGRES_LOGICAL_HOST=$host"
    echo "DBKIT_TEST_POSTGRES_LOGICAL_PORT=15433"
    echo "DBKIT_TEST_POSTGRES_LOGICAL_USER=dbdatasync"
    echo "DBKIT_TEST_POSTGRES_LOGICAL_PASSWORD=$password"
    echo "DBKIT_TEST_POSTGRES_LOGICAL_DB=postgres"
    echo "DBKIT_TEST_POSTGRES_LOGICAL=Host=$host;Port=15433;Username=dbdatasync;Password=$password;Database=postgres"
    echo "DBKIT_TEST_POSTGRES_LOGICAL_JDBC_URL=jdbc:postgresql://$host:15433/"
  fi
  local engine port
  for engine in mysql mariadb; do
    if grep -qx "$engine" <<<"$up"; then
      port=13306; [ "$engine" = mariadb ] && port=13307
      local name="${engine^^}"
      echo "DBKIT_TEST_${name}_HOST=$host"
      echo "DBKIT_TEST_${name}_PORT=$port"
      echo "DBKIT_TEST_${name}_USER=root"
      echo "DBKIT_TEST_${name}_PASSWORD=$password"
      echo "DBKIT_TEST_${name}_DB=dbkit"
      echo "DBKIT_TEST_${name}=Server=$host;Port=$port;User ID=root;Password=$password;AllowUserVariables=true"
    fi
  done
  if grep -qx oracle <<<"$up"; then
    echo "DBKIT_TEST_ORACLE_HOST=$host"
    echo "DBKIT_TEST_ORACLE_PORT=15210"
    echo "DBKIT_TEST_ORACLE_SERVICE=FREEPDB1"
    echo "DBKIT_TEST_ORACLE_USER=dbkit"
    echo "DBKIT_TEST_ORACLE_PASSWORD=$password"
    echo "DBKIT_TEST_ORACLE=Data Source=$host:15210/FREEPDB1;User Id=dbkit;Password=$password"
    echo "DBKIT_TEST_ORACLE_KV=Host=$host;Port=15210;User Id=dbkit;Password=$password;Service=FREEPDB1"
  fi
  if grep -qx mssql2025 <<<"$up"; then
    echo "DBKIT_TEST_MSSQL2025=Server=$host,14332;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
  fi
  if grep -qx spark <<<"$up"; then echo "DBKIT_TEST_SPARK=Host=$host;Port=10000"; fi
  if grep -qx bigquery <<<"$up"; then echo "DBKIT_TEST_BIGQUERY=Host=$host;Port=9050"; fi
  if grep -qx flight <<<"$up"; then
    echo "DBKIT_TEST_FLIGHT=grpc://$host:31337;user=sqlflite_username;password=$password"
  fi
  if grep -qx flight-tls <<<"$up"; then
    # `localhost`, not the address: the server's certificate is made for that name
    echo "DBKIT_TEST_FLIGHT_TLS=grpc+tls://localhost:31338;user=sqlflite_username;password=$password"
    # The certificate is made when the container starts: copy its CA out (only with a real container), and name the file either way.
    if [ -n "${TESTENV_ASSUME_RUNNING+x}" ]; then
      echo "DBKIT_TEST_FLIGHT_TLS_CA=$cache/flight-root-ca.pem"
    elif mkdir -p "$cache" && docker cp dbkit-test-flight-tls:/opt/sqlflite/tls/root-ca.pem "$cache/flight-root-ca.pem" >/dev/null 2>&1; then
      echo "DBKIT_TEST_FLIGHT_TLS_CA=$cache/flight-root-ca.pem"
    fi
  fi
}

# Readiness of the engines that have no compose healthcheck: wait_ready <engine> returns when it answers, or exits 1 after its timeout.
wait_until() {   # wait_until <seconds> <description> <command...>
  local seconds=$1 what=$2; shift 2
  for _ in $(seq 1 "$seconds"); do "$@" >/dev/null 2>&1 && return 0; sleep 1; done
  echo "timed out waiting for $what" >&2; return 1
}
logged() { "${compose[@]}" --profile "$1" logs "$1" 2>&1 | grep -q "$2"; }
wait_ready() {
  case "$1" in
    spark) wait_until 180 "Spark's Thrift server" logged spark "Started ThriftHttpCLIService" ;;
    bigquery) wait_until 60 "the BigQuery emulator" curl -fs "http://$host:9050/bigquery/v2/projects/ddb/datasets" ;;
    flight) wait_until 90 "Flight SQL" logged flight "SQLFlite server - started" ;;
    flight-tls) wait_until 90 "Flight SQL (TLS)" logged flight-tls "SQLFlite server - started" ;;
  esac
}

# Translate with testenv/env/<product>.map. Two line forms (blank lines and # comments are ignored):
#   TARGET <- SOURCE              TARGET gets SOURCE's value, when SOURCE is set
#   TARGET = literal if SOURCE    TARGET gets the literal, when SOURCE is set
mapped() {
  local product="$1" map="$here/env/$1.map"
  [ -f "$map" ] || { echo "no mapping for '$product' (looked for $map)" >&2; return 2; }
  local -A vars=()
  local line k v
  while IFS= read -r line; do k="${line%%=*}"; v="${line#*=}"; vars["$k"]="$v"; done < <(canonical)
  local target rest
  while IFS= read -r line; do
    line="${line%%#*}"; line="${line#"${line%%[![:space:]]*}"}"; line="${line%"${line##*[![:space:]]}"}"
    [ -z "$line" ] && continue
    if [[ "$line" =~ ^([A-Za-z0-9_]+)[[:space:]]*\<-[[:space:]]*([A-Za-z0-9_]+)$ ]]; then
      target="${BASH_REMATCH[1]}"; rest="${BASH_REMATCH[2]}"
      if [ -n "${vars[$rest]+x}" ]; then echo "$target=${vars[$rest]}"; fi
    elif [[ "$line" =~ ^([A-Za-z0-9_]+)[[:space:]]*=[[:space:]]*(.*)[[:space:]]+if[[:space:]]+([A-Za-z0-9_]+)$ ]]; then
      target="${BASH_REMATCH[1]}"
      if [ -n "${vars[${BASH_REMATCH[3]}]+x}" ]; then echo "$target=${BASH_REMATCH[2]}"; fi
    else
      echo "$map: cannot read line: $line" >&2; return 2
    fi
  done < "$map"
}

emit() {  # name=value lines on stdin -> shell exports, or dotenv lines
  local format="$1" line k v
  while IFS= read -r line; do
    k="${line%%=*}"; v="${line#*=}"
    if [ "$format" = dotenv ]; then printf '%s=%s\n' "$k" "$v"
    else printf "export %s='%s'\n" "$k" "${v//\'/\'\\\'\'}"; fi
  done
}

cmd="${1:-}"; shift || true
case "$cmd" in
  up)
    if [ "$#" -eq 0 ]; then set -- "${engines_default[@]}"; elif [ "$1" = all ]; then set -- "${engines_all[@]}"; fi
    profiles_for "$@"
    # --build: postgres-logical is built from testenv/postgres-logical (a no-op for the engines that are pulled).
    "${compose[@]}" "${PROFILES[@]}" up -d --build --wait --wait-timeout 240
    # The slow engines have no healthcheck in their (minimal) images: wait for them from here.
    for e in "$@"; do wait_ready "$e"; done
    ;;
  down)
    "${compose[@]}" --profile '*' down -v --remove-orphans
    ;;
  status)
    running
    ;;
  env)
    product=""; format=shell
    for a in "$@"; do case "$a" in --dotenv) format=dotenv ;; -*) echo "unknown option $a" >&2; exit 2 ;; *) product="$a" ;; esac; done
    { canonical; if [ -n "$product" ]; then mapped "$product"; fi; } | emit "$format"
    ;;
  *)
    sed -n '2,13p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
    exit 2
    ;;
esac
