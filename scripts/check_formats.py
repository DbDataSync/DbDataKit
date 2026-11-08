#!/usr/bin/env python3
"""The freeze rule of formats (plan 24a UC02, "The freeze rule of formats"). docs/formats.json lists each definition and file format
(id, current and previous version, open or frozen). Checks:

  version   the version number the code declares for a format (`version_source`: a file and a regular expression whose group 1 is the
            number) equals `current` in docs/formats.json. A number bumped in the code without an entry in the file fails, and so does an
            entry the code does not say yet.
  frozen    every file of a frozen format (`files`: the schema or definition text, path -> sha256) still has the recorded hash. A frozen
            format is never edited: the change is the next number, with a new entry (current + 1, previous = the old current) and a new
            hash. Line endings do not count (CRLF is read as LF), so Windows and Linux agree.
  entry     each entry is well formed: a unique id, `current` and `previous` whole numbers with previous <= current, a status of `open` or
            `frozen`, a version source that matches, files that exist, and a hash on every file of a frozen format.

An open format records no hashes (`sha256` is null): it may still be edited. To freeze one, set its status to frozen and write the hashes
printed by `python3 -I scripts/check_formats.py --hash <file>`.
Run from anywhere: python3 -I scripts/check_formats.py [root]
"""
import hashlib
import json
import os
import re
import sys

REGISTRY = "docs/formats.json"
STATUSES = ("open", "frozen")


def read_bytes(path):
    with open(path, "rb") as f:
        return f.read()


def file_hash(path):
    """sha256 of the file with CRLF read as LF."""
    return hashlib.sha256(read_bytes(path).replace(b"\r\n", b"\n")).hexdigest()


def check(root):
    """Returns the list of problems."""
    path = os.path.join(root, *REGISTRY.split("/"))
    if not os.path.exists(path):
        return [f"{REGISTRY}: the format registry is missing"]
    try:
        with open(path, encoding="utf-8") as f:
            registry = json.load(f)
    except ValueError as e:
        return [f"{REGISTRY}: not valid JSON: {e}"]
    entries = registry.get("formats") if isinstance(registry, dict) else None
    if not isinstance(entries, list) or not entries:
        return [f"{REGISTRY}: `formats` must be a list with at least one format"]

    problems = []
    seen = set()
    for index, entry in enumerate(entries):
        name = entry.get("id") if isinstance(entry, dict) else None
        where = f"{REGISTRY}: format '{name}'" if isinstance(name, str) and name else f"{REGISTRY}: formats[{index}]"
        if not isinstance(entry, dict) or not isinstance(name, str) or not name:
            problems.append(f"{where}: an entry is an object with an `id`")
            continue
        if name in seen:
            problems.append(f"{where}: listed twice")
        seen.add(name)

        current, previous, status = entry.get("current"), entry.get("previous"), entry.get("status")
        numbers_ok = all(isinstance(n, int) and not isinstance(n, bool) and n >= 1 for n in (current, previous))
        if not numbers_ok:
            problems.append(f"{where}: `current` and `previous` are whole numbers from 1")
        elif previous > current:
            problems.append(f"{where}: `previous` ({previous}) is above `current` ({current})")
        if status not in STATUSES:
            problems.append(f"{where}: `status` is `open` or `frozen`, not {status!r}")

        source = entry.get("version_source")
        if not isinstance(source, dict) or not isinstance(source.get("file"), str) or not isinstance(source.get("pattern"), str):
            problems.append(f"{where}: `version_source` names a `file` and a `pattern` (group 1 is the number)")
        else:
            problems.extend(check_version(root, where, source, current if numbers_ok else None))

        files = entry.get("files")
        if not isinstance(files, list) or not files:
            problems.append(f"{where}: `files` lists the schema or definition files of the format")
            continue
        for item in files:
            if not isinstance(item, dict) or not isinstance(item.get("path"), str):
                problems.append(f"{where}: each of `files` has a `path`")
                continue
            problems.extend(check_file(root, where, item, status))
    return problems


def check_version(root, where, source, current):
    file = os.path.join(root, *source["file"].split("/"))
    if not os.path.exists(file):
        return [f"{where}: its version source {source['file']} does not exist"]
    try:
        found = re.findall(source["pattern"], read_bytes(file).decode("utf-8"))
    except re.error as e:
        return [f"{where}: its version pattern is not a regular expression: {e}"]
    if len(found) != 1:
        return [f"{where}: the pattern {source['pattern']!r} matches {len(found)} times in {source['file']}; it must match once"]
    number = found[0] if isinstance(found[0], str) else found[0][0]
    if current is not None and number != str(current):
        return [f"{where}: {source['file']} declares version {number} but {REGISTRY} says current {current}: "
                f"a bumped format needs its entry (current {number}, previous {current}, the new hashes) in the same change"]
    return []


def check_file(root, where, item, status):
    rel = item["path"]
    file = os.path.join(root, *rel.split("/"))
    recorded = item.get("sha256")
    if not os.path.exists(file):
        return [f"{where}: {rel} does not exist"]
    if status != "frozen":
        return []
    if not isinstance(recorded, str) or not re.fullmatch(r"[0-9a-f]{64}", recorded):
        return [f"{where}: frozen, so {rel} needs its sha256 (python3 -I scripts/check_formats.py --hash {rel})"]
    actual = file_hash(file)
    if actual != recorded:
        return [f"{where}: frozen, but {rel} changed (sha256 {actual[:12]}..., recorded {recorded[:12]}...): a frozen format is not edited; "
                f"the change is the next number, with a new entry in {REGISTRY}"]
    return []


def main(argv):
    if len(argv) == 3 and argv[1] == "--hash":
        print(file_hash(argv[2]))
        return 0
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems = check(root)
    for p in problems:
        print(p)
    print(f"{len(problems)} problem(s)" if problems else "the formats agree with docs/formats.json")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
