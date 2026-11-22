#!/usr/bin/env python3
"""The suite's services as the startup library's table spells them (Shared/src/DbDataKit.Hosting.Startup/SuiteServices.cs).

That table is the one place a service's Windows name, account and description are written. The installers read it through this file, so a product's
dist.json names no service of the suite: dist.py takes the Windows service name of each suite product from here, and the Scoop install test reads the
names and descriptions it checks with `sc qc` and `sc qdescription` from here too.

    python3 -I dist/suite_services.py            # the table as JSON, in start order

Design: docs/install.md#service-names-accounts-and-descriptions
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent
TABLE = pathlib.Path("Shared") / "src" / "DbDataKit.Hosting.Startup" / "SuiteServices.cs"

# public static SuiteService Gateway { get; } = new("dbdatagateway", "DbDataGateway", "<description>", "gateway", 2);
_ENTRY = re.compile(
    r'public\s+static\s+SuiteService\s+(\w+)\s*\{\s*get;\s*\}\s*=\s*new\(\s*'
    r'"([a-z][a-z0-9]*)"\s*,\s*"(\w+)"\s*,\s*"([^"\\]+)"\s*,\s*"([a-z][a-z0-9]*)"\s*,\s*(\d+)\s*\)\s*;'
)


def load(root=ROOT):
    """The services in start order: [{key, program, windowsName, account, displayName, description, dataFolder, startOrder}]."""
    path = pathlib.Path(root) / TABLE
    try:
        text = path.read_text(encoding="utf-8")
    except OSError as e:
        raise SystemExit(f"{path}: the suite's table of services cannot be read ({e})")
    services = []
    for key, program, windows, description, folder, order in _ENTRY.findall(text):
        services.append({
            "key": key,
            "program": program,
            "windowsName": windows,
            "account": "NT SERVICE\\" + windows,
            "displayName": windows,
            "description": description,
            "dataFolder": folder,
            "startOrder": int(order),
        })
    # a declaration this pattern does not read would be a service the installers silently leave out
    declared = len(re.findall(r"public\s+static\s+SuiteService\s+\w+\s*\{", text))
    if not services or declared != len(services):
        raise SystemExit(f"{path}: read {len(services)} of the {declared} services declared there; "
                         "each is written new(\"<program>\", \"<WindowsName>\", \"<description>\", \"<folder>\", <order>), the description without quotes")
    return sorted(services, key=lambda s: s["startOrder"])


def by_program(root=ROOT):
    return {s["program"]: s for s in load(root)}


def main():
    json.dump(load(), sys.stdout, indent=2)
    sys.stdout.write("\n")


if __name__ == "__main__":
    sys.exit(main())
