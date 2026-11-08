#!/usr/bin/env python3
"""Every product whose command line has the shared `state` verbs (plan 29b C03) must say in its dist.json `notes` how to move its state off the
default SQLite file, by naming `<program> state init`. Standard library only. Exit code 1 when a note is missing."""
import json
import os
import sys

# product folder -> program name; DbDataSync has no `state` noun yet (it is not listed)
PRODUCTS = {"DbDataConsole": "dbdatakit", "DbDataGateway": "dbdatagateway", "DbDataBuild": "dbdatabuild"}


def problems(root):
    found = []
    for folder, program in PRODUCTS.items():
        path = os.path.join(root, folder, "dist.json")
        try:
            with open(path, encoding="utf-8") as f:
                notes = json.load(f).get("notes", "")
        except (OSError, ValueError) as ex:
            found.append(f"{folder}/dist.json cannot be read: {ex}")
            continue
        if f"{program} state init" not in notes:
            found.append(f"{folder}/dist.json: `notes` must name `{program} state init` (how to put the state in a database shared by a group or the install)")
    return found


def main(root):
    found = problems(root)
    for line in found:
        print(line)
    if not found:
        print("every product with the state verbs names `state init` in its dist.json notes")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
