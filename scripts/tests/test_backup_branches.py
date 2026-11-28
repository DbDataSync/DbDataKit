import contextlib
import io
import os
import subprocess
import sys
import tempfile
import time
import unittest
import unittest.mock

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

    def commit_as(self, name, message):
        with open(os.path.join(self.work, name + ".txt"), "w") as f:
            f.write(name + "\n")
        run(self.work, "add", name + ".txt")
        run(self.work, "commit", "-m", message)

    def test_task_ids_are_read_from_the_start_of_commit_messages(self):
        messages = ["INSTALLER-3: the start sequence", "INSTALLER-3: its test", "Merge branch 'main' into work/installer",
                    "ENDPOINTS-12: one port\n\nbody naming INSTALLER-9: not at the start", "see INSTALLER-4: not at the start", "Planning: x", "SEO4-1: a short slug", "bad-2: lower case"]
        self.assertEqual(backup_branches.task_ids(messages), ["INSTALLER-3", "ENDPOINTS-12", "SEO4-1"])
        self.assertEqual(backup_branches.task_branch("INSTALLER-3"), "backup/installer-3")

    def test_tasks_deletes_the_task_branches_a_squash_merged_work_pull_request_carried(self):
        base = run(self.work, "rev-parse", "main")
        # two workers' task branches, one task branch of another subject, and one task branch that went on after it was combined
        for branch, message in [("backup/installer-3", "INSTALLER-3: start sequence"), ("backup/installer-4", "INSTALLER-4: failure file"),
                                ("backup/installer-5", "INSTALLER-5: later work"), ("backup/endpoints-2", "ENDPOINTS-2: other subject")]:
            run(self.work, "checkout", "-b", branch, base)
            self.commit_as(branch.replace("/", "_"), message)
            run(self.work, "push", "origin", branch)
        # the reviewer combines installer-3, -4 and -5 into work/installer
        run(self.work, "checkout", "-b", "work/installer", base)
        for branch in ("backup/installer-3", "backup/installer-4", "backup/installer-5"):
            run(self.work, "merge", "--no-ff", "-m", f"Merge {branch} into work/installer", branch)
        head = run(self.work, "rev-parse", "HEAD")
        # the worker of installer-5 pushes more after the combine
        run(self.work, "checkout", "backup/installer-5")
        self.commit_as("five_more", "INSTALLER-5: more")
        run(self.work, "push", "origin", "backup/installer-5")
        # main takes the pull request as a squash: the task branches' tips are not contained in main
        run(self.work, "checkout", "main")
        run(self.work, "merge", "--squash", "work/installer")
        run(self.work, "commit", "-m", "INSTALLER: the start sequence (squashed)")
        run(self.work, "push", "origin", "main")
        run(self.work, "fetch", "origin", "+refs/heads/backup/*:refs/remotes/origin/backup/*")
        dry = dict(backup_branches.clean_tasks("origin", base, head, apply=False))
        self.assertTrue(dry["backup/installer-3"].startswith("would delete"), dry)
        self.assertTrue(dry["backup/installer-4"].startswith("would delete"), dry)
        self.assertTrue(dry["backup/installer-5"].startswith("kept"), dry)
        self.assertNotIn("backup/endpoints-2", dry)  # not named by the pull request
        self.assertIn("refs/heads/backup/installer-3", "\n".join(self.remote_branches()))  # the dry run deleted nothing
        done = dict(backup_branches.clean_tasks("origin", base, head, apply=True))
        self.assertEqual(done["backup/installer-3"], "deleted")
        self.assertEqual(done["backup/installer-4"], "deleted")
        heads = "\n".join(self.remote_branches())
        self.assertNotIn("backup/installer-3", heads)
        self.assertNotIn("backup/installer-4", heads)
        self.assertIn("backup/installer-5", heads)
        self.assertIn("backup/endpoints-2", heads)
        # the existing cleanup keeps what main has not caught up with: the squash left the tips out of main, and installer-5 has more
        run(self.work, "fetch", "--prune", "origin", "+refs/heads/backup/*:refs/remotes/origin/backup/*")
        rest = dict(backup_branches.clean("origin", 0, apply=False))
        self.assertTrue(rest["backup/installer-5"].startswith("kept"), rest)
        self.assertTrue(rest["backup/endpoints-2"].startswith("kept"), rest)


    def gone_on_the_remote(self, branch):
        """Push `branch`, fetch it, then delete it on the remote only: the clone still has its remote-tracking ref, as when another workflow deleted it first."""
        run(self.work, "push", "origin", branch)
        run(self.work, "fetch", "origin", "+refs/heads/backup/*:refs/remotes/origin/backup/*")
        run(self.remote, "branch", "-D", branch)

    def main_quietly(self, *argv):
        out = io.StringIO()
        with contextlib.redirect_stdout(out):
            code = backup_branches.main(list(argv))
        return code, out.getvalue()

    def test_clean_counts_a_branch_another_workflow_deleted_first_as_already_deleted(self):
        run(self.work, "branch", "backup/work/gone", "main")
        self.gone_on_the_remote("backup/work/gone")
        done = dict(backup_branches.clean("origin", 0, apply=True))
        self.assertEqual(done["backup/work/gone"], "already deleted")
        code, out = self.main_quietly("clean", "--apply")
        self.assertEqual(code, 0, out)
        self.assertIn("backup/work/gone: already deleted", out)

    def test_tasks_counts_a_task_branch_another_workflow_deleted_first_as_already_deleted(self):
        base = run(self.work, "rev-parse", "main")
        run(self.work, "checkout", "-b", "backup/installer-36", base)
        self.commit_as("installer_36", "INSTALLER-36: a task")
        head = run(self.work, "rev-parse", "HEAD")
        run(self.work, "checkout", "main")
        self.gone_on_the_remote("backup/installer-36")
        code, out = self.main_quietly("tasks", "--base", base, "--head", head, "--apply")
        self.assertEqual(code, 0, out)
        self.assertIn("backup/installer-36: already deleted", out)

    def test_any_other_delete_failure_still_fails(self):
        run(self.work, "branch", "backup/work/held", "main")
        run(self.work, "push", "origin", "backup/work/held")
        hook = os.path.join(self.remote, "hooks", "pre-receive")
        with open(hook, "w") as f:
            f.write("#!/bin/sh\necho 'refused by the test' >&2\nexit 1\n")
        os.chmod(hook, 0o755)
        code, out = self.main_quietly("clean", "--apply")
        self.assertEqual(code, 1, out)
        self.assertIn("backup/work/held: FAILED:", out)
        self.assertIn("refs/heads/backup/work/held", "\n".join(self.remote_branches()))

    def failing_delete(self, also_delete_on_remote):
        """A `git push --delete` that fails with an error the script has never seen; when asked, it first deletes the branch on the remote, as a
        workflow that won the race would. Every other git call runs for real, so the script's check of the remote is the real `git ls-remote`."""
        real_git = backup_branches.git
        message = "error: cannot lock ref 'refs/heads/{0}': unable to resolve reference 'refs/heads/{0}'"

        def git(*args, check=True):
            if args[:1] == ("push",) and "--delete" in args:
                name = args[-1]
                if also_delete_on_remote:
                    run(self.remote, "branch", "-D", name)
                return subprocess.CompletedProcess(["git", *args], 1, "", message.format(name) + "\n")
            return real_git(*args, check=check)

        return unittest.mock.patch.object(backup_branches, "git", git)

    def test_a_delete_that_fails_with_an_unknown_error_on_a_branch_that_is_gone_is_already_deleted(self):
        run(self.work, "branch", "backup/work/raced", "main")
        run(self.work, "push", "origin", "backup/work/raced")
        with self.failing_delete(also_delete_on_remote=True):
            self.assertEqual(backup_branches.delete_remote("origin", "backup/work/raced"), "already deleted")
        self.assertNotIn("backup/work/raced", "\n".join(self.remote_branches()))

    def test_a_delete_that_fails_with_an_unknown_error_on_a_branch_that_is_still_there_is_a_failure_naming_the_error(self):
        run(self.work, "branch", "backup/work/stays", "main")
        run(self.work, "push", "origin", "backup/work/stays")
        with self.failing_delete(also_delete_on_remote=False):
            status = backup_branches.delete_remote("origin", "backup/work/stays")
        self.assertEqual(status, "FAILED: error: cannot lock ref 'refs/heads/backup/work/stays': unable to resolve reference 'refs/heads/backup/work/stays'")
        self.assertIn("refs/heads/backup/work/stays", "\n".join(self.remote_branches()))

    def test_a_branch_whose_name_is_the_end_of_another_is_not_taken_for_it(self):
        # `git ls-remote --heads origin work/stays` would also list `backup/work/stays`; the full ref name matches only the branch itself.
        run(self.work, "branch", "backup/work/stays", "main")
        run(self.work, "push", "origin", "backup/work/stays")
        with self.failing_delete(also_delete_on_remote=False):
            self.assertEqual(backup_branches.delete_remote("origin", "work/stays"), "already deleted")

    def test_a_remote_that_cannot_be_asked_after_a_failed_delete_is_a_failure(self):
        run(self.work, "branch", "backup/work/unreachable", "main")
        run(self.work, "push", "origin", "backup/work/unreachable")
        run(self.work, "remote", "set-url", "origin", os.path.join(self.tmp.name, "no-such-remote.git"))
        with self.failing_delete(also_delete_on_remote=False):
            status = backup_branches.delete_remote("origin", "backup/work/unreachable")
        self.assertTrue(status.startswith("FAILED: error: cannot lock ref"), status)
        self.assertIn("asking origin whether the branch is still there failed", status)


if __name__ == "__main__":
    unittest.main()
