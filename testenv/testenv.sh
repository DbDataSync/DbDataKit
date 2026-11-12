#!/usr/bin/env bash
# The shared test engines (docs/ci-and-testing.md): start, stop, and print the connection variables every suite needs.
#
#   testenv/testenv.sh up mssql postgres         start the engines and wait until they are healthy (`up` alone: the seven everyday ones;
#                                                `up all`: also the slow ones, mssql2025 spark bigquery flight flight-tls, the file servers sftp ssh ftps, and the
#                                                previous releases mssql2022 postgres16)
#   testenv/testenv.sh down                      stop and remove them (and their data)
#   testenv/testenv.sh status                    list the engines that are running
#   testenv/testenv.sh env [product] [--dotenv]  print the variables for what is running
#
# `env` prints the canonical DBDATAKIT_TEST_* variables (below). With a product name it also prints that product's own variable names,
# extended by testenv/env/<product>.map with the few names that suite reads and the canonical set does not have (every name starts DBDATAKIT_TEST_,
# docs/testing-environment.md):
#
#   eval "$(testenv/testenv.sh env dbdatagateway)"                         in a shell
#   testenv/testenv.sh env dbdatagateway --dotenv >> "$GITHUB_ENV"         in a GitHub Actions job, for the steps after it
#
# Canonical variables, each only when its engine is running:
#   mssql     DBDATAKIT_TEST_MSSQL            ADO.NET connection string (SqlClient)
#             DBDATAKIT_TEST_MSSQL_SERVER     host,port       DBDATAKIT_TEST_MSSQL_USER   DBDATAKIT_TEST_MSSQL_PASSWORD
#             DBDATAKIT_TEST_MSSQL_ODBC       ODBC connection string for "ODBC Driver 18 for SQL Server"
#   mssql-target  DBDATAKIT_TEST_MSSQL_TARGET   the same ADO.NET string for the second SQL Server (port 14331)
#             DBDATAKIT_TEST_MSSQL_TARGET_SERVER / _USER / _PASSWORD
#   postgres  DBDATAKIT_TEST_POSTGRES         Npgsql connection string
#             DBDATAKIT_TEST_POSTGRES_HOST / _PORT / _USER / _PASSWORD / _DB
#   postgres-logical  DBDATAKIT_TEST_POSTGRES_LOGICAL   Npgsql connection string for the wal_level=logical server (port 15433, user dbdatasync)
#             DBDATAKIT_TEST_POSTGRES_LOGICAL_HOST / _PORT / _USER / _PASSWORD / _DB / _JDBC_URL
#   mysql     DBDATAKIT_TEST_MYSQL            MySqlConnector connection string (root)
#   mariadb   DBDATAKIT_TEST_MARIADB          the same, for MariaDB
#             DBDATAKIT_TEST_MYSQL_HOST / _PORT / _USER / _PASSWORD / _DB (and DBDATAKIT_TEST_MARIADB_...)
#   oracle    DBDATAKIT_TEST_ORACLE           ODP.NET connection string (user dbkit, service FREEPDB1)
#             DBDATAKIT_TEST_ORACLE_HOST / _PORT / _SERVICE / _USER / _PASSWORD
#             DBDATAKIT_TEST_ORACLE_KV        the same as Host=...;Port=...;User Id=...;Password=...;Service=... (Build's probe reads this form)
# The previous release of each state engine (N-1, plan 29b U01; `up` with no engine named leaves them out):
#   mssql2022 DBDATAKIT_TEST_MSSQL2022        ADO.NET connection string for SQL Server 2022 (port 14333, user sa, the same password)
#             DBDATAKIT_TEST_MSSQL2022_SERVER / _USER / _PASSWORD
#   postgres16 DBDATAKIT_TEST_POSTGRES16      Npgsql connection string for PostgreSQL 16 with pgvector (port 15434, user postgres)
#             DBDATAKIT_TEST_POSTGRES16_HOST / _PORT / _USER / _PASSWORD / _DB
# The slow engines (DbDataBuild's conformance suite; `up` with no engine named leaves them out, `up all` starts everything):
#   mssql2025 DBDATAKIT_TEST_MSSQL2025        ADO.NET connection string for SQL Server 2025 (port 14332)
#   spark     DBDATAKIT_TEST_SPARK            Host=...;Port=10000
#   bigquery  DBDATAKIT_TEST_BIGQUERY         Host=...;Port=9050
#   flight    DBDATAKIT_TEST_FLIGHT           grpc://127.0.0.1:31337;user=sqlflite_username;password=...
#   flight-tls DBDATAKIT_TEST_FLIGHT_TLS      grpc+tls://localhost:31338;user=...;password=...
#             DBDATAKIT_TEST_FLIGHT_TLS_CA    the server's CA certificate, copied out of the container into $DBDATAKIT_TEST_CACHE (default ~/.cache/dbdatakit-testenv)
#
# Set TESTENV_ASSUME_RUNNING="mssql postgres mysql" to print variables without Docker (testenv/selftest.sh does).
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
compose=(docker compose -f "$here/docker-compose.yml")
password="${DBDATAKIT_TEST_PASSWORD:-DbKit_Test_Pw1}"
host=127.0.0.1
engines_all=(mssql mssql-target postgres postgres-logical mysql mariadb oracle mssql2025 spark bigquery flight flight-tls mssql2022 postgres16 sftp ssh ftps)
engines_default=(mssql mssql-target postgres postgres-logical mysql mariadb oracle)   # what `up` with no engine named starts
cache="${DBDATAKIT_TEST_CACHE:-$HOME/.cache/dbdatakit-testenv}"

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
    echo "DBDATAKIT_TEST_MSSQL_SERVER=$server"
    echo "DBDATAKIT_TEST_MSSQL_USER=sa"
    echo "DBDATAKIT_TEST_MSSQL_PASSWORD=$password"
    echo "DBDATAKIT_TEST_MSSQL=Server=$server;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
    echo "DBDATAKIT_TEST_MSSQL_ODBC=Driver={ODBC Driver 18 for SQL Server};Server=$server;UID=sa;PWD=$password;TrustServerCertificate=Yes"
  fi
  if grep -qx mssql-target <<<"$up"; then
    local tserver="$host,14331"
    echo "DBDATAKIT_TEST_MSSQL_TARGET_SERVER=$tserver"
    echo "DBDATAKIT_TEST_MSSQL_TARGET_USER=sa"
    echo "DBDATAKIT_TEST_MSSQL_TARGET_PASSWORD=$password"
    echo "DBDATAKIT_TEST_MSSQL_TARGET=Server=$tserver;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
  fi
  if grep -qx postgres <<<"$up"; then
    echo "DBDATAKIT_TEST_POSTGRES_HOST=$host"
    echo "DBDATAKIT_TEST_POSTGRES_PORT=15432"
    echo "DBDATAKIT_TEST_POSTGRES_USER=postgres"
    echo "DBDATAKIT_TEST_POSTGRES_PASSWORD=$password"
    echo "DBDATAKIT_TEST_POSTGRES_DB=postgres"
    echo "DBDATAKIT_TEST_POSTGRES=Host=$host;Port=15432;Username=postgres;Password=$password;Database=postgres"
  fi
  if grep -qx postgres-logical <<<"$up"; then
    echo "DBDATAKIT_TEST_POSTGRES_LOGICAL_HOST=$host"
    echo "DBDATAKIT_TEST_POSTGRES_LOGICAL_PORT=15433"
    echo "DBDATAKIT_TEST_POSTGRES_LOGICAL_USER=dbdatasync"
    echo "DBDATAKIT_TEST_POSTGRES_LOGICAL_PASSWORD=$password"
    echo "DBDATAKIT_TEST_POSTGRES_LOGICAL_DB=postgres"
    echo "DBDATAKIT_TEST_POSTGRES_LOGICAL=Host=$host;Port=15433;Username=dbdatasync;Password=$password;Database=postgres"
    echo "DBDATAKIT_TEST_POSTGRES_LOGICAL_JDBC_URL=jdbc:postgresql://$host:15433/"
  fi
  local engine port
  for engine in mysql mariadb; do
    if grep -qx "$engine" <<<"$up"; then
      port=13306; [ "$engine" = mariadb ] && port=13307
      local name="${engine^^}"
      echo "DBDATAKIT_TEST_${name}_HOST=$host"
      echo "DBDATAKIT_TEST_${name}_PORT=$port"
      echo "DBDATAKIT_TEST_${name}_USER=root"
      echo "DBDATAKIT_TEST_${name}_PASSWORD=$password"
      echo "DBDATAKIT_TEST_${name}_DB=dbkit"
      echo "DBDATAKIT_TEST_${name}=Server=$host;Port=$port;User ID=root;Password=$password;AllowUserVariables=true"
    fi
  done
  if grep -qx oracle <<<"$up"; then
    echo "DBDATAKIT_TEST_ORACLE_HOST=$host"
    echo "DBDATAKIT_TEST_ORACLE_PORT=15210"
    echo "DBDATAKIT_TEST_ORACLE_SERVICE=FREEPDB1"
    echo "DBDATAKIT_TEST_ORACLE_USER=dbkit"
    echo "DBDATAKIT_TEST_ORACLE_PASSWORD=$password"
    echo "DBDATAKIT_TEST_ORACLE=Data Source=$host:15210/FREEPDB1;User Id=dbkit;Password=$password"
    echo "DBDATAKIT_TEST_ORACLE_KV=Host=$host;Port=15210;User Id=dbkit;Password=$password;Service=FREEPDB1"
  fi
  if grep -qx mssql2025 <<<"$up"; then
    echo "DBDATAKIT_TEST_MSSQL2025=Server=$host,14332;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
  fi
  if grep -qx mssql2022 <<<"$up"; then
    local server22="$host,14333"
    echo "DBDATAKIT_TEST_MSSQL2022_SERVER=$server22"
    echo "DBDATAKIT_TEST_MSSQL2022_USER=sa"
    echo "DBDATAKIT_TEST_MSSQL2022_PASSWORD=$password"
    echo "DBDATAKIT_TEST_MSSQL2022=Server=$server22;User Id=sa;Password=$password;TrustServerCertificate=True;Encrypt=False"
  fi
  if grep -qx postgres16 <<<"$up"; then
    echo "DBDATAKIT_TEST_POSTGRES16_HOST=$host"
    echo "DBDATAKIT_TEST_POSTGRES16_PORT=15434"
    echo "DBDATAKIT_TEST_POSTGRES16_USER=postgres"
    echo "DBDATAKIT_TEST_POSTGRES16_PASSWORD=$password"
    echo "DBDATAKIT_TEST_POSTGRES16_DB=postgres"
    echo "DBDATAKIT_TEST_POSTGRES16=Host=$host;Port=15434;Username=postgres;Password=$password;Database=postgres"
  fi
  if grep -qx sftp <<<"$up"; then
    echo "DBDATAKIT_TEST_SFTP_HOST=$host"
    echo "DBDATAKIT_TEST_SFTP_PORT=13022"
    echo "DBDATAKIT_TEST_SFTP_USER=foo"
    echo "DBDATAKIT_TEST_SFTP_PASSWORD=$password"
    echo "DBDATAKIT_TEST_SFTP_ROOT=/upload"
    echo "DBDATAKIT_TEST_SFTP_KEY_FILE=$cache/sftp/client_ed25519"
  fi
  if grep -qx ssh <<<"$up"; then
    echo "DBDATAKIT_TEST_SSH_HOST=$host"
    echo "DBDATAKIT_TEST_SSH_PORT=13023"
    echo "DBDATAKIT_TEST_SSH_USER=foo"
    echo "DBDATAKIT_TEST_SSH_PASSWORD=$password"
    echo "DBDATAKIT_TEST_SSH_ROOT=/config"
  fi
  if grep -qx ftps <<<"$up"; then
    echo "DBDATAKIT_TEST_FTPS_HOST=$host"
    echo "DBDATAKIT_TEST_FTPS_PORT=13021"
    echo "DBDATAKIT_TEST_FTPS_USER=foo"
    echo "DBDATAKIT_TEST_FTPS_PASSWORD=$password"
    # the certificate is made when the container starts: its SHA-256 (what `certificate_sha256` pins), only with a real container
    if [ -z "${TESTENV_ASSUME_RUNNING+x}" ]; then
      fp="$(docker exec dbkit-test-ftps openssl x509 -in /etc/ssl/private/pure-ftpd.pem -noout -fingerprint -sha256 2>/dev/null | sed 's/.*=//; s/://g')"
      [ -n "$fp" ] && echo "DBDATAKIT_TEST_FTPS_CERT_SHA256=$fp"
    fi
  fi
  if grep -qx spark <<<"$up"; then echo "DBDATAKIT_TEST_SPARK=Host=$host;Port=10000"; fi
  if grep -qx bigquery <<<"$up"; then echo "DBDATAKIT_TEST_BIGQUERY=Host=$host;Port=9050"; fi
  if grep -qx flight <<<"$up"; then
    echo "DBDATAKIT_TEST_FLIGHT=grpc://$host:31337;user=sqlflite_username;password=$password"
  fi
  if grep -qx flight-tls <<<"$up"; then
    # `localhost`, not the address: the server's certificate is made for that name
    echo "DBDATAKIT_TEST_FLIGHT_TLS=grpc+tls://localhost:31338;user=sqlflite_username;password=$password"
    # The certificate is made when the container starts: copy its CA out (only with a real container), and name the file either way.
    if [ -n "${TESTENV_ASSUME_RUNNING+x}" ]; then
      echo "DBDATAKIT_TEST_FLIGHT_TLS_CA=$cache/flight-root-ca.pem"
    elif mkdir -p "$cache" && docker cp dbkit-test-flight-tls:/opt/sqlflite/tls/root-ca.pem "$cache/flight-root-ca.pem" >/dev/null 2>&1; then
      echo "DBDATAKIT_TEST_FLIGHT_TLS_CA=$cache/flight-root-ca.pem"
    fi
  fi
}

# The key pair sftp's tests sign in with: made once in the cache folder, by the ssh-keygen of the sftp image itself (nothing to install), and mounted by the compose file.
prepare() {
  local e
  for e in "$@"; do
    case "$e" in
      sftp)
        mkdir -p "$cache/sftp"
        if [ ! -f "$cache/sftp/client_ed25519" ]; then
          docker run --rm -v "$cache/sftp:/k" --entrypoint sh "${DBDATAKIT_TEST_SFTP_IMAGE:-atmoz/sftp:alpine}" -c 'ssh-keygen -q -t ed25519 -N "" -C dbdatakit-test -f /k/client_ed25519 && chmod 644 /k/client_ed25519 /k/client_ed25519.pub'
        fi
        export DBDATAKIT_TEST_SFTP_KEYS="$cache/sftp"
        ;;
    esac
  done
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
    prepare "$@"
    # --build: postgres-logical is built from testenv/postgres-logical (a no-op for the engines that are pulled).
    # One retry: a container that exits at start before answering (SQL Server 2025 in CI, docs/ci-lessons.md) is removed and started again.
    if ! "${compose[@]}" "${PROFILES[@]}" up -d --build --wait --wait-timeout 240; then
      echo "testenv: an engine did not start; the failed containers' logs, then one more try" >&2
      "${compose[@]}" "${PROFILES[@]}" ps -a >&2 || true
      "${compose[@]}" "${PROFILES[@]}" logs --tail 40 >&2 || true
      "${compose[@]}" "${PROFILES[@]}" down --remove-orphans >&2 || true
      "${compose[@]}" "${PROFILES[@]}" up -d --build --wait --wait-timeout 240
    fi
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
