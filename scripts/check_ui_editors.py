#!/usr/bin/env python3
"""docs/ui-editors.md says, for everything a person can author or publish, where its editor screen is and how complete it is.

The table is written by hand (read the code, do not guess); this check keeps it from drifting from the repository:
  1. every format registered in docs/formats.json is named (in backticks) in the Format column of at least one row, so a new stored format
     cannot arrive without a row that says whether it has an editor;
  2. every backticked id in a Format column is a registered format;
  3. every row has a state: full, partial, view-only, none or planned (the first word of the State cell);
  4. a state other than full says why or what closes it: a slice id in the Gap column (UE01 of plan 33, or another plan's slice such as
     DBR27) or a reason in parentheses in the State cell (`none (reason)`, the documented exemption);
  5. every UEnn named in the table is a slice of docs/plans/33-ui-editors.md;
  6. every `Name.tsx` named in the Editor column exists under DbDataKit/web/*/src;
  7. the `Counts:` line equals the count of the rows' states.
Run from anywhere: python3 scripts/check_ui_editors.py [root]
"""
import json
import os
import re
import sys

STATES = ("full", "partial", "view-only", "none", "planned")
HEADER = re.compile(r"^\|\s*Feature\s*\|")
CELL_SPLIT = re.compile(r"(?<!\\)\|")
SLICE_ID = re.compile(r"\b[A-Z]{1,3}\d+[a-z]?\b")
UE_ID = re.compile(r"\bUE\d+\b")
BACKTICKED = re.compile(r"`([^`]+)`")
TSX = re.compile(r"`([A-Za-z0-9_]+\.tsx)`")
COUNTS = re.compile(r"^Counts: full (\d+), partial (\d+), view-only (\d+), none (\d+), planned (\d+)\.\s*$", re.M)
PLAN_ROW = re.compile(r"^\|\s*\**(UE\d+)\**\s*\|", re.M)
DOC = "docs/ui-editors.md"
PLAN = "docs/plans/33-ui-editors.md"


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def rows(text):
    """The rows of the editors table as lists of six cells (the header and the separator left out)."""
    found, inside = [], False
    for line in text.splitlines():
        if HEADER.match(line):
            inside = True
            continue
        if not inside:
            continue
        if not line.startswith("|"):
            break
        cells = [c.strip() for c in CELL_SPLIT.split(line.strip())[1:-1]]
        if all(re.fullmatch(r":?-{3,}:?", c) for c in cells):
            continue
        found.append(cells)
    return found


def screen_files(root):
    names = set()
    web = os.path.join(root, "DbDataKit", "web")
    if not os.path.isdir(web):
        return names
    for module in os.listdir(web):
        for folder, dirs, files in os.walk(os.path.join(web, module, "src")):
            dirs[:] = [d for d in dirs if d != "node_modules"]
            names.update(n for n in files if n.endswith(".tsx"))
    return names


def problems(root):
    doc_path = os.path.join(root, DOC)
    if not os.path.exists(doc_path):
        return [f"{DOC} does not exist"]
    text = read(doc_path)
    out = []
    registry_path = os.path.join(root, "docs", "formats.json")
    registered = [f["id"] for f in json.loads(read(registry_path))["formats"]] if os.path.exists(registry_path) else []
    plan_path = os.path.join(root, PLAN)
    slices = set(PLAN_ROW.findall(read(plan_path))) if os.path.exists(plan_path) else set()
    table = rows(text)
    if not table:
        return [f"{DOC} has no table whose header starts with | Feature |"]
    existing = screen_files(root)
    named_formats, counts = set(), dict.fromkeys(STATES, 0)
    for cells in table:
        if len(cells) != 6:
            out.append(f"{DOC}: a row has {len(cells)} cells, not 6 (Feature, Authored, Format, Editor, State, Gap): {cells[0] if cells else ''!r}")
            continue
        feature, _authored, formats, editor, state, gap = cells
        for ident in BACKTICKED.findall(formats):
            named_formats.add(ident)
            if ident not in registered:
                out.append(f"{DOC}: '{feature}' names format `{ident}`, which docs/formats.json does not register")
        word = re.match(r"[a-z-]+", state)
        word = word.group(0) if word else ""
        if word not in STATES:
            out.append(f"{DOC}: '{feature}' has state '{state}': the first word is one of {', '.join(STATES)}")
            continue
        counts[word] += 1
        if word != "full" and not SLICE_ID.search(gap) and "(" not in state:
            out.append(f"{DOC}: '{feature}' is {word} and says neither which slice closes the gap nor why not: name a slice in the last cell or write `{word} (reason)`")
        for ue in UE_ID.findall(gap):
            if ue not in slices:
                out.append(f"{DOC}: '{feature}' names {ue}, which is not a slice of {PLAN}")
        for name in TSX.findall(editor):
            if name not in existing:
                out.append(f"{DOC}: '{feature}' names {name}, which is not a file under DbDataKit/web/*/src")
    for ident in registered:
        if ident not in named_formats:
            out.append(f"{DOC} has no row for the stored format `{ident}` (docs/formats.json): add a row with it in the Format column and a state, "
                       f"or the state `none (reason)` when no editor is wanted")
    m = COUNTS.search(text)
    if not m:
        out.append(f"{DOC} has no line 'Counts: full N, partial N, view-only N, none N, planned N.'")
    elif tuple(int(g) for g in m.groups()) != tuple(counts[s] for s in STATES):
        said = ", ".join(f"{s} {n}" for s, n in zip(STATES, m.groups()))
        have = ", ".join(f"{s} {counts[s]}" for s in STATES)
        out.append(f"{DOC}: the Counts line says {said}, the rows say {have}")
    return out


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    found = problems(root)
    for line in found:
        print(line)
    print("OK" if not found else f"{len(found)} problem(s)")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
