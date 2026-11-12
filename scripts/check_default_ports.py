#!/usr/bin/env python3
"""No two services of the suite default to the same listening port (plan 33 SC19).

The suite installs every server on one machine (`dbdatakit-full`), so each listener of each service needs a default of its own. This reads the
default of each listener from the source that declares it (a file and a regular expression whose group 1 is the port) and fails when
  * a declaration is not found (the entry here is out of date: a renamed constant or a moved default must be fixed here too), or
  * two listeners share a port.
Run from anywhere: python3 -I scripts/check_default_ports.py [root]
"""
import os
import re
import sys

# (service and listener, file, pattern with the port as group 1; the Nth match counts when `nth` is given)
LISTENERS = [
    ("DbDataKit (the console)", "DbDataKit/src/DbDataKit.Server/ConsoleSettings.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1),
    ("DbDataKit identity home on its own", "DbDataKit/src/DbDataKit.Server/IdentityHost.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1),
    ("DbDataBuild", "DbDataBuild/src/DbDataBuild.Cli/Server/BuildServer.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1),
    ("DbDataWorkspace", "DbDataWorkspace/src/DbDataWorkspace.Server/WorkspaceSettings.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1),
    ("DbDataStore", "DbDataStore/src/DbDataStore.Server/StoreSettings.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1),
    ("DbDataRunner", "DbDataRunner/src/DbDataRunner.Server/RunnerSettings.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1),
    ("DbDataSync", "DbDataSync/src/DbDataSync.Api/Configuration/ApiOptions.cs", r'DefaultUrl = "https?://[^":]+:(\d+)"', 1),
    ("DbDataGateway management", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8816);', 1),
    ("DbDataGateway Flight", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8815);', 1),
    ("DbDataGateway identity", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8817);', 1),
    ("DbDataGateway apps", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8818);', 1),
    ("DbDataGateway store module", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8819);', 1),
    ("DbDataGateway views", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8820);', 1),
    ("DbDataGateway mcp", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8821);', 1),
]


def read(root, rel):
    with open(os.path.join(root, rel), encoding="utf-8") as f:
        return f.read()


def check(root, listeners=LISTENERS):
    problems = []
    seen = {}
    for name, rel, pattern, nth in listeners:
        try:
            text = read(root, rel)
        except OSError:
            problems.append(f"{name}: {rel} does not exist; fix the entry in scripts/check_default_ports.py")
            continue
        found = re.findall(pattern, text)
        if len(found) < nth:
            problems.append(f"{name}: no default port found in {rel} (pattern {pattern}); fix the entry in scripts/check_default_ports.py")
            continue
        port = int(found[nth - 1])
        if port in seen:
            problems.append(f"port {port} is the default of both '{seen[port]}' and '{name}'")
        else:
            seen[port] = name
    return problems, seen


def main(argv):
    root = argv[1] if len(argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    problems, seen = check(root)
    for p in problems:
        print(p)
    if problems:
        return 1
    print(f"{len(seen)} default ports, none shared")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
