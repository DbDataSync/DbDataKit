import os
import subprocess
import sys
import tempfile
import time
import unittest

SCRIPTS = os.path.join(os.path.dirname(__file__), "..")
sys.path.insert(0, SCRIPTS)
import backup_branches  # noqa: E402


def run(cwd, *args, env=None):
    full = dict(os.environ, GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@t", GIT_COMMITTER_NAME="t", GIT_COMMITTER_EMAIL="t@t")
    if env:
        full.update(env)
    result = subprocess.run(["git", *args], cwd=cwd, capture_output=True, text=True, env=full)
    assert result.returncode == 0, result.stderr
    return result.stdout.strip()


class BackupBranches(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        root = self.tmp.name
        self.remote = os.path.join(root, "remote.git")
        self.work = os.path.join(root, "work")
        run(root, "init", "--bare", "-b", "main", self.remote)
        run(root, "clone", self.remote, self.work)
        self.cwd0 = os.getcwd()
        os.chdir(self.work)
        run(self.work, "checkout", "-b", "main")
        self.commit("one")
        run(self.work, "push", "origin", "main")

    def tearDown(self):
        os.chdir(self.cwd0)
        self.tmp.cleanup()

    def commit(self, text, when=None):
        with open(os.path.join(self.work, text + ".txt"), "w") as f:
            f.write(text + "\n")
        run(self.work, "add", text + ".txt")
        env = {"GIT_COMMITTER_DATE": when, "GIT_AUTHOR_DATE": when} if when else None
        run(self.work, "commit", "-m", text, env=env)

    def remote_branches(self):
        return run(self.work, "ls-remote", "--heads", "origin").splitlines()

    def test_push_backs_up_pending_main_and_work_branches_only(self):
        run(self.work, "checkout", "-b", "work/a")
        self.commit("a1")
        run(self.work, "checkout", "main")
        self.commit("m2")  # local main is ahead of origin/main
        run(self.work, "branch", "work/merged", "origin/main")  # nothing of its own: on origin/main
        run(self.work, "checkout", "main")
        rows = backup_branches.push("origin")
        names = sorted(t for _, t, s in rows if s == "pushed")
        self.assertEqual(names, ["backup/main", "backup/work/a"])
        heads = "\n".join(self.remote_branches())
        self.assertIn("refs/heads/backup/main", heads)
        self.assertNotIn("backup/work/merged", heads)
        # a second run with new work fast-forwards, never forces
        self.commit("m3")
        rows = backup_branches.push("origin")
        self.assertTrue(all(s == "pushed" for _, _, s in rows), rows)

    def test_clean_deletes_only_old_backups_main_has_caught_up_with(self):
        run(self.work, "checkout", "-b", "work/old")
        self.commit("old1", when="2020-01-01T00:00:00")
        run(self.work, "push", "origin", "work/old:refs/heads/backup/work/old")
        run(self.work, "checkout", "-b", "work/pending")
        self.commit("pending1", when="2020-01-01T00:00:00")
        run(self.work, "push", "origin", "work/pending:refs/heads/backup/work/pending")
        run(self.work, "checkout", "-b", "work/fresh", "main")
        self.commit("fresh1")
        run(self.work, "push", "origin", "work/fresh:refs/heads/backup/work/fresh")
        # main takes old and fresh, not pending
        run(self.work, "checkout", "main")
        run(self.work, "merge", "--no-ff", "-m", "merge old", "work/old")
        run(self.work, "merge", "--no-ff", "-m", "merge fresh", "work/fresh")
        run(self.work, "push", "origin", "main")
        run(self.work, "fetch", "origin", "+refs/heads/backup/*:refs/remotes/origin/backup/*")
        dry = dict(backup_branches.clean("origin", 24, apply=False))
        self.assertTrue(dry["backup/work/old"].startswith("would delete"))
        self.assertTrue(dry["backup/work/pending"].startswith("kept: not on main"))
        self.assertTrue(dry["backup/work/fresh"].startswith("kept: on main but touched"))
        self.assertEqual(len(self.remote_branches()), 4)  # main + 3 backups: nothing deleted by the dry run
        done = dict(backup_branches.clean("origin", 24, apply=True))
        self.assertEqual(done["backup/work/old"], "deleted")
        heads = "\n".join(self.remote_branches())
        self.assertNotIn("backup/work/old", heads)
        self.assertIn("backup/work/pending", heads)
        self.assertIn("backup/work/fresh", heads)


    def test_a_rewritten_branch_goes_to_the_next_name_and_the_old_backup_is_cleaned_by_content(self):
        run(self.work, "checkout", "-b", "work/sq")
        self.commit("wip1")
        self.commit("wip2")
        backup_branches.push("origin")  # backup/work/sq = two wip commits
        # squash the two commits into one (a rewrite)
        run(self.work, "reset", "--soft", "main")
        run(self.work, "commit", "-m", "squashed")
        rows = backup_branches.push("origin")
        moved = [r for r in rows if r[0] == "work/sq"][0]
        self.assertEqual(moved[1], "backup/work/sq-2")
        self.assertIn("rewritten", moved[2])
        rows = backup_branches.push("origin")  # again: fast-forward of -2 (no new commit: nothing to say, but no failure)
        self.assertFalse(any("FAILED" in s for _, _, s in rows), rows)
        # main takes the squashed work by cherry-pick (a different history)
        run(self.work, "checkout", "main")
        run(self.work, "cherry-pick", "work/sq")
        run(self.work, "push", "origin", "main")
        run(self.work, "fetch", "origin", "+refs/heads/backup/*:refs/remotes/origin/backup/*")
        done = dict(backup_branches.clean("origin", 0, apply=True))
        self.assertEqual(done["backup/work/sq"], "deleted")  # the old two-commit backup: same content as main
        self.assertEqual(done["backup/work/sq-2"], "deleted")


if __name__ == "__main__":
    unittest.main()
