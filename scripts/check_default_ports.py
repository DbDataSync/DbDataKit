#!/usr/bin/env python3
"""The suite's listening ports: one range, a sub-block per service, no shared default and no Windows default port.

The suite installs every server on one machine (`dbdatakit-full`), so each listener of each service needs a default of its own, and the defaults stay
out of the ports Windows uses itself. The design is docs/install.md, "Ports". This reads
  * the suite range and the sub-blocks from the startup library's table of services (`SuiteServices.cs`, declarations
    `public static PortRange <Name>Ports { get; } = new(<first>, <last>);`, `Suite` being the whole range), and
  * the default of each listener from the source that declares it (a file and a regular expression whose group 1 is the port),
and fails when
  * a declaration is not found (the entry here is out of date: a renamed constant or a moved default must be fixed here too),
  * a sub-block lies outside the suite range, or two sub-blocks overlap,
  * a listener's default lies outside its service's sub-block (and so outside the suite range),
  * two listeners share a port, or
  * the suite range, a sub-block or a default holds one of Windows' default ports (WINDOWS_PORTS).
A listener with no sub-block (the Gateway's, whose defaults are still fixed ports outside the range) is checked for the last two only.
Run from anywhere: python3 -I scripts/check_default_ports.py [root]
"""
import os
import re
import sys

TABLE = "Shared/src/DbDataKit.Hosting.Startup/SuiteServices.cs"
_BLOCK = re.compile(r"public\s+static\s+PortRange\s+(\w+)Ports\s*\{\s*get;\s*\}\s*=\s*new\(\s*(\d+)\s*,\s*(\d+)\s*\)\s*;")

# Ports Windows listens on or hands out itself: HTTP, HTTPS, RPC, SMB, Remote Desktop, CDP, WSD, WinRM (HTTP, HTTPS), Delivery Optimization,
# the WinRM compatibility listener, and the dynamic range.
WINDOWS_PORTS = [(80, 80), (443, 443), (135, 135), (445, 445), (3389, 3389), (5040, 5040), (5357, 5357), (5985, 5985), (5986, 5986),
                 (7680, 7680), (47001, 47001), (49152, 65535)]

# (service and listener, file, pattern with the port as group 1, the Nth match that counts, the sub-block in SuiteServices.cs or None)
LISTENERS = [
    ("DbDataKit (the console)", "DbDataKit/src/DbDataKit.Server/ConsoleSettings.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1, "Console"),
    ("DbDataKit identity home on its own", "DbDataKit/src/DbDataKit.Server/IdentityHost.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1, "Console"),
    ("DbDataWorkspace", "DbDataWorkspace/src/DbDataWorkspace.Server/WorkspaceSettings.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1, "Workspace"),
    ("DbDataRunner", "DbDataRunner/src/DbDataRunner.Server/RunnerSettings.cs", r'DefaultListen = "https://[^":]+:(\d+)"', 1, "Runner"),
    ("DbDataGateway management", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8816);', 1, None),
    ("DbDataGateway Flight", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8815);', 1, None),
    ("DbDataGateway identity", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8817);', 1, None),
    ("DbDataGateway apps", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8818);', 1, None),
    ("DbDataGateway store module", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8819);', 1, None),
    ("DbDataGateway views", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8820);', 1, None),
    ("DbDataGateway mcp", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8822);', 1, None),
    ("DbDataGateway feeds (REST)", "DbDataGateway/src/DbDataGateway.Server/Configuration/GatewayOptions.cs", r'public int Port \{ get; set; \} = (8821);', 1, None),
]


def read(root, rel):
    with open(os.path.join(root, rel), encoding="utf-8") as f:
        return f.read()


def describe(first, last):
    return str(first) if first == last else f"{first} to {last}"


def windows_ports_in(first, last):
    """Windows' default ports between first and last, as text."""
    return [describe(max(a, first), min(b, last)) for a, b in WINDOWS_PORTS if a <= last and b >= first]


def read_blocks(root, table, problems):
    """The sub-blocks of the table, {name: (first, last)}; problems found in it are added to problems."""
    try:
        text = read(root, table)
    except OSError:
        problems.append(f"{table} does not exist; fix TABLE in scripts/check_default_ports.py")
        return {}
    blocks = {}
    for name, first, last in _BLOCK.findall(text):
        first, last = int(first), int(last)
        if first > last:
            problems.append(f"{table}: {name}Ports starts at {first} after it ends at {last}")
        blocks[name] = (first, last)
    if "Suite" not in blocks:
        problems.append(f"{table}: no SuitePorts declaration (public static PortRange SuitePorts {{ get; }} = new(<first>, <last>);)")
        return blocks
    suite = blocks["Suite"]
    for hit in windows_ports_in(*suite):
        problems.append(f"the suite range {describe(*suite)} holds Windows' default port {hit}")
    subs = [(name, b) for name, b in blocks.items() if name != "Suite"]
    for name, (first, last) in subs:
        if first < suite[0] or last > suite[1]:
            problems.append(f"the sub-block {name} ({describe(first, last)}) lies outside the suite range {describe(*suite)}")
    for i, (a_name, (a_first, a_last)) in enumerate(subs):
        for b_name, (b_first, b_last) in subs[i + 1:]:
            if a_first <= b_last and b_first <= a_last:
                problems.append(f"the sub-blocks {a_name} ({describe(a_first, a_last)}) and {b_name} ({describe(b_first, b_last)}) overlap")
    return blocks


def check(root, listeners=LISTENERS, table=TABLE):
    problems = []
    blocks = read_blocks(root, table, problems)
    seen = {}
    for name, rel, pattern, nth, block in listeners:
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
        for hit in windows_ports_in(port, port):
            problems.append(f"'{name}' defaults to {hit}, one of Windows' default ports")
        if block is None:
            continue
        if block not in blocks:
            problems.append(f"{name}: the sub-block {block} is not declared in {table}; fix the entry in scripts/check_default_ports.py")
            continue
        first, last = blocks[block]
        if not first <= port <= last:
            problems.append(f"'{name}' defaults to {port}, outside its sub-block {block} ({describe(first, last)})")
    return problems, seen


def main(argv):
    root = argv[1] if len(argv) > 1 else os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    problems, seen = check(root)
    for p in problems:
        print(p)
    if problems:
        return 1
    print(f"{len(seen)} default ports, none shared, each in its sub-block of the suite range or a listener with no sub-block yet")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
