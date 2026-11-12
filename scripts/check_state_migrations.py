#!/usr/bin/env python3
"""Expand-then-contract is checked, not trusted (plan 24a UC06, plan 21, plan 29b U02).

A state module that declares `compatibleWithPrevious: N` (N >= 1) says an instance one version behind still works with the schema after the
module's last N migrations. This check reads every such module's migrations (the raw-string list in its `new(...)` call) and fails when one of
those last N migrations is not additive:

  additive  CREATE TABLE / INDEX / VIEW, ALTER TABLE ... add a column that is nullable or has a DEFAULT, INSERT, UPDATE
  removing  DROP, RENAME, ALTER COLUMN, DELETE, TRUNCATE, and a column added NOT NULL with no DEFAULT (the previous code's inserts fail)

A removal is allowed in a migration only when the migration carries a note naming the release in which the previous code stopped using the thing,
a SQL comment line `-- Contract: <release> <what>` (for example `-- Contract: 2026.12.1 stopped reading login_name`). Migrations before the last
N are not judged (the module no longer promises them) but `--list` prints every removal of every module, so none is hidden.
A module whose compatibleWithPrevious is declared but whose migrations are not a literal list of raw strings fails: write them as a list.
Run from anywhere: python3 -I scripts/check_state_migrations.py [--list] [root]
"""
import os
import re
import sys

SKIP_DIRS = {"bin", "obj", "node_modules", ".git", "tests", ".claude"}
CONTRACT = re.compile(r"^[ \t]*--[ \t]*Contract:[ \t]*(\S.*)$", re.M)
COMPAT = re.compile(r"compatibleWithPrevious\s*:\s*(\d+)")
MODULE = re.compile(r"StateModule\s+Module\s*\{\s*get;\s*\}\s*=\s*new\(")


def source_files(root):
    for base, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            if name.endswith(".cs"):
                yield os.path.join(base, name)


SKIP = re.compile(r"(?:\s|//[^\n]*)*")


def raw_strings_of_list(text, start):
    """From `start` (just after the module constructor's `(`), the raw strings of the migration list and the position after its `]`, or (None, start) when the migrations are not a literal list."""
    head = re.compile(r'\s*(?:"[^"]*"|[\w.]+)\s*,\s*[\w.]+\s*,\s*\[').match(text, start)
    if not head:
        return None, start
    out, pos = [], head.end()
    while True:
        pos = SKIP.match(text, pos).end()
        if text.startswith("]", pos):
            return out, pos + 1
        m = re.compile(r'("""+)').match(text, pos)
        if not m:
            return None, start
        quote = m.group(1)
        end = text.find(quote, m.end())
        if end < 0:
            return None, start
        out.append(text[m.end():end])
        pos = end + len(quote)
        pos = SKIP.match(text, pos).end()
        if text.startswith(",", pos):
            pos += 1


def module_compat(text, start):
    """The compatibleWithPrevious of the constructor call whose arguments continue at `start`: the first one before the call's closing `);`."""
    end = re.compile(r"\);").search(text, start)
    segment = text[start:end.end() if end else len(text)]
    m = COMPAT.search(segment)
    return int(m.group(1)) if m else 0


def statements(sql):
    cleaned = "\n".join(re.sub(r"--.*$", "", line) for line in sql.splitlines())
    return [s.strip() for s in cleaned.split(";") if s.strip()]


def removals(sql):
    """The statements of one migration that remove or rename something, or add a column the previous code cannot insert around."""
    found = []
    for s in statements(sql):
        flat = re.sub(r"\s+", " ", s)
        up = flat.upper()
        if re.match(r"^(DROP|TRUNCATE|DELETE) ", up):
            found.append(f"removes: {flat[:90]}")
        elif up.startswith("ALTER TABLE"):
            if re.search(r"\bRENAME\b", up):
                found.append(f"renames: {flat[:90]}")
            elif re.search(r"\bDROP\b", up):
                found.append(f"removes: {flat[:90]}")
            elif re.search(r"\bALTER COLUMN\b|\bMODIFY\b|\bALTER \w+ TYPE\b", up):
                found.append(f"changes a column: {flat[:90]}")
            elif re.search(r"\{\{ADDCOLUMN\}\}|\bADD\b", up) and "NOT NULL" in up and "DEFAULT" not in up:
                found.append(f"adds a NOT NULL column with no default: {flat[:90]}")
    return found


def modules(root):
    """Yields (path, line, migrations or None, compat) for each module declared as `StateModule Module { get; } = new(`."""
    for path in sorted(source_files(root)):
        with open(path, encoding="utf-8-sig") as f:
            text = f.read()
        for m in MODULE.finditer(text):
            line = text.count("\n", 0, m.start()) + 1
            migrations, after = raw_strings_of_list(text, m.end())
            yield path, line, migrations, module_compat(text, after)


def check(root, listing=None):
    problems = []
    for path, line, migrations, compat in modules(root):
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        where = f"{rel}:{line}"
        if migrations is None:
            if compat >= 1:
                problems.append(f"{where}: compatibleWithPrevious is {compat} but the migrations are not a literal list of raw strings, so they cannot be checked; write them as a list")
            continue
        for index, sql in enumerate(migrations, start=1):
            found = removals(sql)
            if listing is not None:
                for r in found:
                    listing.append(f"{where}: migration {index} of {len(migrations)} (compatibleWithPrevious {compat}) {r}")
            promised = compat >= 1 and index > len(migrations) - compat
            if promised and found and not CONTRACT.search(sql):
                for r in found:
                    problems.append(f"{where}: migration {index} is covered by compatibleWithPrevious = {compat} but {r}; "
                                    "mark the module 0, or name the release that stopped using it with a `-- Contract: <release> <what>` line in the migration")
    return problems


def main(argv):
    args = [a for a in argv[1:] if a != "--list"]
    root = os.path.abspath(args[0]) if args else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    listing = [] if "--list" in argv else None
    problems = check(root, listing)
    for line in listing or []:
        print("listed:", line)
    for p in problems:
        print(p)
    print(f"{len(problems)} problem(s)" if problems else "every migration a module promises to be compatible only adds, or names its contract")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
