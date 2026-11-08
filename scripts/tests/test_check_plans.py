import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_plans as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


def readme(*rows, extra=""):
    return "# Plans\n\n| # | Plan |\n|---|---|\n" + "".join(f"| {n} | [{n}]({f}) |\n" for n, f in rows) + extra


GOOD = {
    "docs/plans/01-a.md": "# A\n\nStatus: done.\n",
    "docs/plans/02-b.md": "# B\n\nStatus: proposed. See [A](01-a.md).\n",
    "docs/plans/README.md": readme(("01", "01-a.md"), ("02", "02-b.md")),
}


def table(*statuses):
    return "| Id | Slice | Done when |\n|---|---|---|\n" + "".join(f"| R{i + 1:02d} | slice | test {s} |\n" for i, s in enumerate(statuses))


class CheckPlans(unittest.TestCase):
    def problems(self, files, baseline=None):
        files = dict(files)
        if baseline is not None:
            files["scripts/plan_check_baseline.txt"] = baseline
        return check.check(tree(files))

    def test_a_consistent_tree_passes(self):
        self.assertEqual(([], []), self.problems(GOOD))

    def test_a_link_to_a_missing_plan_is_found(self):
        files = dict(GOOD, **{"docs/plans/02-b.md": "# B\n\nStatus: proposed. See [C](03-c.md#x).\n"})
        problems, _ = self.problems(files)
        self.assertEqual(1, len(problems))
        self.assertIn("03-c.md", problems[0])

    def test_links_to_other_files_are_not_checked(self):
        files = dict(GOOD, **{"docs/plans/02-b.md": "# B\n\nStatus: proposed. [x](../ui-coverage.md) [y](https://e.com/z-1.md)\n"})
        self.assertEqual(([], []), self.problems(files))

    def test_a_plan_number_without_a_file_in_the_readme_is_found(self):
        files = dict(GOOD, **{"docs/plans/README.md": readme(("01", "01-a.md"), ("02", "02-b.md"), extra="See plans 01 to 07 and plan 2.\n")})
        problems, _ = self.problems(files)
        self.assertEqual(1, len(problems))
        self.assertIn("plan 07", problems[0])

    def test_a_list_of_plans_names_each(self):
        files = dict(GOOD, **{"docs/plans/README.md": readme(("01", "01-a.md"), ("02", "02-b.md"), extra="plans 1, 2 and 9\n")})
        problems, _ = self.problems(files)
        self.assertEqual(1, len(problems))
        self.assertIn("plan 9", problems[0])

    def test_a_finished_plans_pointer_row_keeps_its_number_known(self):
        files = dict(GOOD, **{"docs/plans/README.md": readme(("01", "01-a.md"), ("02", "02-b.md"), ("09", "../done.md"), extra="plan 9\n")})
        self.assertEqual(([], []), self.problems(files))

    def test_a_row_pointing_at_the_merged_file_of_a_plan_keeps_its_number_known(self):
        files = {"docs/plans/01-a.md": "# A\n\nStatus: done.\n", "docs/plans/02a-c.md": "# C\n\nStatus: done.\n", "docs/plans/README.md": readme(("01", "01-a.md"), ("02", "02a-c.md"), ("02a", "02a-c.md"), extra="plan 2\n")}
        self.assertEqual(([], []), self.problems(files))

    def test_a_suffixed_number_resolves_to_its_file(self):
        files = dict(GOOD, **{"docs/plans/02a-c.md": "# C\n\nStatus: done.\n", "docs/plans/README.md": readme(("01", "01-a.md"), ("02", "02-b.md"), ("02a", "02a-c.md"), extra="plan 2a\n")})
        self.assertEqual(([], []), self.problems(files))

    def test_the_index_must_list_every_plan_and_no_missing_one(self):
        files = dict(GOOD, **{"docs/plans/03-c.md": "# C\n\nStatus: done.\n", "docs/plans/README.md": readme(("01", "01-a.md"), ("02", "02-b.md"), ("04", "04-d.md"))})
        problems, _ = self.problems(files)
        text = "\n".join(problems)
        self.assertIn("does not list 03-c.md", text)
        self.assertIn("lists 04-d.md, which does not exist", text)

    def test_an_open_plan_with_every_row_done_is_flagged(self):
        files = dict(GOOD, **{"docs/plans/02-b.md": "# B\n\nStatus: **proposed** (2026-11-07).\n\n" + table("**Status: done**", "Status: built")})
        problems, _ = self.problems(files)
        self.assertEqual(1, len(problems))
        self.assertIn("all 2 slice rows are done", problems[0])

    def test_an_open_plan_with_a_row_left_is_fine(self):
        files = dict(GOOD, **{"docs/plans/02-b.md": "# B\n\nStatus: in progress.\n\n" + table("Status: done", "not built")})
        self.assertEqual(([], []), self.problems(files))

    def test_a_plan_without_slice_rows_is_never_flagged(self):
        files = dict(GOOD, **{"docs/plans/02-b.md": "# B\n\nStatus: proposed.\n\n| Q | question |\n|---|---|\n| a question | Status: done |\n"})
        self.assertEqual(([], []), self.problems(files))

    def test_a_closed_plan_with_every_row_done_is_fine(self):
        files = dict(GOOD, **{"docs/plans/02-b.md": "# B\n\nStatus: **closed**.\n\n" + table("Status: done")})
        self.assertEqual(([], []), self.problems(files))

    def test_the_baseline_turns_a_violation_into_a_warning(self):
        files = dict(GOOD, **{"docs/plans/02-b.md": "# B\n\nStatus: proposed. [C](03-c.md)\n"})
        problems, warnings = self.problems(files, "# note\n02-b.md: links\n")
        self.assertEqual([], problems)
        self.assertEqual(1, len(warnings))

    def test_a_baseline_line_without_a_violation_fails(self):
        problems, _ = self.problems(GOOD, "02-b.md: links\n")
        self.assertEqual(1, len(problems))
        self.assertIn("remove the line", problems[0])

    def test_the_repository_itself_passes(self):
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
        problems, _ = check.check(root)
        self.assertEqual([], problems)


if __name__ == "__main__":
    unittest.main()
