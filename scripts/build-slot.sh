#!/usr/bin/env bash
# Runs a command in a build slot so several agents can build at once on one machine without filling the disk or the cores.
# Usage: scripts/build-slot.sh <command> [args...]
# Two pools of slots, chosen by the command (override with DBDATAKIT_BUILD_POOL=dotnet|node):
#   dotnet  (dotnet, msbuild, built test executables, anything else): DBDATAKIT_BUILD_SLOTS slots (default 2). Slot 1 is /tmp/build.lock,
#           the file the older `flock -o /tmp/build.lock <command>` convention uses, so both ways count against the same limit.
#   node    (npm, npx, node, tsc, vitest, playwright, oxlint): DBDATAKIT_NODE_SLOTS slots (default 2), locks /tmp/build-node.lock[.N].
# A build is refused (exit 75) when the disk has less than DBDATAKIT_BUILD_MIN_FREE_GB free (default 3).
set -u
# JDBC/IKVM off by default: the IKVM packages are about 3.7 GB. A build that really needs the JDBC projects sets
# DBDATAKIT_TEST_SKIP_JDBC=false (docs/testing-environment.md, "Fast verifier runs").
export DBDATAKIT_TEST_SKIP_JDBC="${DBDATAKIT_TEST_SKIP_JDBC:-true}"

# Hard links instead of copies for NuGet assemblies and copied content in bin (dev machines only: CI does not use this script). They need the
# NuGet cache and the repository on one volume (otherwise MSBuild copies, silently). Set DBDATAKIT_HARDLINKS=false to turn them off. A tool that
# rewrites a file in bin in place would change the NuGet cache too: tests and tools must write new files, not edit those.
if [ "${DBDATAKIT_HARDLINKS:-true}" != "false" ]; then
  export CreateHardLinksForCopyLocalIfPossible=true CreateHardLinksForCopyFilesToOutputDirectoryIfPossible=true \
         CreateHardLinksForAdditionalFilesIfPossible=true CreateHardLinksForPublishFilesIfPossible=true
fi

pool="${DBDATAKIT_BUILD_POOL:-}"
if [ -z "$pool" ]; then
  case "$(basename -- "${1:-}")" in
    npm|npx|node|tsc|vitest|playwright|oxlint|pnpm|yarn) pool=node ;;
    *) pool=dotnet ;;
  esac
fi
if [ "$pool" = "node" ]; then
  slots="${DBDATAKIT_NODE_SLOTS:-2}"; base=/tmp/build-node.lock
else
  slots="${DBDATAKIT_BUILD_SLOTS:-2}"; base=/tmp/build.lock
fi

min_free="${DBDATAKIT_BUILD_MIN_FREE_GB:-3}"
free_gb=$(df -P --block-size=1G / 2>/dev/null | awk 'NR==2 {print $4}')
if [ -n "$free_gb" ] && [ "$free_gb" -lt "$min_free" ]; then
  echo "build-slot: refused: only ${free_gb} GB free on / (minimum ${min_free} GB). Delete bin, obj, node_modules and .duckdb of finished steps, then try again." >&2
  exit 75
fi

# Dev machines only (this script is not used by CI): IKVM packages in the NuGet cache mean a build restored them although JDBC is off.
warn_ikvm() {
  if [ "$DBDATAKIT_TEST_SKIP_JDBC" = "true" ] && ls -d "${NUGET_PACKAGES:-$HOME/.nuget/packages}"/ikvm* >/dev/null 2>&1; then
    echo "build-slot: WARNING: IKVM packages (about 3.7 GB) are in the NuGet cache although JDBC is off. Delete them: rm -rf \"${NUGET_PACKAGES:-$HOME/.nuget/packages}\"/ikvm* (and find which build ran without the flag)." >&2
  fi
}
while true; do
  for i in $(seq 1 "$slots"); do
    f="$base"
    [ "$i" -gt 1 ] && f="$base.$i"
    # -n: do not wait; -E 200: exit code 200 means the slot was busy; -o: the lock does not leak into child processes.
    flock -n -E 200 -o "$f" "$@"
    rc=$?
    if [ "$rc" -ne 200 ]; then warn_ikvm; exit "$rc"; fi
  done
  sleep 2
done
