import contextlib
import io
import os
import sys
import unittest

sys.path.insert(0, os.path.dirname(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import plan_status  # noqa: E402
from plan_repo import PlanRepo, git  # noqa: E402

ALL = ("ALPHABETA-1", "ALPHABETA-3", "ALPHABETA-4", "ALPHABETA-5", "ALPHABETA-6", "ALPHABETA-7", "ALPHABETA-2")


class PlanStatus(PlanRepo, unittest.TestCase):
    def run_main(self, *args):
        out, err = io.StringIO(), io.StringIO()
        with contextlib.redirect_stdout(out), contextlib.redirect_stderr(err):
            code = plan_status.main(["plan_status.py", *args, "--root", self.work])
        return code, out.getvalue(), err.getvalue()

    def status_of(self, *args):
        code, out, err = self.run_main("READY", *args)
        self.assertEqual(0, code, err)
        lines = [line.split() for line in out.splitlines() if line.lstrip()[:1].isdigit()]
        return {words[1]: " ".join(words[2:]) for words in lines}, out

    def test_each_task_shows_its_status_and_the_counts_follow(self):
        self.add_plan("READY", *ALL)
        status, out = self.status_of()
        self.assertTrue(status["ALPHABETA-1"].startswith("merged"), out)
        self.assertTrue(status["ALPHABETA-6"].startswith("merged"), out)  # named by a pull-request title
        self.assertTrue(status["ALPHABETA-3"].startswith("in progress (origin/backup/alphabeta-3)"), out)
        self.assertTrue(status["ALPHABETA-4"].startswith("blocked (by ALPHABETA-2)"), out)
        self.assertTrue(status["ALPHABETA-5"].startswith("not started"), out)
        self.assertTrue(status["ALPHABETA-2"].startswith("not started"), out)
        self.assertTrue(status["ALPHABETA-7"].startswith("not started"), out)  # a commit names it, but its row is still in the subject
        self.assertIn("READY: A person does the thing from start to end.", out)
        self.assertIn("merged 2, in progress 1, blocked 1, not started 3", out)

    def test_an_id_that_only_starts_like_a_merged_one_is_not_merged(self):
        self.assertFalse(plan_status.named_by("ALPHABETA-1", ["ALPHABETA-10: x", "ALPHABETA-1x"]))
        self.assertTrue(plan_status.named_by("ALPHABETA-1", ["ALPHABETA-1: x"]))
        self.assertTrue(plan_status.named_by("ALPHABETA-1", ["ALPHABETA-1 and ALPHABETA-2 (#3)"]))

    def test_remote_reads_the_branches_from_the_remote_not_the_local_refs(self):
        self.add_plan("READY", "ALPHABETA-5")
        git(self.work, "push", "origin", "main:refs/heads/backup/alphabeta-5")
        git(self.work, "update-ref", "-d", "refs/remotes/origin/backup/alphabeta-5")
        self.assertTrue(self.status_of()[0]["ALPHABETA-5"].startswith("not started"))
        self.assertTrue(self.status_of("--remote")[0]["ALPHABETA-5"].startswith("in progress"))

    def test_without_origin_main_it_says_so_and_shows_nothing_merged(self):
        self.add_plan("READY", "ALPHABETA-1", "ALPHABETA-2", push=False)
        git(self.work, "update-ref", "-d", "refs/remotes/origin/main")
        status, out = self.status_of()
        self.assertIn("origin/main is missing", out)
        self.assertTrue(status["ALPHABETA-1"].startswith("unknown"), out)
        self.assertIn("unknown 1", out)

    def test_a_plan_that_does_not_exist_or_has_another_shape_exits_2(self):
        code, _, err = self.run_main("NOPE")
        self.assertEqual(2, code)
        self.assertIn("planning/plans/NOPE.md does not exist", err)
        os.makedirs(os.path.join(self.work, "planning", "plans"), exist_ok=True)
        with open(os.path.join(self.work, "planning", "plans", "BAD.md"), "w", encoding="utf-8") as f:
            f.write("# BAD\n\nAn outcome.\n\n1. install it first\n")
        code, _, err = self.run_main("BAD")
        self.assertEqual(2, code)
        self.assertIn("a numbered line starts with a task id", err)

    def test_the_plan_shape_is_parsed_with_notes(self):
        name, outcome, tasks, problems = plan_status.parse_plan("# P\n\nOne sentence.\nTwo.\n\n1. A-1: first\n2. **B-2** - second\n10. C-3\n")
        self.assertEqual(("P", "One sentence. Two.", [("A-1", "first"), ("B-2", "second"), ("C-3", "")], []), (name, outcome, tasks, problems))
        self.assertIn("it lists A-1 twice", plan_status.parse_plan("# P\n\nx.\n\n1. A-1\n2. A-1\n")[3])


if __name__ == "__main__":
    unittest.main()
