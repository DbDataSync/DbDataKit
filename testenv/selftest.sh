#!/usr/bin/env bash
# Checks the variable generation and the product mappings without Docker (TESTENV_ASSUME_RUNNING). Fast; CI runs it.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=$((fail+1)); fi; }
t() { TESTENV_ASSUME_RUNNING="$1" "$here/testenv.sh" env "${@:2}"; }

check "nothing running prints nothing" '[ -z "$(t "" )" ]'
check "mssql gives the canonical SQL Server variables" '[[ "$(t mssql)" == *"DBKIT_TEST_MSSQL_SERVER"* && "$(t mssql)" != *POSTGRES* ]]'
check "postgres gives the canonical PostgreSQL variables" '[[ "$(t postgres)" == *"DBKIT_TEST_POSTGRES="* && "$(t postgres)" != *MSSQL* ]]'
check "the ADO string is complete" '[[ "$(t mssql --dotenv)" == *"DBKIT_TEST_MSSQL=Server=127.0.0.1,14330;User Id=sa;Password=DbKit_Test_Pw1;TrustServerCertificate=True;Encrypt=False"* ]]'
check "the ODBC string names the driver" '[[ "$(t mssql --dotenv)" == *"DBKIT_TEST_MSSQL_ODBC=Driver={ODBC Driver 18 for SQL Server};Server=127.0.0.1,14330;"* ]]'

# Proxy mapping
out="$(t mssql dbdataproxy --dotenv)"
check "proxy: server, user, password" '[[ "$out" == *"DBPROXY_TEST_SQLSERVER=127.0.0.1,14330"* && "$out" == *"DBPROXY_TEST_SQLSERVER_USER=sa"* && "$out" == *"DBPROXY_TEST_SQLSERVER_PASSWORD=DbKit_Test_Pw1"* ]]'
check "proxy: SQL Server is required when it was started" '[[ "$out" == *"DBPROXY_REQUIRE_SQLSERVER=true"* ]]'
out="$(t postgres dbdataproxy --dotenv)"
check "proxy: nothing about SQL Server mapped without it" '[[ "$out" != *DBPROXY_TEST_SQLSERVER* && "$out" != *DBPROXY_REQUIRE_SQLSERVER* ]]'
check "proxy: the PostgreSQL string, and it is required when it was started" '[[ "$out" == *"DBPROXY_TEST_POSTGRES=Host=127.0.0.1;Port=15432;"* && "$out" == *"DBPROXY_REQUIRE_POSTGRES=true"* ]]'

# Shared libraries mapping
out="$(t "mssql postgres" dbdatakit --dotenv)"
check "kit: a full connection string and separate parts" '[[ "$out" == *"DBDATA_TEST_SQLSERVER=Server=127.0.0.1,14330;"* && "$out" == *"DBDATA_TEST_SQLSERVER_SERVER=127.0.0.1,14330"* && "$out" == *"DBDATA_TEST_SQLSERVER_USER=sa"* ]]'
check "kit: ODBC and PostgreSQL" '[[ "$out" == *"DBDATA_TEST_ODBC=Driver={ODBC Driver 18"* && "$out" == *"DBDATA_TEST_POSTGRES=Host=127.0.0.1;Port=15432;"* ]]'
check "kit: SQL Server required" '[[ "$out" == *"DBDATA_REQUIRE_SQLSERVER=true"* ]]'
out="$(t postgres dbdatakit --dotenv)"
check "kit: only PostgreSQL when only it runs" '[[ "$out" == *"DBDATA_TEST_POSTGRES="* && "$out" != *SQLSERVER* && "$out" != *ODBC* ]]'

# DbDataBuild mapping
out="$(t "mssql postgres" dbdatabuild --dotenv)"
check "build: SQL Server and PostgreSQL connection strings" '[[ "$out" == *"DBDATABUILD_TEST_MSSQL=Server=127.0.0.1,14330;"* && "$out" == *"DBDATABUILD_TEST_PG=Host=127.0.0.1;Port=15432;"* ]]'
out="$(t postgres dbdatabuild --dotenv)"
check "build: only PostgreSQL when only it runs" '[[ "$out" == *"DBDATABUILD_TEST_PG="* && "$out" != *DBDATABUILD_TEST_MSSQL* ]]'

# MySQL, MariaDB and Oracle
out="$(t mysql --dotenv)"
check "mysql: a MySqlConnector string and its parts" '[[ "$out" == *"DBKIT_TEST_MYSQL=Server=127.0.0.1;Port=13306;User ID=root;Password=DbKit_Test_Pw1;"* && "$out" == *"DBKIT_TEST_MYSQL_PORT=13306"* && "$out" != *MARIADB* ]]'
out="$(t mariadb --dotenv)"
check "mariadb: its own port and names" '[[ "$out" == *"DBKIT_TEST_MARIADB=Server=127.0.0.1;Port=13307;"* && "$out" != *DBKIT_TEST_MYSQL* ]]'
out="$(t oracle --dotenv)"
check "oracle: an ODP.NET string for the pluggable database" '[[ "$out" == *"DBKIT_TEST_ORACLE=Data Source=127.0.0.1:15210/FREEPDB1;User Id=dbkit;Password=DbKit_Test_Pw1"* && "$out" == *"DBKIT_TEST_ORACLE_SERVICE=FREEPDB1"* ]]'

# The slow engines (DbDataBuild's full conformance run)
out="$(t "mssql postgres mssql2025 oracle spark bigquery flight flight-tls" dbdatabuild --dotenv)"
check "build slow: SQL Server 2025 on its own port, beside the 2022 one" '[[ "$out" == *"DBDATABUILD_TEST_MSSQL2025=Server=127.0.0.1,14332;User Id=sa;Password=DbKit_Test_Pw1;"* && "$out" == *"DBDATABUILD_TEST_MSSQL=Server=127.0.0.1,14330;"* ]]'
check "build slow: Oracle in the Host/Port/Service form the probe reads" '[[ "$out" == *"DBDATABUILD_TEST_ORACLE=Host=127.0.0.1;Port=15210;User Id=dbkit;Password=DbKit_Test_Pw1;Service=FREEPDB1"* ]]'
check "build slow: Spark and the BigQuery emulator" '[[ "$out" == *"DBDATABUILD_TEST_SPARK=Host=127.0.0.1;Port=10000"* && "$out" == *"DBDATABUILD_TEST_BIGQUERY=Host=127.0.0.1;Port=9050"* ]]'
check "build slow: the two Flight SQL servers, the TLS one by name and with its CA file" '[[ "$out" == *"DBDATABUILD_TEST_FLIGHT=grpc://127.0.0.1:31337;user=sqlflite_username;password=DbKit_Test_Pw1"* && "$out" == *"DBDATABUILD_TEST_FLIGHT_TLS=grpc+tls://localhost:31338;user=sqlflite_username;"* && "$out" == *"DBDATABUILD_TEST_FLIGHT_CA="*"flight-root-ca.pem"* ]]'
out="$(t "mssql postgres" dbdatabuild --dotenv)"
check "build slow: nothing of the slow engines is set while they are not running" '[[ "$out" != *MSSQL2025* && "$out" != *ORACLE* && "$out" != *SPARK* && "$out" != *BIGQUERY* && "$out" != *FLIGHT* ]]'
out="$(t flight --dotenv)"
check "flight: the plain server does not set the TLS variables" '[[ "$out" == *"DBKIT_TEST_FLIGHT=grpc://"* && "$out" != *FLIGHT_TLS* ]]'
check "up with no engine named leaves the slow ones out, and 'all' names every profile" '[[ "$(grep "^engines_default=" "$here/testenv.sh")" != *mssql2025* && "$(grep "^engines_all=" "$here/testenv.sh")" == *mssql2025*flight-tls* ]]'

# The second SQL Server and the logical-decoding PostgreSQL
out="$(t mssql-target --dotenv)"
check "mssql-target: its own port, and nothing of the first SQL Server" '[[ "$out" == *"DBKIT_TEST_MSSQL_TARGET=Server=127.0.0.1,14331;User Id=sa;Password=DbKit_Test_Pw1;"* && "$out" == *"DBKIT_TEST_MSSQL_TARGET_SERVER=127.0.0.1,14331"* && "$out" != *"DBKIT_TEST_MSSQL="* ]]'
out="$(t mssql --dotenv)"
check "mssql: the target's variables stay out when only the first runs" '[[ "$out" != *MSSQL_TARGET* ]]'
out="$(t postgres-logical --dotenv)"
check "postgres-logical: port 15433, user dbdatasync, a JDBC URL, and nothing of the plain PostgreSQL" '[[ "$out" == *"DBKIT_TEST_POSTGRES_LOGICAL=Host=127.0.0.1;Port=15433;Username=dbdatasync;Password=DbKit_Test_Pw1;Database=postgres"* && "$out" == *"DBKIT_TEST_POSTGRES_LOGICAL_JDBC_URL=jdbc:postgresql://127.0.0.1:15433/"* && "$out" != *"DBKIT_TEST_POSTGRES="* && "$out" != *"DBKIT_TEST_POSTGRES_PORT"* ]]'
out="$(t postgres --dotenv)"
check "postgres: the logical server's variables stay out when only the plain one runs" '[[ "$out" != *LOGICAL* ]]'
out="$(t "mssql postgres postgres-logical" dbdatakit --dotenv)"
check "kit: the plain PostgreSQL is still the shared one when the logical one runs too" '[[ "$out" == *"DBDATA_TEST_POSTGRES=Host=127.0.0.1;Port=15432;"* ]]'

# DbDataSync mapping
out="$(t "mssql mssql-target postgres-logical mysql mariadb oracle" dbdatasync --dotenv)"
check "sync: SQL Server source, target and the shared server variable" '[[ "$out" == *"DBDATASYNC_TEST_MSSQL_SERVER=Server=127.0.0.1,14330;"* && "$out" == *"DBDATASYNC_TEST_MSSQL_SOURCE_SERVER=Server=127.0.0.1,14330;"* && "$out" == *"DBDATASYNC_TEST_MSSQL_TARGET_SERVER=Server=127.0.0.1,14331;"* ]]'
check "sync: PostgreSQL is the logical one, with its JDBC URL" '[[ "$out" == *"DBDATASYNC_TEST_POSTGRES_SERVER=Host=127.0.0.1;Port=15433;Username=dbdatasync;"* && "$out" == *"DBDATASYNC_TEST_POSTGRES_JDBC_URL=jdbc:postgresql://127.0.0.1:15433/"* ]]'
check "sync: MySQL, MariaDB and Oracle" '[[ "$out" == *"DBDATASYNC_TEST_MYSQL_SERVER=Server=127.0.0.1;Port=13306;"* && "$out" == *"DBDATASYNC_TEST_MARIADB_SERVER=Server=127.0.0.1;Port=13307;"* && "$out" == *"DBDATASYNC_TEST_ORACLE_SERVER=Data Source=127.0.0.1:15210/FREEPDB1;User Id=dbkit;"* ]]'
out="$(t postgres dbdatasync --dotenv)"
check "sync: the plain PostgreSQL is not the one Sync uses (no logical decoding there)" '[[ "$out" != *DBDATASYNC_TEST_POSTGRES* ]]'
out="$(t mssql dbdatasync --dotenv)"
check "sync: only what runs is set" '[[ "$out" == *DBDATASYNC_TEST_MSSQL_SERVER=* && "$out" != *TARGET_SERVER* && "$out" != *ORACLE* && "$out" != *POSTGRES* ]]'

# The compose file itself (needs the docker CLI, not a daemon; skipped without it)
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  cfg="$(docker compose -f "$here/docker-compose.yml" --profile '*' config 2>&1 || true)"
  check "compose: every engine has its own profile and a container name" '[[ "$cfg" == *"container_name: dbkit-test-mssql-target"* && "$cfg" == *"container_name: dbkit-test-postgres-logical"* ]]'
  check "compose: no two engines publish the same host port" '[ "$(grep -oE "published: \"[0-9]+\"" <<<"$cfg" | sort | uniq -d | wc -l)" -eq 0 ]'
  check "compose: the logical server is wal_level=logical with wal2json" '[[ "$cfg" == *"wal_level=logical"* && "$cfg" == *"wal2json"* ]]'
  check "compose: SQL Server Agent on for the first SQL Server only" '[ "$(grep -c "MSSQL_AGENT_ENABLED" <<<"$cfg")" -eq 1 ]'
  check "compose: Oracle runs the init scripts" '[[ "$cfg" == *"/container-entrypoint-initdb.d"* ]]'
  check "compose: the slow engines have their own ports (14332, 10000, 9050, 31337, 31338)" '[[ "$cfg" == *"published: \"14332\""* && "$cfg" == *"published: \"10000\""* && "$cfg" == *"published: \"9050\""* && "$cfg" == *"published: \"31337\""* && "$cfg" == *"published: \"31338\""* ]]'
  check "compose: only the TLS Flight server has TLS on, and both share one image" '[ "$(grep -c "TLS_ENABLED: \"1\"" <<<"$cfg")" -eq 1 ] && [ "$(grep -E "^\s+image: voltrondata|^\s+image: .*sqlflite" <<<"$cfg" | sort -u | wc -l)" -eq 1 ]'
  check "each profile named in testenv.sh exists in the compose file" '[ "$(docker compose -f "$here/docker-compose.yml" --profile "*" config --services | sort | tr "\n" " ")" = "$(grep "^engines_all=" "$here/testenv.sh" | sed "s/.*(\(.*\))/\1/" | tr " " "\n" | sort | tr "\n" " ")" ]'
fi

# Output formats
check "shell format quotes values" '[[ "$(t postgres)" == *"export DBKIT_TEST_POSTGRES_USER='"'"'postgres'"'"'"* ]]'
check "eval makes the variables real" '(eval "$(t mssql dbdataproxy)"; [ "$DBPROXY_TEST_SQLSERVER_USER" = sa ] && [ "$DBPROXY_REQUIRE_SQLSERVER" = true ])'
check "a password with a quote survives eval" '(export DBKIT_TEST_PASSWORD="a'"'"'b"; eval "$(t mssql)"; [ "$DBKIT_TEST_MSSQL_PASSWORD" = "a'"'"'b" ])'
check "an unknown product is an error" '! t mssql nosuchproduct >/dev/null 2>&1'
check "an unknown engine is refused by up, before Docker is asked" '[[ "$("$here/testenv.sh" up nosuchengine 2>&1 || true)" == *"unknown engine"* ]]'

[ "$fail" -eq 0 ] && echo "All checks passed." || { echo "$fail check(s) failed."; exit 1; }
