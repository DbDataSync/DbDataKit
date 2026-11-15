#!/bin/bash
# SessionStart hook for Claude Code cloud sessions: makes the local test engines usable.
# SQL Server (docker, port 1433) is started by the environment; this adds PostgreSQL 16 + pgvector (port 5432)
# and exports DBDATAKIT_TEST_SQLSERVER / DBDATAKIT_TEST_POSTGRES. Passwords stay in root-only files and are
# read when the shell sources the env file, never written into it.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

cd "${CLAUDE_PROJECT_DIR:-.}"

MSSQL_PW_FILE=/root/.mssql_sa_password
PG_PW_FILE=/root/.postgres_password

# --- PostgreSQL: package (only if missing), pgvector (only if missing) ---
if ! command -v pg_ctlcluster >/dev/null 2>&1; then
  apt-get update -qq
  apt-get install -y -qq postgresql
fi

PGVER="$(pg_lsclusters --no-header | awk '$2=="main" { print $1; exit }')"
[ -n "$PGVER" ] || { echo "session-start: no PostgreSQL 'main' cluster found" >&2; exit 1; }

if ! dpkg -s "postgresql-$PGVER-pgvector" >/dev/null 2>&1; then
  apt-get update -qq
  apt-get install -y -qq "postgresql-$PGVER-pgvector"
fi

# --- start the cluster if it is down ---
if ! pg_lsclusters --no-header | awk '$2=="main" { print $4 }' | grep -qx online; then
  pg_ctlcluster "$PGVER" main start
fi
for _ in $(seq 1 20); do
  su postgres -c "psql -Atqc 'select 1'" >/dev/null 2>&1 && break
  sleep 1
done

# --- postgres role password: keep the file if there is one, else make one and set it ---
if [ ! -s "$PG_PW_FILE" ]; then
  ( umask 077; openssl rand -hex 16 > "$PG_PW_FILE" )
fi
su postgres -c "psql -qAt" <<SQL >/dev/null
ALTER ROLE postgres PASSWORD '$(cat "$PG_PW_FILE")';
SQL

# --- environment for the test suites (evaluated when the shell sources it) ---
if [ -n "${CLAUDE_ENV_FILE:-}" ]; then
  {
    echo 'export DBDATAKIT_TEST_POSTGRES="Host=localhost;Port=5432;Username=postgres;Password=$(cat /root/.postgres_password);Database=postgres"'
    if [ -s "$MSSQL_PW_FILE" ]; then
      echo 'export DBDATAKIT_TEST_SQLSERVER="Server=localhost,1433;User Id=sa;Password=$(cat /root/.mssql_sa_password);TrustServerCertificate=True;Encrypt=False"'
    fi
  } >> "$CLAUDE_ENV_FILE"
fi
