#!/usr/bin/env bash
# Prints the exports the ODBC origin's conformance tests need for PostgreSQL's ODBC driver (psqlodbc): the driver unpacked by
# scripts/fetch-psqlodbc.sh, or one installed on the system (odbcinst lists `PostgreSQL Unicode`). Prints nothing when there is neither,
# and those tests then skip and say why.
#   eval "$(scripts/psqlodbc-env.sh)"
# The engines themselves come from testenv/: eval "$(../testenv/testenv.sh env dbdatabuild)".
set -euo pipefail
dir="${DDB_PSQLODBC_DIR:-$HOME/.cache/dbdatabuild/psqlodbc}"
driver="$(find "$dir" -name psqlodbcw.so 2>/dev/null | head -1 || true)"
if [ -n "$driver" ]; then
  echo "export DBDATABUILD_TEST_ODBC_PG_DRIVER='$driver'"
  echo "export LD_LIBRARY_PATH='$(dirname "$(dirname "$driver")")${LD_LIBRARY_PATH:+:$LD_LIBRARY_PATH}'"
elif command -v odbcinst >/dev/null 2>&1 && odbcinst -q -d 2>/dev/null | grep -qx '\[PostgreSQL Unicode\]'; then
  echo "export DBDATABUILD_TEST_ODBC_PG_DRIVER='PostgreSQL Unicode'"
fi
