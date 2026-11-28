#!/usr/bin/env python3
"""Print a plan with the status of each of its tasks (planning/ROLES.md, "Plans").

Usage, from anywhere:

  python3 -I scripts/plan_status.py <NAME>            the plan planning/plans/<NAME>.md, from the local refs (offline)
  python3 -I scripts/plan_status.py <NAME> --remote   the same, but the in-progress branches are read from the remote (git ls-remote origin)
  python3 -I scripts/plan_status.py <NAME> --root <folder>   a checkout other than the one this script is in

The shape of a plan file, the only one read:

  # SYNCREADY

  A person installs the suite on Linux, authors a Sync project in the Designer, deploys it and runs it, with no step done by hand.

  1. INSTALLER-21
  2. INSTALLER-7: a short note, if one helps
  3. SYNCAUTHORING-6

The heading is the plan's name. The outcome is the text between the heading and the list, one or two sentences. The tasks are a numbered list, in
order, one task per line: the line starts with the number, a dot, then the task id (`<SLUG>-<n>`, of any subject); anything after the id is a note
and is printed as it is. A numbered line that does not start with a task id is an error. A plan holds no statuses, no task rows and no proposed
decisions: the tasks stay in their subjects.

The status of each task, the first that applies:

  merged       the id is in no subject's task row, and a commit on origin/main has a subject that starts with `<ID>:` (a task commit) or `<ID> `
               (a merge or pull-request title that names it)
  in progress  a branch backup/<slug in lower case>-<n> exists on origin (`git branch -r`, or `git ls-remote` with --remote)
  blocked      a task in its "Depends on" cell is not merged
  not started  none of the above
  unknown      the id is in no subject and no commit on origin/main names it (moved, renamed, or origin/main is missing or not fetched)

Then a count per status. Run `git fetch origin` first for a current answer; without it the script answers from the refs it has and says when
origin/main is missing. Exit code: 0 when the plan was printed, 2 when it does not exist or does not have the shape above.
"""
import os
import re
import subprocess
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import check_plans  # noqa: E402

PLANS_DIR = os.path.join("planning", "plans")
TASK_ID = re.compile(r"^[A-Z][A-Z0-9]*-\d+$")
NUMBERED = re.compile(r"^\s*\d+\.\s+(.*)$")
LEAD_ID = re.compile(r"^\**([A-Z][A-Z0-9]*-\d+)\**(?![\w-])\s*(.*)$")
STATUSES = ("merged", "in progress", "blocked", "not started", "unknown")
# Variables that point git at another repository than the one asked for (a hook or `git rebase -x` sets them); the script names its repository itself.
GIT_LOCATION = ("GIT_DIR", "GIT_WORK_TREE", "GIT_INDEX_FILE", "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES", "GIT_COMMON_DIR",
                "GIT_NAMESPACE", "GIT_PREFIX")


def parse_plan(text):
    """(name, outcome, [(id, note)], [problems]) of a plan's text."""
    name, outcome, tasks, out = None, [], [], []
    in_list = False
    for number, line in enumerate(text.splitlines(), 1):
        stripped = line.strip()
        if name is None and stripped.startswith("# "):
            name = stripped[2:].strip()
            continue
        item = NUMBERED.match(line)
        if item:
            in_list = True
            lead = LEAD_ID.match(item.group(1).strip())
            if not lead:
                out.append(f"line {number}: a numbered line starts with a task id (`1. SLUG-3`), not '{item.group(1).strip()}'")
                continue
            note = lead.group(2).lstrip(":-–— ").strip()
            tasks.append((lead.group(1), note))
        elif stripped and not in_list and name is not None:
            outcome.append(stripped)
    if name is None:
        out.append("it has no heading `# <NAME>`")
    if not outcome:
        out.append("it has no outcome (a sentence or two between the heading and the list)")
    if not tasks:
        out.append("it lists no task (a numbered list of task ids)")
    seen = set()
    for ident, _ in tasks:
        if ident in seen:
            out.append(f"it lists {ident} twice")
        seen.add(ident)
    return name, " ".join(outcome), tasks, out


def git(root, *args):
    """(exit code, standard output) of `git -C root args`, with no variable pointing git at another repository."""
    env = {k: v for k, v in os.environ.items() if k not in GIT_LOCATION}
    try:
        result = subprocess.run(["git", "-C", root, *args], capture_output=True, text=True, env=env)
    except OSError as error:
        return 127, str(error)
    return result.returncode, result.stdout


def commit_subjects(root, ref):
    """The subjects of the commits reachable from ref, or None when ref does not exist (or root is not a git repository)."""
    code, _ = git(root, "rev-parse", "--verify", "--quiet", ref + "^{commit}")
    if code != 0:
        return None
    code, out = git(root, "log", ref, "--format=%s")
    return out.splitlines() if code == 0 else None


def is_shallow(root):
    code, out = git(root, "rev-parse", "--is-shallow-repository")
    return code == 0 and out.strip() == "true"


def named_by(ident, subjects):
    """Whether a commit subject starts with `<ID>:` or `<ID> `."""
    return any(s.startswith(ident + ":") or s.startswith(ident + " ") for s in subjects)


def remote_branches(root, remote, use_ls_remote):
    """The names (without the remote) of the remote's branches: from the local refs, or asked of the remote with use_ls_remote."""
    if use_ls_remote:
        code, out = git(root, "ls-remote", "--heads", remote)
        if code != 0:
            raise RuntimeError(f"git ls-remote --heads {remote} failed (exit code {code})")
        return {line.split("\t", 1)[1][len("refs/heads/"):] for line in out.splitlines() if "\trefs/heads/" in line}
    code, out = git(root, "for-each-ref", "--format=%(refname)", f"refs/remotes/{remote}/")
    prefix = f"refs/remotes/{remote}/"
    return {line[len(prefix):] for line in out.splitlines() if line.startswith(prefix)} if code == 0 else set()


def subject_tasks(root):
    """{task id: (subject file, [ids in its "Depends on" cell])} of every subject's task rows."""
    planning = os.path.join(root, "planning")
    return check_plans.task_dependencies(planning, check_plans.subject_files(planning))


def statuses(root, tasks, remote="origin", use_ls_remote=False):
    """([(id, note, status, detail)], main_found) for the plan's tasks."""
    rows = subject_tasks(root)
    main = commit_subjects(root, f"refs/remotes/{remote}/main")
    branches = remote_branches(root, remote, use_ls_remote)
    merged_cache = {}

    def merged(ident):
        if ident not in merged_cache:
            merged_cache[ident] = ident not in rows and main is not None and named_by(ident, main)
        return merged_cache[ident]

    out = []
    for ident, note in tasks:
        slug, n = ident.rsplit("-", 1)
        branch = f"backup/{slug.lower()}-{n}"
        if merged(ident):
            out.append((ident, note, "merged", ""))
        elif branch in branches:
            out.append((ident, note, "in progress", f"{remote}/{branch}"))
        elif ident not in rows:
            out.append((ident, note, "unknown", "in no subject, and no commit on main starts with it"))
        else:
            waiting = [d for d in rows[ident][1] if not merged(d)]
            if waiting:
                out.append((ident, note, "blocked", "by " + ", ".join(waiting)))
            else:
                out.append((ident, note, "not started", ""))
    return out, main is not None


def main(argv):
    args = argv[1:]
    use_ls_remote = "--remote" in args
    args = [a for a in args if a != "--remote"]
    root = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    if "--root" in args:
        i = args.index("--root")
        if i + 1 >= len(args):
            print("--root needs a folder", file=sys.stderr)
            return 2
        root = os.path.abspath(args[i + 1])
        del args[i:i + 2]
    if len(args) != 1 or args[0].startswith("-"):
        print("usage: python3 -I scripts/plan_status.py <NAME> [--remote] [--root <folder>]", file=sys.stderr)
        return 2
    name = args[0]
    path = os.path.join(root, PLANS_DIR, name + ".md")
    if not os.path.isfile(path):
        known = sorted(n[:-3] for n in os.listdir(os.path.join(root, PLANS_DIR)) if n.endswith(".md")) if os.path.isdir(os.path.join(root, PLANS_DIR)) else []
        print(f"planning/plans/{name}.md does not exist" + (f"; the plans are {', '.join(known)}" if known else "; there are no plans"), file=sys.stderr)
        return 2
    with open(path, encoding="utf-8") as f:
        title, outcome, tasks, problems = parse_plan(f.read())
    if problems:
        for problem in problems:
            print(f"planning/plans/{name}.md: {problem}", file=sys.stderr)
        return 2
    try:
        rows, main_found = statuses(root, tasks, use_ls_remote=use_ls_remote)
    except RuntimeError as error:
        print(str(error), file=sys.stderr)
        return 2
    print(f"{title}: {outcome}")
    if not main_found:
        print("origin/main is missing (run `git fetch origin main`): no task can show as merged")
    width = max(len(ident) for ident, *_ in rows)
    for i, (ident, note, status, detail) in enumerate(rows, 1):
        extra = "  ".join(x for x in (f"({detail})" if detail else "", note) if x)
        print(f"{i:>3}. {ident:<{width}}  {status:<11}  {extra}".rstrip())
    counts = {s: sum(1 for r in rows if r[2] == s) for s in STATUSES}
    print(", ".join(f"{s} {counts[s]}" for s in STATUSES if counts[s] or s != "unknown"))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
