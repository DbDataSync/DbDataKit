#!/usr/bin/env bash
# Checks the variable generation and the product mappings without Docker (TESTENV_ASSUME_RUNNING). Fast; CI runs it.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
fail=0
check() { if eval "$2"; then echo "ok   $1"; else echo "FAIL $1"; fail=$((fail+1)); fi; }
t() { TESTENV_ASSUME_RUNNING="$1" "$here/testenv.sh" env "${@:2}"; }

check "nothing running prints nothing" '[ -z "$(t "" )" ]'
check "mssql gives the canonical SQL Server variables" '[[ "$(t mssql)" == *"DBDATAKIT_TEST_MSSQL_SERVER"* && "$(t mssql)" != *POSTGRES* ]]'
check "postgres gives the canonical PostgreSQL variables" '[[ "$(t postgres)" == *"DBDATAKIT_TEST_POSTGRES="* && "$(t postgres)" != *MSSQL* ]]'
check "the ADO string is complete" '[[ "$(t mssql --dotenv)" == *"DBDATAKIT_TEST_MSSQL=Server=127.0.0.1,14330;User Id=sa;Password=DbKit_Test_Pw1;TrustServerCertificate=True;Encrypt=False"* ]]'
check "the ODBC string names the driver" '[[ "$(t mssql --dotenv)" == *"DBDATAKIT_TEST_MSSQL_ODBC=Driver={ODBC Driver 18 for SQL Server};Server=127.0.0.1,14330;"* ]]'

# Gateway mapping
out="$(t mssql dbdatagateway --dotenv)"
check "gateway: server, user, password" '[[ "$out" == *"DBDATAKIT_TEST_MSSQL_SERVER=127.0.0.1,14330"* && "$out" == *"DBDATAKIT_TEST_MSSQL_USER=sa"* && "$out" == *"DBDATAKIT_TEST_MSSQL_PASSWORD=DbKit_Test_Pw1"* ]]'
check "gateway: SQL Server is required when it was started" '[[ "$out" == *"DBDATAKIT_TEST_REQUIRE_SQLSERVER=true"* ]]'
check "gateway: the State suites' name for the SQL Server connection is mapped (they skipped silently without it)" '[[ "$out" == *"DBDATAKIT_TEST_SQLSERVER=Server=127.0.0.1,14330;"* ]]'
out="$(t postgres dbdatagateway --dotenv)"
check "gateway: nothing about SQL Server mapped without it" '[[ "$out" != *DBDATAKIT_TEST_MSSQL_SERVER* && "$out" != *DBDATAKIT_TEST_REQUIRE_SQLSERVER* ]]'
check "gateway: the PostgreSQL string, and it is required when it was started" '[[ "$out" == *"DBDATAKIT_TEST_POSTGRES=Host=127.0.0.1;Port=15432;"* && "$out" == *"DBDATAKIT_TEST_REQUIRE_POSTGRES=true"* ]]'

# Shared libraries mapping
out="$(t "mssql postgres" dbdatakit --dotenv)"
check "kit: a full connection string and separate parts" '[[ "$out" == *"DBDATAKIT_TEST_SQLSERVER=Server=127.0.0.1,14330;"* && "$out" == *"DBDATAKIT_TEST_MSSQL_SERVER=127.0.0.1,14330"* && "$out" == *"DBDATAKIT_TEST_MSSQL_USER=sa"* ]]'
check "kit: ODBC and PostgreSQL" '[[ "$out" == *"DBDATAKIT_TEST_MSSQL_ODBC=Driver={ODBC Driver 18"* && "$out" == *"DBDATAKIT_TEST_POSTGRES=Host=127.0.0.1;Port=15432;"* ]]'
check "kit: SQL Server required" '[[ "$out" == *"DBDATAKIT_TEST_REQUIRE_SQLSERVER=true"* ]]'
out="$(t postgres dbdatakit --dotenv)"
check "kit: only PostgreSQL when only it runs" '[[ "$out" == *"DBDATAKIT_TEST_POSTGRES="* && "$out" != *SQLSERVER* && "$out" != *ODBC* ]]'

# DbDataBuild mapping
out="$(t "mssql postgres" dbdatabuild --dotenv)"
check "build: SQL Server and PostgreSQL connection strings" '[[ "$out" == *"DBDATAKIT_TEST_MSSQL=Server=127.0.0.1,14330;"* && "$out" == *"DBDATAKIT_TEST_POSTGRES=Host=127.0.0.1;Port=15432;"* ]]'
out="$(t postgres dbdatabuild --dotenv)"
check "build: only PostgreSQL when only it runs" '[[ "$out" == *"DBDATAKIT_TEST_POSTGRES="* && "$out" != *DBDATAKIT_TEST_MSSQL* ]]'

# MySQL, MariaDB and Oracle
out="$(t mysql --dotenv)"
check "mysql: a MySqlConnector string and its parts" '[[ "$out" == *"DBDATAKIT_TEST_MYSQL=Server=127.0.0.1;Port=13306;User ID=root;Password=DbKit_Test_Pw1;"* && "$out" == *"DBDATAKIT_TEST_MYSQL_PORT=13306"* && "$out" != *MARIADB* ]]'
out="$(t mariadb --dotenv)"
check "mariadb: its own port and names" '[[ "$out" == *"DBDATAKIT_TEST_MARIADB=Server=127.0.0.1;Port=13307;"* && "$out" != *DBDATAKIT_TEST_MYSQL* ]]'
out="$(t oracle --dotenv)"
check "oracle: an ODP.NET string for the pluggable database" '[[ "$out" == *"DBDATAKIT_TEST_ORACLE=Data Source=127.0.0.1:15210/FREEPDB1;User Id=dbkit;Password=DbKit_Test_Pw1"* && "$out" == *"DBDATAKIT_TEST_ORACLE_SERVICE=FREEPDB1"* ]]'

# The slow engines (DbDataBuild's full conformance run)
out="$(t "mssql postgres mssql2025 oracle spark bigquery flight flight-tls" dbdatabuild --dotenv)"
check "build slow: SQL Server 2025 on its own port, beside the 2022 one" '[[ "$out" == *"DBDATAKIT_TEST_MSSQL2025=Server=127.0.0.1,14332;User Id=sa;Password=DbKit_Test_Pw1;"* && "$out" == *"DBDATAKIT_TEST_MSSQL=Server=127.0.0.1,14330;"* ]]'
check "build slow: Oracle in the Host/Port/Service form the probe reads" '[[ "$out" == *"DBDATAKIT_TEST_ORACLE_KV=Host=127.0.0.1;Port=15210;User Id=dbkit;Password=DbKit_Test_Pw1;Service=FREEPDB1"* ]]'
check "build slow: Spark and the BigQuery emulator" '[[ "$out" == *"DBDATAKIT_TEST_SPARK=Host=127.0.0.1;Port=10000"* && "$out" == *"DBDATAKIT_TEST_BIGQUERY=Host=127.0.0.1;Port=9050"* ]]'
check "build slow: the two Flight SQL servers, the TLS one by name and with its CA file" '[[ "$out" == *"DBDATAKIT_TEST_FLIGHT=grpc://127.0.0.1:31337;user=sqlflite_username;password=DbKit_Test_Pw1"* && "$out" == *"DBDATAKIT_TEST_FLIGHT_TLS=grpc+tls://localhost:31338;user=sqlflite_username;"* && "$out" == *"DBDATAKIT_TEST_FLIGHT_TLS_CA="*"flight-root-ca.pem"* ]]'
out="$(t "mssql postgres" dbdatabuild --dotenv)"
check "build slow: nothing of the slow engines is set while they are not running" '[[ "$out" != *MSSQL2025* && "$out" != *ORACLE* && "$out" != *SPARK* && "$out" != *BIGQUERY* && "$out" != *FLIGHT* ]]'
out="$(t flight --dotenv)"
check "flight: the plain server does not set the TLS variables" '[[ "$out" == *"DBDATAKIT_TEST_FLIGHT=grpc://"* && "$out" != *FLIGHT_TLS* ]]'
check "up with no engine named leaves the slow ones out, and 'all' names every profile" '[[ "$(grep "^engines_default=" "$here/testenv.sh")" != *mssql2025* && "$(grep "^engines_all=" "$here/testenv.sh")" == *mssql2025*flight-tls* ]]'

# The file servers (plan 27a IX04)
out="$(t sftp --dotenv)"
check "sftp: its port, user, folder and the key file of the cache" '[[ "$out" == *"DBDATAKIT_TEST_SFTP_PORT=13022"* && "$out" == *"DBDATAKIT_TEST_SFTP_USER=foo"* && "$out" == *"DBDATAKIT_TEST_SFTP_ROOT=/upload"* && "$out" == *"DBDATAKIT_TEST_SFTP_KEY_FILE="*"/sftp/client_ed25519"* && "$out" != *FTPS* ]]'
out="$(t ssh --dotenv)"
check "ssh: its port, user and folder, and nothing of sftp" '[[ "$out" == *"DBDATAKIT_TEST_SSH_PORT=13023"* && "$out" == *"DBDATAKIT_TEST_SSH_ROOT=/config"* && "$out" != *SFTP* ]]'
out="$(t "s3 azurite" --dotenv)"
check "s3 and azurite: endpoints, the bucket and container, the emulator's account" '[[ "$out" == *"DBDATAKIT_TEST_S3_ENDPOINT=http://127.0.0.1:13090"* && "$out" == *"DBDATAKIT_TEST_S3_BUCKET=dbkit"* && "$out" == *"DBDATAKIT_TEST_AZURITE_ENDPOINT=http://127.0.0.1:13100/devstoreaccount1"* && "$out" == *"DBDATAKIT_TEST_AZURITE_ACCOUNT=devstoreaccount1"* ]]'
out="$(t ftps --dotenv)"
check "ftps: its port and user, and nothing of sftp" '[[ "$out" == *"DBDATAKIT_TEST_FTPS_PORT=13021"* && "$out" == *"DBDATAKIT_TEST_FTPS_USER=foo"* && "$out" != *SFTP* ]]'

# The second SQL Server and the logical-decoding PostgreSQL
out="$(t mssql-target --dotenv)"
check "mssql-target: its own port, and nothing of the first SQL Server" '[[ "$out" == *"DBDATAKIT_TEST_MSSQL_TARGET=Server=127.0.0.1,14331;User Id=sa;Password=DbKit_Test_Pw1;"* && "$out" == *"DBDATAKIT_TEST_MSSQL_TARGET_SERVER=127.0.0.1,14331"* && "$out" != *"DBDATAKIT_TEST_MSSQL="* ]]'
out="$(t mssql --dotenv)"
check "mssql: the target's variables stay out when only the first runs" '[[ "$out" != *MSSQL_TARGET* ]]'
out="$(t postgres-logical --dotenv)"
check "postgres-logical: port 15433, user dbdatasync, a JDBC URL, and nothing of the plain PostgreSQL" '[[ "$out" == *"DBDATAKIT_TEST_POSTGRES_LOGICAL=Host=127.0.0.1;Port=15433;Username=dbdatasync;Password=DbKit_Test_Pw1;Database=postgres"* && "$out" == *"DBDATAKIT_TEST_POSTGRES_LOGICAL_JDBC_URL=jdbc:postgresql://127.0.0.1:15433/"* && "$out" != *"DBDATAKIT_TEST_POSTGRES="* && "$out" != *"DBDATAKIT_TEST_POSTGRES_PORT"* ]]'
out="$(t postgres --dotenv)"
check "postgres: the logical server's variables stay out when only the plain one runs" '[[ "$out" != *LOGICAL* ]]'
out="$(t "mssql postgres postgres-logical" dbdatakit --dotenv)"
check "kit: the plain PostgreSQL is still the shared one when the logical one runs too" '[[ "$out" == *"DBDATAKIT_TEST_POSTGRES=Host=127.0.0.1;Port=15432;"* ]]'

# The previous releases of the state engines (N-1, plan 29b U01)
out="$(t mssql2022 --dotenv)"
check "mssql2022: its own port and names, and nothing of the current SQL Server" '[[ "$out" == *"DBDATAKIT_TEST_MSSQL2022=Server=127.0.0.1,14333;User Id=sa;Password=DbKit_Test_Pw1;TrustServerCertificate=True;Encrypt=False"* && "$out" == *"DBDATAKIT_TEST_MSSQL2022_SERVER=127.0.0.1,14333"* && "$out" != *"DBDATAKIT_TEST_MSSQL="* && "$out" != *"DBDATAKIT_TEST_MSSQL_SERVER"* ]]'
out="$(t postgres16 --dotenv)"
check "postgres16: port 15434, user postgres, and nothing of the current PostgreSQL" '[[ "$out" == *"DBDATAKIT_TEST_POSTGRES16=Host=127.0.0.1;Port=15434;Username=postgres;Password=DbKit_Test_Pw1;Database=postgres"* && "$out" == *"DBDATAKIT_TEST_POSTGRES16_PORT=15434"* && "$out" != *"DBDATAKIT_TEST_POSTGRES="* && "$out" != *"DBDATAKIT_TEST_POSTGRES_PORT"* ]]'
out="$(t "mssql postgres" --dotenv)"
check "the current engines do not set the older engines' variables" '[[ "$out" != *MSSQL2022* && "$out" != *POSTGRES16* ]]'
out="$(t "mssql2022 postgres16" dbdatakit-older --dotenv)"
check "older: the State suites' variables name SQL Server 2022 and PostgreSQL 16, with explicit variables for the version rows" '[[ "$out" == *"DBDATAKIT_TEST_SQLSERVER=Server=127.0.0.1,14333;"* && "$out" == *"DBDATAKIT_TEST_SQLSERVER2022=Server=127.0.0.1,14333;"* && "$out" == *"DBDATAKIT_TEST_POSTGRES=Host=127.0.0.1;Port=15434;"* && "$out" == *"DBDATAKIT_TEST_POSTGRES16=Host=127.0.0.1;Port=15434;"* ]]'
check "older: every engine started is required, the version rows too" '[[ "$out" == *"DBDATAKIT_TEST_REQUIRE_SQLSERVER=true"* && "$out" == *"DBDATAKIT_TEST_REQUIRE_SQLSERVER2022=true"* && "$out" == *"DBDATAKIT_TEST_REQUIRE_POSTGRES=true"* && "$out" == *"DBDATAKIT_TEST_REQUIRE_POSTGRES16=true"* ]]'
out="$(t "mssql postgres" dbdatakit-older --dotenv)"
check "older: nothing is mapped while the older engines are not running" '[[ "$out" == "$(t "mssql postgres" --dotenv)" && "$out" != *DBDATAKIT_TEST_SQLSERVER=* && "$out" != *DBDATAKIT_TEST_REQUIRE* ]]'
check "up with no engine named leaves the older engines out, and 'all' names them" '[[ "$(grep "^engines_default=" "$here/testenv.sh")" != *mssql2022* && "$(grep "^engines_default=" "$here/testenv.sh")" != *postgres16* && "$(grep "^engines_all=" "$here/testenv.sh")" == *mssql2022*postgres16* ]]'

# DbDataSync mapping
out="$(t "mssql mssql-target postgres-logical mysql mariadb oracle" dbdatasync --dotenv)"
check "sync: the SQL Server source and target connection strings" '[[ "$out" == *"DBDATAKIT_TEST_MSSQL=Server=127.0.0.1,14330;"* && "$out" == *"DBDATAKIT_TEST_MSSQL_TARGET=Server=127.0.0.1,14331;"* ]]'
check "sync: PostgreSQL is the logical one, with its JDBC URL" '[[ "$out" == *"DBDATAKIT_TEST_POSTGRES_LOGICAL=Host=127.0.0.1;Port=15433;Username=dbdatasync;"* && "$out" == *"DBDATAKIT_TEST_POSTGRES_LOGICAL_JDBC_URL=jdbc:postgresql://127.0.0.1:15433/"* ]]'
check "sync: MySQL, MariaDB and Oracle" '[[ "$out" == *"DBDATAKIT_TEST_MYSQL=Server=127.0.0.1;Port=13306;"* && "$out" == *"DBDATAKIT_TEST_MARIADB=Server=127.0.0.1;Port=13307;"* && "$out" == *"DBDATAKIT_TEST_ORACLE=Data Source=127.0.0.1:15210/FREEPDB1;User Id=dbkit;"* ]]'
out="$(t postgres dbdatasync --dotenv)"
check "sync: the plain PostgreSQL is not the one Sync uses (no logical decoding there)" '[[ "$out" != *POSTGRES_LOGICAL* ]]'
out="$(t mssql dbdatasync --dotenv)"
check "sync: only what runs is set" '[[ "$out" == *DBDATAKIT_TEST_MSSQL=* && "$out" != *TARGET_SERVER* && "$out" != *ORACLE* && "$out" != *POSTGRES* ]]'

# The compose file itself (needs the docker CLI, not a daemon; skipped without it)
if command -v docker >/dev/null 2>&1 && docker compose version >/dev/null 2>&1; then
  cfg="$(docker compose -f "$here/docker-compose.yml" --profile '*' config 2>&1 || true)"
  check "compose: every engine has its own profile and a container name" '[[ "$cfg" == *"container_name: dbkit-test-mssql-target"* && "$cfg" == *"container_name: dbkit-test-postgres-logical"* ]]'
  check "compose: no two engines publish the same host port" '[ "$(grep -oE "published: \"[0-9]+\"" <<<"$cfg" | sort | uniq -d | wc -l)" -eq 0 ]'
  check "compose: the logical server is wal_level=logical with wal2json" '[[ "$cfg" == *"wal_level=logical"* && "$cfg" == *"wal2json"* ]]'
  check "compose: SQL Server Agent on for the first SQL Server only" '[ "$(grep -c "MSSQL_AGENT_ENABLED" <<<"$cfg")" -eq 1 ]'
  check "compose: Oracle runs the init scripts" '[[ "$cfg" == *"/container-entrypoint-initdb.d"* ]]'
  check "compose: the older engines have their own ports (14333, 15434) and containers, PostgreSQL 16 from the pinned pgvector image" '[[ "$cfg" == *"published: \"14333\""* && "$cfg" == *"published: \"15434\""* && "$cfg" == *"container_name: dbkit-test-mssql2022"* && "$cfg" == *"container_name: dbkit-test-postgres16"* && "$cfg" == *"pgvector/pgvector:pg16"* && "$cfg" == *"mssql/server:2022-latest@sha256:"* ]]'
  check "compose: the slow engines have their own ports (14332, 10000, 9050, 31337, 31338)" '[[ "$cfg" == *"published: \"14332\""* && "$cfg" == *"published: \"10000\""* && "$cfg" == *"published: \"9050\""* && "$cfg" == *"published: \"31337\""* && "$cfg" == *"published: \"31338\""* ]]'
  check "compose: only the TLS Flight server has TLS on, and both share one image" '[ "$(grep -c "TLS_ENABLED: \"1\"" <<<"$cfg")" -eq 1 ] && [ "$(grep -E "^\s+image: voltrondata|^\s+image: .*sqlflite" <<<"$cfg" | sort -u | wc -l)" -eq 1 ]'
  check "each profile named in testenv.sh exists in the compose file" '[ "$(docker compose -f "$here/docker-compose.yml" --profile "*" config --services | sort | tr "\n" " ")" = "$(grep "^engines_all=" "$here/testenv.sh" | sed "s/.*(\(.*\))/\1/" | tr " " "\n" | sort | tr "\n" " ")" ]'
fi

# Output formats
check "shell format quotes values" '[[ "$(t postgres)" == *"export DBDATAKIT_TEST_POSTGRES_USER='"'"'postgres'"'"'"* ]]'
check "eval makes the variables real" '(eval "$(t mssql dbdatagateway)"; [ "$DBDATAKIT_TEST_MSSQL_USER" = sa ] && [ "$DBDATAKIT_TEST_REQUIRE_SQLSERVER" = true ])'
check "a password with a quote survives eval" '(export DBDATAKIT_TEST_PASSWORD="a'"'"'b"; eval "$(t mssql)"; [ "$DBDATAKIT_TEST_MSSQL_PASSWORD" = "a'"'"'b" ])'
check "an unknown product is an error" '! t mssql nosuchproduct >/dev/null 2>&1'
check "an unknown engine is refused by up, before Docker is asked" '[[ "$("$here/testenv.sh" up nosuchengine 2>&1 || true)" == *"unknown engine"* ]]'

[ "$fail" -eq 0 ] && echo "All checks passed." || { echo "$fail check(s) failed."; exit 1; }
