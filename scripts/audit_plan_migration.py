#!/usr/bin/env python3
"""Every item of the deleted plans is traced (planning/wip/MIGRATION/trace.md).

The commit that created planning/ deleted the old plans folder. This audit reads that folder from git at a commit (default 127c80e8^, the commit
before the deletion), extracts every item a person could lose and fails, listing each one, when the trace file has no row for it:

  row ids    the first cell of every table row: each id in it (`SC23`, `E17`, `DBR53b`, `QF6`, `I00`, `D18 (was Q1)` gives D18 and Q1), or a plain
             number (`7` is `#7`; `3, 16` gives #3 and #16). Rows added by a diff inside a fenced block (`+| I01 | ...`) count.
  headings   every second- and third-level heading outside fenced blocks (and `+## ...` lines of a diff).
  lists      every numbered list item directly under a heading that names decisions, defaults, follow-ups, questions or rules: the key is
             `<heading> #<n>`.

A trace row is `| old file | ids or heading | summary | old status | outcome |`; old file is the path under the old plans folder
(`22a-gap-slices.md`, `needs-review/13a-lane-I-addition.md`). A row id is covered by a row of the same file whose second cell holds it as a whole
token; a heading by a row whose second cell holds its text; a list item by a row whose second cell holds its heading's text and `#<n>`.

Run from the repository root: python3 -I scripts/audit_plan_migration.py [--commit REV] [--trace PATH] [--list]
--list prints every extracted item (file, kind, key) and exits 0.
"""
import os
import re
import subprocess
import sys

DEFAULT_COMMIT = "127c80e8^"
OLD_FOLDER = "docs/" + "plans/"
DEFAULT_TRACE = os.path.join("planning", "wip", "MIGRATION", "trace.md")

ID = re.compile(r"(?<![A-Za-z0-9])([A-Z]{1,4}\d{1,3}[a-z]?)(?![A-Za-z0-9])")
NUMBERS = re.compile(r"^\d{1,3}[a-z]?(\s*,\s*\d{1,3}[a-z]?)*$")
HEADING = re.compile(r"^(#{2,3})\s+(.*?)\s*#*\s*$")
LIST_ITEM = re.compile(r"^(\d{1,3})\.\s+\S")
LIST_HEADINGS = re.compile(r"decision|decided|default|follow-up|question|rule", re.IGNORECASE)
SEPARATOR = re.compile(r"^\|\s*:?-{3,}")


def clean(text):
    """Markdown links become their text; bold, code and escapes go; spaces collapse."""
    text = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", text)
    text = text.replace("**", "").replace("`", "").replace("\\|", "|")
    return re.sub(r"\s+", " ", text).strip()


def split_row(line):
    """Cells of a markdown table row, splitting on pipes that are not escaped."""
    body = line.strip()
    if body.startswith("|"):
        body = body[1:]
    if body.endswith("|") and not body.endswith("\\|"):
        body = body[:-1]
    return [cell.strip() for cell in re.split(r"(?<!\\)\|", body)]


def extract(text):
    """The items of one old file: a list of (kind, key), kind in 'row', 'heading', 'list', in order, without repeats."""
    items = []
    fence = False
    heading = ""
    for raw in text.splitlines():
        if raw.lstrip().startswith("```"):
            fence = not fence
            continue
        line = raw
        if fence:
            if not raw.startswith("+") or raw.startswith("+++"):
                continue
            line = raw[1:]
        m = HEADING.match(line)
        if m:
            heading = clean(m.group(2))
            items.append(("heading", heading))
            continue
        if line.startswith("|"):
            if SEPARATOR.match(line):
                continue
            first = clean(split_row(line)[0])
            if NUMBERS.match(first):
                items.extend(("row", "#" + n.strip()) for n in first.split(","))
            else:
                items.extend(("row", i) for i in ID.findall(first))
            continue
        m = LIST_ITEM.match(line)
        if m and heading and LIST_HEADINGS.search(heading):
            items.append(("list", f"{heading} #{m.group(1)}"))
    return list(dict.fromkeys(items))


def git(*args, cwd=None):
    return subprocess.run(["git", *args], capture_output=True, text=True, check=True, cwd=cwd).stdout


def old_files(commit, cwd=None):
    """{path under the old plans folder: text} at the commit."""
    names = git("ls-tree", "-r", "--name-only", commit, "--", OLD_FOLDER, cwd=cwd).splitlines()
    return {n[len(OLD_FOLDER):]: git("show", f"{commit}:{n}", cwd=cwd) for n in names if n.endswith(".md")}


def trace_rows(text):
    """{old file: [the cleaned second cell of each of its rows]} from the trace's tables."""
    rows = {}
    for line in text.splitlines():
        if not line.startswith("|") or SEPARATOR.match(line):
            continue
        cells = split_row(line)
        if len(cells) < 2:
            continue
        name = clean(cells[0])
        rows.setdefault(name, []).append(clean(cells[1]))
    return rows


def token_in(key, cell):
    return re.search(r"(?<![A-Za-z0-9#])" + re.escape(key) + r"(?![A-Za-z0-9])", cell) is not None


def covered(kind, key, cells):
    if kind == "row":
        return any(token_in(key, c) for c in cells)
    if kind == "heading":
        return any(key in c for c in cells)
    head, number = key.rsplit(" #", 1)
    return any(head in c and token_in("#" + number, c) for c in cells)


def missing(files, trace_text):
    """[(file, kind, key)] extracted from the old files and not in the trace."""
    rows = trace_rows(trace_text)
    out = []
    for name in sorted(files):
        cells = rows.get(name, [])
        for kind, key in extract(files[name]):
            if not covered(kind, key, cells):
                out.append((name, kind, key))
    return out


def main(argv):
    commit, trace, listing = DEFAULT_COMMIT, DEFAULT_TRACE, False
    args = list(argv[1:])
    while args:
        a = args.pop(0)
        if a == "--commit" and args:
            commit = args.pop(0)
        elif a == "--trace" and args:
            trace = args.pop(0)
        elif a == "--list":
            listing = True
        else:
            print(__doc__)
            return 2
    files = old_files(commit)
    if not files:
        print(f"audit_plan_migration: no files under {OLD_FOLDER} at {commit}")
        return 1
    if listing:
        for name in sorted(files):
            for kind, key in extract(files[name]):
                print(f"{name}\t{kind}\t{key}")
        return 0
    try:
        with open(trace, encoding="utf-8") as f:
            text = f.read()
    except OSError as e:
        print(f"audit_plan_migration: cannot read the trace {trace}: {e}")
        return 1
    found = missing(files, text)
    total = sum(len(extract(t)) for t in files.values())
    if found:
        print(f"audit_plan_migration: {len(found)} of {total} items of the old plans at {commit} have no row in {trace}:")
        for name, kind, key in found:
            print(f"  {name}: {kind} {key}")
        return 1
    print(f"audit_plan_migration: all {total} items of {len(files)} old files at {commit} are traced in {trace}")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
