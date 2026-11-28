"""A temporary git repository with a planning folder and a remote, for the tests of plan_status.py and the plan checks of check_plans.py.

The remote's main has these commits: `ALPHABETA-1: ...` (its row has left the subject: merged) and `ALPHABETA-6 and ALPHABETA-7 (#12)` (a pull-request
title; ALPHABETA-6 has left the subject, ALPHABETA-7 still has a row: done in part). The subject ALPHABETA keeps the rows 2 to 5 and 7: ALPHABETA-3 has
a pushed branch backup/alphabeta-3, ALPHABETA-4 depends on ALPHABETA-2 (not merged), ALPHABETA-5 depends on nothing.
"""
import os
import tempfile

from gitenv import run_git


def git(root, *args):
    return run_git(root, "-c", "user.name=t", "-c", "user.email=t@t", "-c", "commit.gpgsign=false", *args)


def write(root, rel, text):
    path = os.path.join(root, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


def subject(slug, tasks):
    rows = "".join(f"| {slug}-{n} | what | {dep} | S | a test |\n" for n, dep in tasks)
    return (f"# {slug}\n\n## Goal\n\nx\n\n## Tasks\n\n| Task | What | Depends on | Size | Done when |\n| --- | --- | --- | --- | --- |\n{rows}\n"
            "## Needs from other subjects\n\n\n## Proposed decisions\n\n")


def plan(name, *ids):
    return f"# {name}\n\nA person does the thing from start to end.\n\n" + "".join(f"{i}. {ident}\n" for i, ident in enumerate(ids, 1))


ROWS = [(2, "none"), (3, "none"), (4, "ALPHABETA-2"), (5, "none"), (7, "none")]


class PlanRepo:
    """setUp makes self.remote (bare) and self.work (its clone, on main, pushed); tearDown removes both."""

    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        base = self.tmp.name
        self.remote = os.path.join(base, "remote.git")
        self.work = os.path.join(base, "work")
        run_git(base, "init", "--bare", "-b", "main", self.remote)
        run_git(base, "clone", self.remote, self.work)
        git(self.work, "checkout", "-b", "main")
        write(self.work, "planning/README.md",
              "# Planning\n\n| # | Subject | Goal | Status | Assigned |\n| --- | --- | --- | --- | --- |\n| 1 | [ALPHABETA](ALPHABETA.md) | g | queued | unassigned |\n")
        write(self.work, "planning/ROLES.md", "# Roles\n")
        write(self.work, "planning/ALPHABETA.md", subject("ALPHABETA", [(1, "none"), (6, "none")] + ROWS))
        self.commit("Planning: ALPHABETA")
        write(self.work, "planning/ALPHABETA.md", subject("ALPHABETA", ROWS))
        self.commit("ALPHABETA-1: the first part")
        self.commit("ALPHABETA-6 and ALPHABETA-7 (#12)", allow_empty=True)
        self.commit("ALPHABETA-10: a task whose id starts like another's", allow_empty=True)
        git(self.work, "push", "origin", "main")
        git(self.work, "push", "origin", "main:refs/heads/backup/alphabeta-3")

    def tearDown(self):
        self.tmp.cleanup()

    def commit(self, message, allow_empty=False):
        git(self.work, "add", "-A")
        git(self.work, "commit", "-q", "-m", message, *(["--allow-empty"] if allow_empty else []))

    def add_plan(self, name, *ids, push=True):
        write(self.work, f"planning/plans/{name}.md", plan(name, *ids))
        self.commit(f"Planning: the plan {name}")
        if push:
            git(self.work, "push", "origin", "main")
