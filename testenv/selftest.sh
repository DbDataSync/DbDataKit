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
check "proxy: nothing mapped without SQL Server" '[[ "$out" != *DBPROXY_* ]]'

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

# Output formats
check "shell format quotes values" '[[ "$(t postgres)" == *"export DBKIT_TEST_POSTGRES_USER='"'"'postgres'"'"'"* ]]'
check "eval makes the variables real" '(eval "$(t mssql dbdataproxy)"; [ "$DBPROXY_TEST_SQLSERVER_USER" = sa ] && [ "$DBPROXY_REQUIRE_SQLSERVER" = true ])'
check "a password with a quote survives eval" '(export DBKIT_TEST_PASSWORD="a'"'"'b"; eval "$(t mssql)"; [ "$DBKIT_TEST_MSSQL_PASSWORD" = "a'"'"'b" ])'
check "an unknown product is an error" '! t mssql nosuchproduct >/dev/null 2>&1'
check "an unknown engine is refused by up, before Docker is asked" '[[ "$("$here/testenv.sh" up nosuchengine 2>&1 || true)" == *"unknown engine"* ]]'

[ "$fail" -eq 0 ] && echo "All checks passed." || { echo "$fail check(s) failed."; exit 1; }
