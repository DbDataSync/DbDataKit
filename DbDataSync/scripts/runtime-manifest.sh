#!/usr/bin/env bash
# The manifest (package/1) of the Sync runtime package (plan 28c E06, section 4.1 of the plan): text on stdout.
#   scripts/runtime-manifest.sh <rid> <version>
# The text is what PackageManifestWriter writes (a test holds the two equal). <version> is the Sync release as three numbers (1.2.3; a suffix such as -beta.4 is cut off, and so is a four-number release (2026.11.08.1234: year, month, day, time) becomes three by folding the last two, 2026.11.81234, so that two releases of one day differ and the order is kept).
# One package per platform: the manifest says `platform: <rid>` and the runner stores the package as dbdatasync-runtime-<rid> (PackageManifest.PackageName), as Build's does (E05).
# The engine is the framework-dependent TaskRunner started through the dotnet host of the runner (the runner is .NET and has the runtime, `requiresDotnet: 10.0` in dist.json); it needs a view of copies
# (the host resolves a link and looks for runtimeconfig.json beside the stored object), which Windows makes and Linux links do not: the same note as Build's --framework-dependent layout (E05).
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
  *) echo "runtime-manifest: the runtime package is published for win-x64 and linux-x64 only (plan 28c, decision QE8), not '$RID'." >&2; exit 2 ;;
esac
cat <<YAML
schema: package/1
name: dbdatasync-runtime
description: "The dbdatasync engine $VERSION for $RID, run by definition packages that name it"
runtime: true
version: "$VERSION"
platform: $RID
launch: { kind: executable, command: dotnet, args: ["{runtime}/bin/DbDataSync.TaskRunner.dll", "--runner"] }
built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }
YAML
