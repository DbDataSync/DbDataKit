#!/usr/bin/env python3
"""The planning folder stays consistent (planning/ROLES.md). Checks:

  index      planning/README.md links every subject file (planning/<SLUG>.md other than README.md and ROLES.md), and every subject it links exists
             (it carries no task counts, so a subject PR never has to touch it)
  slug       a subject file's name is its slug: capitals and digits, at least 8 characters, starting with a letter
  tasks      a task is a table row whose first cell is `<SLUG>-<n>`; every task id in a subject file starts with that file's slug, and no id is
             used twice anywhere
  proposals  a proposed decision is a heading that starts with `<SLUG>-P<n>`; it starts with its file's slug and is unique
  refs       every task or proposal id named anywhere under planning/ (a dependency, a need from another subject, a hand-off) exists
  links      no markdown file in the repository names `docs/plans/` or links to a file under planning/ that does not exist

Work-in-progress notes under planning/wip/ are not read here; scripts/check_no_wip.py refuses them on main.
Run from anywhere: python3 -I scripts/check_plans.py [root]
"""
import os
import re
import sys

SLUG = re.compile(r"^[A-Z][A-Z0-9]{7,}$")
ID = re.compile(r"\b([A-Z][A-Z0-9]{7,})-(P?)(\d+)\b")
TASK_ROW = re.compile(r"^\|\s*\**([A-Z][A-Z0-9]{7,})-(\d+)\**\s*\|", re.M)
PROPOSAL_HEADING = re.compile(r"^#{2,6}\s+([A-Z][A-Z0-9]{7,})-P(\d+)\b", re.M)
INDEX_LINK = re.compile(r"\[[^\]]*\]\(([^)\s#]+\.md)\)")
MD_LINK = re.compile(r"\]\(([^)\s#]+\.md)(?:#[^)]*)?\)")
NOT_SUBJECTS = {"README.md", "ROLES.md"}
SKIP_DIRS = {".git", "node_modules", "bin", "obj", ".claude", "wip"}


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


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
    tasks, proposals = {}, {}

    for name in subjects:
        slug = name[:-3]
        if not SLUG.match(slug):
            out.append(f"planning/{name}: the slug '{slug}' must be capitals and digits, at least 8 characters, starting with a letter")
        text = read(os.path.join(planning, name))
        for prefix, number in TASK_ROW.findall(text):
            ident = f"{prefix}-{number}"
            if prefix != slug:
                out.append(f"planning/{name}: task {ident} does not start with the slug {slug}")
            if ident in tasks:
                out.append(f"planning/{name}: task {ident} is also in planning/{tasks[ident]}")
            tasks[ident] = name
        for prefix, number in PROPOSAL_HEADING.findall(text):
            ident = f"{prefix}-P{number}"
            if prefix != slug:
                out.append(f"planning/{name}: proposed decision {ident} does not start with the slug {slug}")
            if ident in proposals:
                out.append(f"planning/{name}: proposed decision {ident} is also in planning/{proposals[ident]}")
            proposals[ident] = name

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
            ident = f"{prefix}-{p}{number}"
            known = proposals if p else tasks
            if ident not in known:
                out.append(f"planning/{name}: names {ident}, which is not a {'proposed decision' if p else 'task'} of any subject")

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


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    found = problems(root)
    for line in found:
        print(line)
    print(f"{len(found)} problem(s)" if found else "the planning folder is consistent")
    return 1 if found else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
