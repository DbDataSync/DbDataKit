#!/usr/bin/env python3
"""The planning folder stays consistent (planning/ROLES.md). Checks:

  index      planning/README.md links every subject file (planning/<SLUG>.md other than README.md, ROLES.md and PARKED.md), and every subject it
             links exists (it carries no task counts, so a subject PR never has to touch it)
  others     README.md, ROLES.md and PARKED.md (the parked work) are not subjects: they need no index row, may be linked from anywhere, and hold no
             task row or proposed-decision heading of their own; an id they name is checked like any other (refs)
  slug       a subject file's name is its slug: capitals and digits, starting with a letter, at most 12 characters (the longer slugs in
             LONG_SLUGS were named before the limit and stay)
  tasks      a task is a table row whose first cell is `<SLUG>-<n>`; every task id in a subject file starts with that file's slug, and no id is
             used twice anywhere
  proposals  a proposed decision is a heading that starts with `<SLUG>-P<n>`; it starts with its file's slug and is unique
  refs       every task or proposal id named anywhere under planning/ (a dependency, a need from another subject, a hand-off) exists
  follows    a task named after "Follows" in a row's text is also in the row's "Depends on" cell
  cycles     no tasks depend on each other in a cycle (the "Depends on" cell of each task row); a task that depends on a task of a subject ranked
             lower in the index is printed as a report after the result, not counted as a problem (the order may be meant)
  links      no markdown file in the repository names `docs/plans/` or links to a file under planning/ that does not exist

Work-in-progress notes under planning/wip/ are not read here; scripts/check_no_wip.py refuses them on main.
Run from anywhere: python3 -I scripts/check_plans.py [root]
"""
import os
import re
import sys

SLUG = re.compile(r"^[A-Z][A-Z0-9]*$")
SLUG_MAX = 12
# Subjects named before the 12-character limit; they keep their names. Add a slug here only with the owner's word, never remove another's.
LONG_SLUGS = {
    "BACKUPRESTORE",
    "FABRICSERVING",
    "REPORTCATALOGS",
    "SUBSCRIPTIONS",
    "SYNCAUTHORING",
    "WORKSPACEISOLATION",
}
# A word of capitals and digits, a hyphen and a number reads as a task id (`P` before the number: a proposed decision) only when its prefix is the
# slug of a subject or is 8 characters or longer (`is_id_prefix`), so that `UTF-8`, `SHA-256` or `N-1` are not taken for ids of a short slug.
ID = re.compile(r"\b([A-Z][A-Z0-9]*)-(P?)(\d+)\b")
TASK_ROW = re.compile(r"^\|\s*\**([A-Z][A-Z0-9]*)-(\d+)\**\s*\|", re.M)
PROPOSAL_HEADING = re.compile(r"^#{2,6}\s+([A-Z][A-Z0-9]*)-P(\d+)\b", re.M)
ID_PREFIX_MIN = 8
INDEX_LINK = re.compile(r"\[[^\]]*\]\(([^)\s#]+\.md)\)")
MD_LINK = re.compile(r"\]\(([^)\s#]+\.md)(?:#[^)]*)?\)")
NOT_SUBJECTS = {"README.md", "ROLES.md", "PARKED.md"}
SKIP_DIRS = {".git", "node_modules", "bin", "obj", ".claude", "wip"}


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def is_id_prefix(prefix, slugs):
    return prefix in slugs or len(prefix) >= ID_PREFIX_MIN


def slug_problem(slug):
    if not SLUG.match(slug):
        return f"the slug '{slug}' must be capitals and digits, starting with a letter"
    if len(slug) > SLUG_MAX and slug not in LONG_SLUGS:
        return f"the slug '{slug}' has {len(slug)} characters; a slug has at most {SLUG_MAX}"
    return None


def subject_files(planning):
    return sorted(n for n in os.listdir(planning) if n.endswith(".md") and n not in NOT_SUBJECTS and os.path.isfile(os.path.join(planning, n)))


def markdown_files(root):
    for folder, dirs, files in os.walk(root):
        dirs[:] = [d for d in dirs if d not in SKIP_DIRS]
        for name in files:
            if name.endswith(".md"):
                yield os.path.join(folder, name)


def problems(root):
    out = []
    planning = os.path.join(root, "planning")
    if not os.path.isdir(planning):
        return ["planning/ does not exist"]
    readme_path = os.path.join(planning, "README.md")
    if not os.path.exists(readme_path):
        return ["planning/README.md does not exist"]
    subjects = subject_files(planning)
    slugs = {name[:-3] for name in subjects}
    tasks, proposals = {}, {}

    for name in subjects:
        slug = name[:-3]
        problem = slug_problem(slug)
        if problem:
            out.append(f"planning/{name}: {problem}")
        text = read(os.path.join(planning, name))
        for prefix, number in TASK_ROW.findall(text):
            if not is_id_prefix(prefix, slugs):
                continue
            ident = f"{prefix}-{number}"
            if prefix != slug:
                out.append(f"planning/{name}: task {ident} does not start with the slug {slug}")
            if ident in tasks:
                out.append(f"planning/{name}: task {ident} is also in planning/{tasks[ident]}")
            tasks[ident] = name
        for prefix, number in PROPOSAL_HEADING.findall(text):
            if not is_id_prefix(prefix, slugs):
                continue
            ident = f"{prefix}-P{number}"
            if prefix != slug:
                out.append(f"planning/{name}: proposed decision {ident} does not start with the slug {slug}")
            if ident in proposals:
                out.append(f"planning/{name}: proposed decision {ident} is also in planning/{proposals[ident]}")
            proposals[ident] = name

    for name in sorted(NOT_SUBJECTS):
        path = os.path.join(planning, name)
        if not os.path.isfile(path):
            continue
        text = read(path)
        for prefix, number in TASK_ROW.findall(text):
            out.append(f"planning/{name}: has the task row {prefix}-{number}; tasks are rows of a subject file only")
        for prefix, number in PROPOSAL_HEADING.findall(text):
            out.append(f"planning/{name}: has the proposed decision {prefix}-P{number}; proposed decisions are in a subject file only")

    readme = read(readme_path)
    listed = set()
    for line in readme.splitlines():
        for target in INDEX_LINK.findall(line):
            base = os.path.basename(target)
            if base in NOT_SUBJECTS or "/" in target.replace("./", ""):
                continue
            listed.add(base)
            if not os.path.exists(os.path.join(planning, base)):
                out.append(f"planning/README.md: links {base}, which does not exist")
                continue
    for name in subjects:
        if name not in listed:
            out.append(f"planning/README.md: does not list the subject planning/{name}")

    for name in sorted(n for n in os.listdir(planning) if n.endswith(".md")):
        text = read(os.path.join(planning, name))
        for prefix, p, number in ID.findall(text):
            if not is_id_prefix(prefix, slugs):
                continue
            ident = f"{prefix}-{p}{number}"
            known = proposals if p else tasks
            if ident not in known:
                out.append(f"planning/{name}: names {ident}, which is not a {'proposed decision' if p else 'task'} of any subject")

    deps = task_dependencies(planning, subjects)
    out.extend(cycle_problems(deps))
    out.extend(follows_problems(planning, subjects, deps))

    for path in markdown_files(root):
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        text = read(path)
        if "docs/plans/" in text or re.search(r"\]\((?:\.\./)*plans/", text):
            out.append(f"{rel}: names docs/plans/, which is gone (the open work is in planning/)")
        for target in MD_LINK.findall(text):
            if "://" in target:
                continue
            resolved = os.path.normpath(os.path.join(os.path.dirname(path), target))
            if os.path.relpath(resolved, root).replace(os.sep, "/").startswith("planning/") and not os.path.exists(resolved):
                out.append(f"{rel}: links {target}, which does not exist")
    return sorted(set(out))


def task_dependencies(planning, subjects):
    """{task id: (subject file, [task ids in its "Depends on" cell])}, from the third cell of each task row."""
    deps = {}
    slugs = {name[:-3] for name in subjects}
    for name in subjects:
        for line in read(os.path.join(planning, name)).splitlines():
            row = TASK_ROW.match(line + "\n")
            if not row or not is_id_prefix(row.group(1), slugs):
                continue
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cells) < 3:
                continue
            ident = cells[0].strip("*")
            deps[ident] = (name, [f"{a}-{n}" for a, p, n in ID.findall(cells[2]) if not p and is_id_prefix(a, slugs)])
    return deps


FOLLOWS = re.compile(r"\bFollows\b([^(.;:]*)")


def follows_problems(planning, subjects, deps):
    """A problem for each task named after "Follows" in a row's text that is not in the row's "Depends on" cell."""
    out = []
    slugs = {name[:-3] for name in subjects}
    for name in subjects:
        for line in read(os.path.join(planning, name)).splitlines():
            row = TASK_ROW.match(line + "\n")
            if not row or not is_id_prefix(row.group(1), slugs):
                continue
            cells = [c.strip() for c in line.strip().strip("|").split("|")]
            if len(cells) < 3:
                continue
            ident = cells[0].strip("*")
            for m in FOLLOWS.finditer(cells[1]):
                for a, p, n in ID.findall(m.group(1)):
                    named = f"{a}-{n}"
                    if not p and is_id_prefix(a, slugs) and named not in deps.get(ident, ("", []))[1]:
                        out.append(f"planning/{name}: {ident} follows {named} in its text but does not list it under Depends on")
    return out


def cycle_problems(deps):
    """A problem for each cycle of tasks that depend on each other, named once from its smallest id."""
    out, seen = [], set()

    def walk(node, path):
        if node in path:
            cycle = path[path.index(node):]
            start = cycle.index(min(cycle))
            key = tuple(cycle[start:] + cycle[:start])
            if key not in seen:
                seen.add(key)
                out.append(f"planning/{deps[key[0]][0]}: the tasks {' -> '.join(key + (key[0],))} depend on each other in a cycle")
            return
        for dep in deps.get(node, ("", []))[1]:
            if dep in deps:
                walk(dep, path + [node])

    for ident in sorted(deps):
        walk(ident, [])
    return out


def priority_report(root):
    """Each dependency of a task on a task of a subject ranked lower in planning/README.md. A report, not a problem: the order may be meant."""
    planning = os.path.join(root, "planning")
    rank = {}
    for line in read(os.path.join(planning, "README.md")).splitlines():
        m = re.match(r"\|\s*(\d+)\s*\|\s*\[([A-Z0-9]+)\]", line)
        if m:
            rank[m.group(2)] = int(m.group(1))
    deps = task_dependencies(planning, subject_files(planning))
    out = []
    for ident, (name, needs) in sorted(deps.items(), key=lambda kv: (rank.get(kv[1][0][:-3], 999), kv[0])):
        mine = name[:-3]
        for dep in needs:
            theirs = dep.rsplit("-", 1)[0]
            if mine in rank and theirs in rank and rank[theirs] > rank[mine]:
                out.append(f"{ident} (rank {rank[mine]}) depends on {dep} (rank {rank[theirs]})")
    return out


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    found = problems(root)
    for line in found:
        print(line)
    print(f"{len(found)} problem(s)" if found else "the planning folder is consistent")
    if not found:
        report = priority_report(root)
        if report:
            print(f"{len(report)} dependencies on a subject ranked lower in planning/README.md (a report, not a problem):")
            for line in report:
                print(f"  {line}")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
