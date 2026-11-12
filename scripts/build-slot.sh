#!/usr/bin/env bash
# Runs a command in one of N build slots (default 2) so several agents can build at once on a machine with
# enough disk and cores. Usage: scripts/build-slot.sh <command> [args...]
# Slot 1 is /tmp/build.lock, the file the older `flock -o /tmp/build.lock <command>` convention already uses,
# so both ways of building count against the same limit. DBDATAKIT_BUILD_SLOTS=1 gives the old behaviour.
set -u
# JDBC/IKVM off by default: the IKVM packages are about 3.7 GB. A build that really needs the JDBC projects sets
# DBDATAKIT_TEST_SKIP_JDBC=false (docs/testing-environment.md, "Fast verifier runs").
export DBDATAKIT_TEST_SKIP_JDBC="${DBDATAKIT_TEST_SKIP_JDBC:-true}"
slots="${DBDATAKIT_BUILD_SLOTS:-2}"
while true; do
  for i in $(seq 1 "$slots"); do
    f=/tmp/build.lock
    [ "$i" -gt 1 ] && f="/tmp/build.lock.$i"
    # -n: do not wait; -E 200: exit code 200 means the slot was busy; -o: the lock does not leak into child processes.
    flock -n -E 200 -o "$f" "$@"
    rc=$?
    if [ "$rc" -ne 200 ]; then exit "$rc"; fi
  done
  sleep 2
done
