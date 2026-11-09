#!/usr/bin/env python3
"""The plans in docs/plans do not rot: finished work does not stay behind as a plan that says it is open, and nothing links to a plan that
is gone (CLAUDE.md, "Making changes"). Checks, on docs/plans/*.md:

  links    every relative markdown link to a plan file (NN-name.md, NNa-name.md) resolves
  numbers  every "plan N" / "plans N to M" / "plans N, M and K" in README.md names a plan number that has a file
  finished a plan whose status line says proposed / in progress / being worked, and whose slice table has rows that are ALL marked done
           or built, is flagged: delete it or cut it to an "as built" note (a row counts as slice row when its first cell is an id such
           as R01, DR1, CI03; a row is done when it says `Status: done` or `Status: built`; a plan with no such row is never flagged)
  closed   a plan whose status line says closed / dropped / superseded / marked for deletion is flagged: a plan that is over is deleted, and what is
           still useful in it becomes documentation (CLAUDE.md, "A finished plan becomes a product doc"); it is never kept with a status that says so
  index    README.md lists every plan file and links to no plan file that is missing
           (a row whose link goes to a document that is not that plan's own file, such as `../releases.md` or the merged `22a-gap-slices.md` for
           plan 22, is a pointer: its number stays known, so "plan 9" in a source comment or in the index still resolves)

Violations that exist today and cannot be fixed yet are recorded in scripts/plan_check_baseline.txt, one `file: rule` per line. They are
printed as warnings and do not fail the check; the list must only shrink: an entry with no violation behind it fails the check.
Run from anywhere: python3 -I scripts/check_plans.py [root]
"""
import os
import re
import sys

PLAN_FILE = re.compile(r"^(\d\d[a-z]?)-.*\.md$")
LINK = re.compile(r"\]\(([^)\s#]+\.md)(?:#[^)]*)?\)")
SLICE_ROW = re.compile(r"^\|\s*\**([A-Z]{1,3}\d+[a-z]?)\**\s*\|")
DONE = re.compile(r"status:?\**:?\s*\**(done|built)\b", re.I)
OPEN_STATUS = re.compile(r"^\**status:?\**:?\s*\**\s*(proposed|in progress|being worked)\b", re.I)
OVER_STATUS = re.compile(r"^\**status:?\**:?\s*\**\s*(closed|dropped|superseded|marked for deletion|to be deleted)\b", re.I)
PLAN_REF = re.compile(r"\bplans?\s+(\d{1,2}[a-z]?)((?:\s*(?:,|and|to)\s*\d{1,2}[a-z]?\b)*)", re.I)
INDEX_ROW = re.compile(r"^\|\s*(\d\d[a-z]?)\s*\|[^|]*\]\(([^)]*\.md)\)", re.M)
BASELINE = "scripts/plan_check_baseline.txt"


def plan_files(plans_dir):
    return sorted(n for n in os.listdir(plans_dir) if PLAN_FILE.match(n))


def known_numbers(files):
    return {PLAN_FILE.match(n).group(1) for n in files}


def number_of(text):
    m = re.match(r"(\d+)([a-z]?)", text.lower())
    return f"{int(m.group(1)):02d}{m.group(2)}"


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def violations(root):
    """Returns {(file, rule): [messages]}."""
    plans_dir = os.path.join(root, "docs", "plans")
    files = plan_files(plans_dir)
    numbers = known_numbers(files)
    found = {}

    def add(file, rule, message):
        found.setdefault((file, rule), []).append(message)

    for name in sorted(n for n in os.listdir(plans_dir) if n.endswith(".md")):
        text = read(os.path.join(plans_dir, name))
        for target in LINK.findall(text):
            if "://" in target or not PLAN_FILE.match(os.path.basename(target)):
                continue
            if not os.path.exists(os.path.normpath(os.path.join(plans_dir, target))):
                add(name, "links", f"links to {target}, which does not exist")
        status = next((l for l in text.splitlines() if re.match(r"\**status\b", l, re.I)), "")
        if PLAN_FILE.match(name) and OVER_STATUS.match(status.strip()):
            add(name, "closed", "its status says the plan is over: delete it and turn what is still useful into documentation")
        if PLAN_FILE.match(name) and OPEN_STATUS.match(status.strip()):
            rows = [l for l in text.splitlines() if SLICE_ROW.match(l)]
            if rows and all(DONE.search(l) for l in rows):
                add(name, "finished", f"its status says open but all {len(rows)} slice rows are done: delete the plan or cut it to an as-built note")

    readme = read(os.path.join(plans_dir, "README.md"))
    for m in INDEX_ROW.finditer(readme):
        # a row whose link goes to a document that is not that plan's own file (`../releases.md`, a package README, or the merged file
        # of the plan, `22a-gap-slices.md` for plan 22) is a pointer: its number stays known
        if not os.path.basename(m.group(2)).startswith(m.group(1) + "-"):
            numbers.add(number_of(m.group(1)))
    for m in PLAN_REF.finditer(readme):
        for ref in [m.group(1)] + re.findall(r"\d{1,2}[a-z]?\b", m.group(2)):
            if number_of(ref) not in numbers:
                add("README.md", "numbers", f'"{m.group(0).strip()}" names plan {ref}, which has no file')
    listed = set()
    for target in LINK.findall(readme):
        base = os.path.basename(target)
        if PLAN_FILE.match(base):
            listed.add(base)
            if base not in files:
                add("README.md", "index", f"lists {base}, which does not exist")
    for name in files:
        if name not in listed:
            add("README.md", "index", f"does not list {name}")
    return found


def load_baseline(root):
    path = os.path.join(root, BASELINE)
    if not os.path.exists(path):
        return set()
    entries = set()
    for line in read(path).splitlines():
        line = line.strip()
        if line and not line.startswith("#"):
            file, _, rule = line.partition(": ")
            entries.add((file.strip(), rule.strip()))
    return entries


def check(root):
    """Returns (problems, warnings)."""
    found = violations(root)
    baseline = load_baseline(root)
    problems, warnings = [], []
    for key in sorted(found):
        target = warnings if key in baseline else problems
        for message in found[key]:
            target.append(f"{key[0]}: {key[1]}: {message}")
    for key in sorted(baseline - set(found)):
        problems.append(f"{BASELINE}: '{key[0]}: {key[1]}' no longer happens: remove the line")
    return problems, warnings


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems, warnings = check(root)
    for w in warnings:
        print(f"warning (baseline): {w}")
    for p in problems:
        print(p)
    print(f"{len(problems)} problem(s)" if problems else "the plans are consistent")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
