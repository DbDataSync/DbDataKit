#!/usr/bin/env bash
# The manifest (package/1) of the Build runtime package (plan 28c E05, section 4.1 of the plan): text on stdout.
#   scripts/runtime-manifest.sh <rid> <version> [--framework-dependent]
# The text is what PackageManifestWriter writes (a test holds the two equal). <version> is the Build release as three numbers (1.2.3; a suffix such as -beta.4 is cut off: a range reads numbers only). The layout is documented in docs/operations.md, "The runtime package".
set -euo pipefail
RID="${1:?usage: runtime-manifest.sh <rid> <version> [--framework-dependent]}"
VERSION="${2:?usage: runtime-manifest.sh <rid> <version> [--framework-dependent]}"
VERSION="${VERSION%%[-+]*}"
case "$VERSION" in [0-9]*.[0-9]*.[0-9]*) ;; *) echo "runtime-manifest: '$VERSION' is not a version of three numbers (1.2.3)." >&2; exit 2 ;; esac
case "$RID" in
  win-x64) EXE="dbdatabuild.exe" ;;
  linux-x64) EXE="dbdatabuild" ;;
  *) echo "runtime-manifest: the runtime package is published for win-x64 and linux-x64 only (plan 28c, decision QE8), not '$RID'." >&2; exit 2 ;;
esac
if [ "${3:-}" = "--framework-dependent" ]; then
  # the runner starts the dotnet host on the program's dll; it needs a view of copies (the host resolves a link and looks for runtimeconfig.json beside the stored object)
  LAUNCH="{ kind: executable, command: dotnet, args: [\"{runtime}/bin/dbdatabuild.dll\", \"--runner-task\"] }"
else
  LAUNCH="{ kind: executable, command: bin/$EXE, args: [\"--runner-task\"] }"
fi
cat <<YAML
schema: package/1
name: dbdatabuild-runtime
description: "The dbdatabuild engine $VERSION for $RID, run by definition packages that name it"
runtime: true
version: "$VERSION"
launch: $LAUNCH
built_against: { dotnet: "10.0", kit: "1", protocol: dbdatakit.runner.v1 }
YAML
