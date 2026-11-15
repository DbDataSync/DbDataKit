#!/usr/bin/env bash
# The manifest (package/1) of the Store runtime package (plan 33 SC24): text on stdout.
#   scripts/runtime-manifest.sh <rid> <version>
# The runtime runs the tasks of a model's definition package (the task `deploy`, DbDataKit.DataModel.Runner): `dbdatastore --runner`, framework-dependent, started through the dotnet host of the runner.
# <version> is three numbers (a suffix is cut off; a four-number release folds its last two, as Sync's runtime-manifest.sh does). One package per platform (dbdatastore-runtime-<rid>).
set -euo pipefail
RID="${1:?usage: runtime-manifest.sh <rid> <version>}"
VERSION="${2:?usage: runtime-manifest.sh <rid> <version>}"
VERSION="${VERSION%%[-+]*}"
case "$VERSION" in
  [0-9]*.[0-9]*.[0-9]*.[0-9]*)
    IFS=. read -r A B C D <<<"$VERSION"
    VERSION="$A.$B.$((10#$C * 10000 + 10#$D))" ;;
  [0-9]*.[0-9]*.[0-9]*) ;;
  *) echo "runtime-manifest: '$VERSION' is not a version of three numbers (1.2.3)." >&2; exit 2 ;;
esac
case "$RID" in
  win-x64|linux-x64) ;;
  *) echo "runtime-manifest: the runtime package is published for win-x64 and linux-x64 only, not '$RID'." >&2; exit 2 ;;
esac
cat <<YAML
schema: package/1
name: dbdatastore-runtime
description: "The dbdatastore runtime $VERSION for $RID: deploys a model's tables, run by a model's definition package"
runtime: true
version: "$VERSION"
platform: $RID
launch: { kind: executable, command: dotnet, args: ["{runtime}/bin/dbdatastore.dll", "--runner"] }
built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }
YAML
