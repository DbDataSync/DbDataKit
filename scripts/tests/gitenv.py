"""Git for the tests of the scripts, without the environment a `git rebase -x`, a hook or a worktree command hands its children.

`git rebase -x <command>` (and git hooks) run the command with GIT_DIR (and GIT_WORK_TREE, GIT_INDEX_FILE and the like) set to the repository being
worked on. A test that runs `git init` in a temporary folder then does not make a repository there: `git init` reinitialises the repository GIT_DIR
names, and with no GIT_WORK_TREE it writes `core.bare = true` into it. For a linked worktree that is the shared `.git/config` of the main repository,
and every work-tree command of every worktree then fails with "this operation must be run in a work tree". Every git a test starts goes through
`run_git` or `clean_env`; `test_zz_git_config_untouched.py` fails when anything changes the repository's `core.bare`.
"""
import os
import subprocess
from pathlib import Path


def clean_env(extra: dict[str, str] | None = None) -> dict[str, str]:
    """The environment without any GIT_* variable that points git at a repository (the ones that configure it, such as GIT_CONFIG_*, GIT_SSL_*, stay)."""
    keep = ("GIT_CONFIG_", "GIT_SSL_", "GIT_ASKPASS", "GIT_TERMINAL_PROMPT", "GIT_EDITOR")
    env = {k: v for k, v in os.environ.items() if not k.startswith("GIT_") or k.startswith(keep)}
    if extra:
        env.update(extra)
    return env


def run_git(root: Path, *args: str, check: bool = True) -> str:
    """`git -C root args` in a clean environment; the standard output."""
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=check, env=clean_env()).stdout
