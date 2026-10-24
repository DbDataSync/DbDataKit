#!/usr/bin/env bash
# The shared test engines (docs/plans/11-ci-and-testing.md): start, stop, and print the connection variables every suite needs.
#
#   testenv/testenv.sh up mssql postgres         start the engines and wait until they are healthy
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
#   postgres  DBKIT_TEST_POSTGRES         Npgsql connection string
#             DBKIT_TEST_POSTGRES_HOST / _PORT / _USER / _PASSWORD / _DB
#   mysql     DBKIT_TEST_MYSQL            MySqlConnector connection string (root)
#   mariadb   DBKIT_TEST_MARIADB          the same, for MariaDB
#             DBKIT_TEST_MYSQL_HOST / _PORT / _USER / _PASSWORD / _DB (and DBKIT_TEST_MARIADB_...)
#   oracle    DBKIT_TEST_ORACLE           ODP.NET connection string (user dbkit, service FREEPDB1)
#             DBKIT_TEST_ORACLE_HOST / _PORT / _SERVICE / _USER / _PASSWORD
#
# Set TESTENV_ASSUME_RUNNING="mssql postgres mysql" to print variables without Docker (testenv/selftest.sh does).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose=(docker compose -f "$here/docker-compose.yml")
password="${DBKIT_TEST_PASSWORD:-DbKit_Test_Pw1}"
host=127.0.0.1
engines_all=(mssql postgres mysql mariadb oracle)

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
  if grep -qx postgres <<<"$up"; then
    echo "DBKIT_TEST_POSTGRES_HOST=$host"
    echo "DBKIT_TEST_POSTGRES_PORT=15432"
    echo "DBKIT_TEST_POSTGRES_USER=postgres"
    echo "DBKIT_TEST_POSTGRES_PASSWORD=$password"
    echo "DBKIT_TEST_POSTGRES_DB=postgres"
    echo "DBKIT_TEST_POSTGRES=Host=$host;Port=15432;Username=postgres;Password=$password;Database=postgres"
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
  fi
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
    [ "$#" -gt 0 ] || set -- "${engines_all[@]}"
    profiles_for "$@"
    "${compose[@]}" "${PROFILES[@]}" up -d --wait --wait-timeout 240
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
    sed -n '2,12p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//' >&2
    exit 2
    ;;
esac
