"""No test of the scripts may change the repository's own git configuration.

The name sorts last, so the check runs after every other module of `unittest discover -s scripts/tests`. The state is read when the module is
imported (discovery imports every module before the first test runs). Background: `core.bare` flipped to true in the shared `.git/config` when a
test ran `git init` under `git rebase -x` (see gitenv.py and docs/ci-lessons.md).
"""
import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from gitenv import clean_env, run_git  # noqa: E402

ROOT = Path(__file__).resolve().parent.parent.parent


def repository_config() -> str | None:
    """The local configuration of the repository the checkout belongs to (the shared one for a linked worktree); None outside a git repository."""
    done = subprocess.run(["git", "-C", str(ROOT), "rev-parse", "--git-common-dir"], capture_output=True, text=True, env=clean_env())
    if done.returncode != 0:
        return None
    common = Path(done.stdout.strip())
    common = common if common.is_absolute() else ROOT / common
    config = common / "config"
    return config.read_text(encoding="utf-8") if config.is_file() else None


BEFORE = repository_config()


class GitConfigUntouched(unittest.TestCase):
    def test_the_repository_configuration_is_what_it_was_before_the_tests(self):
        if BEFORE is None:
            self.skipTest("not a git repository")
        self.assertEqual(BEFORE, repository_config(), "a test changed the repository's .git/config (core.bare?): run its git commands through scripts/tests/gitenv.py")

    def test_git_init_in_a_folder_leaves_the_repository_in_GIT_DIR_alone(self):
        # what `git rebase -x` does to its children: GIT_DIR names the repository being worked on
        with tempfile.TemporaryDirectory() as tmp:
            repo, other = Path(tmp, "repo"), Path(tmp, "other")
            repo.mkdir()
            other.mkdir()
            run_git(repo, "init", "-q")
            poisoned = {"GIT_DIR": str(repo / ".git"), "GIT_WORK_TREE": str(repo), "GIT_INDEX_FILE": str(repo / ".git" / "index")}
            saved = {k: os.environ.get(k) for k in poisoned}
            os.environ.update(poisoned)
            try:
                run_git(other, "init", "-q")
                run_git(other, "config", "user.name", "t")
            finally:
                for k, v in saved.items():
                    if v is None:
                        os.environ.pop(k, None)
                    else:
                        os.environ[k] = v
            self.assertTrue((other / ".git").is_dir(), "git init made no repository in the folder it was given")
            self.assertEqual("false", run_git(repo, "config", "--local", "core.bare").strip())
            self.assertEqual("", run_git(repo, "config", "--local", "--get", "user.name", check=False).strip())


if __name__ == "__main__":
    unittest.main()
