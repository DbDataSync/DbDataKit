import io
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stdout
from pathlib import Path

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import audit_plan_migration as audit  # noqa: E402
from gitenv import clean_env, run_git  # noqa: E402

PLAN = """# Plan 99: a sample

## Slices

| Slice | What | Done when |
| --- | --- | --- |
| SC23 | Delete the servers | gone |
| **DBR53b** | Batches | staged |
| D18 (was Q1) | A decision with its old number | decided |
| 3, 16 | Two numbered decisions in one row | decided |

## Decisions

1. **Reader-shaped interchange stays the default.**
2. Moves wait for a second product.

## Steps of a rollover

1. Not a decision: a numbered step under a heading that names none.

```yaml
## not a heading: inside a fence
| XX99 | not a row |
```

```diff
+| I01 | a row a diff adds | trust |
+## I. A heading a diff adds
 | L08 | a context row of the diff, not added |
```
"""


class Extract(unittest.TestCase):
    def test_rows_headings_and_decision_lists(self):
        items = audit.extract(PLAN)
        self.assertIn(("row", "SC23"), items)
        self.assertIn(("row", "DBR53b"), items)
        self.assertIn(("row", "D18"), items)
        self.assertIn(("row", "Q1"), items)
        self.assertIn(("row", "#3"), items)
        self.assertIn(("row", "#16"), items)
        self.assertIn(("heading", "Slices"), items)
        self.assertIn(("list", "Decisions #1"), items)
        self.assertIn(("list", "Decisions #2"), items)

    def test_fences_count_only_lines_a_diff_adds(self):
        items = audit.extract(PLAN)
        self.assertIn(("row", "I01"), items)
        self.assertIn(("heading", "I. A heading a diff adds"), items)
        self.assertNotIn(("row", "XX99"), items)
        self.assertNotIn(("row", "L08"), items)
        self.assertNotIn(("heading", "not a heading: inside a fence"), items)

    def test_steps_under_other_headings_are_not_items(self):
        self.assertFalse(any(k.startswith("Steps of a rollover #") for _, k in audit.extract(PLAN)))

    def test_the_separator_and_the_header_are_not_ids(self):
        self.assertNotIn(("row", "Slice"), audit.extract(PLAN))


FULL_TRACE = """| Old file | Old id or heading | Summary | Old status | Outcome |
| --- | --- | --- | --- | --- |
| 99-sample.md | ## Slices | | history | history |
| 99-sample.md | SC23 | servers | built | built |
| 99-sample.md | DBR53b | batches | partly | task PAGEFORMAT-9 |
| 99-sample.md | D18, Q1, #3, #16 | decisions | decided | doc docs/x.md |
| 99-sample.md | Decisions #1 #2 | rules | decided | THINNED |
| 99-sample.md | ## Decisions, ## Steps of a rollover | | design | doc |
| 99-sample.md | I01 | trust | open | task CERTIFICATES-5 |
| 99-sample.md | ## I. A heading a diff adds | | open | task CERTIFICATES-5 |
"""


class Missing(unittest.TestCase):
    def test_a_full_trace_misses_nothing(self):
        self.assertEqual([], audit.missing({"99-sample.md": PLAN}, FULL_TRACE))

    def test_each_kind_of_missing_item_is_listed(self):
        trace = FULL_TRACE.replace("| 99-sample.md | SC23 |", "| 99-sample.md | SC2 |")
        trace = trace.replace("Decisions #1 #2", "Decisions #1")
        trace = trace.replace("## Slices", "## Slice list")
        found = audit.missing({"99-sample.md": PLAN}, trace)
        self.assertIn(("99-sample.md", "row", "SC23"), found)
        self.assertIn(("99-sample.md", "list", "Decisions #2"), found)
        self.assertIn(("99-sample.md", "heading", "Slices"), found)
        self.assertEqual(3, len(found))

    def test_an_id_is_matched_as_a_whole_token_and_in_the_same_file(self):
        trace = FULL_TRACE.replace("| 99-sample.md | DBR53b |", "| 99-sample.md | DBR53 |")
        trace += "| other.md | DBR53b | elsewhere | open | task X-1 |\n"
        self.assertEqual([("99-sample.md", "row", "DBR53b")], audit.missing({"99-sample.md": PLAN}, trace))

    def test_a_number_is_not_matched_inside_another(self):
        trace = FULL_TRACE.replace("D18, Q1, #3, #16", "D18, Q1, #30, #16")
        self.assertEqual([("99-sample.md", "row", "#3")], audit.missing({"99-sample.md": PLAN}, trace))

    def test_escaped_pipes_in_a_heading_cell(self):
        plan = "## Mode: screen | print\n"
        trace = "| 99-sample.md | ## Mode: screen \\| print | | design | doc |\n"
        self.assertEqual([], audit.missing({"99-sample.md": plan}, trace))


class FromGit(unittest.TestCase):
    def test_main_reads_the_old_files_at_a_commit(self):
        root = Path(tempfile.mkdtemp())
        run_git(root, "init", "-q")
        run_git(root, "config", "user.email", "test@example.invalid")
        run_git(root, "config", "user.name", "test")
        folder = root / "docs" / "plans" / "needs-review"
        folder.mkdir(parents=True)
        (folder.parent / "99-sample.md").write_text(PLAN, encoding="utf-8")
        (folder / "lane.md").write_text("## Lane\n\n| I00 | a smoke test |\n", encoding="utf-8")
        run_git(root, "add", "-A")
        run_git(root, "commit", "-q", "-m", "plans")
        trace = root / "trace.md"
        trace.write_text(FULL_TRACE + "| needs-review/lane.md | ## Lane, I00 | | open | task CERTIFICATES-2 |\n", encoding="utf-8")
        old_cwd, old_env = os.getcwd(), dict(os.environ)
        try:
            os.chdir(root)
            os.environ.clear()
            os.environ.update(clean_env())
            out = io.StringIO()
            with redirect_stdout(out):
                self.assertEqual(0, audit.main(["x", "--commit", "HEAD", "--trace", str(trace)]))
            self.assertIn("all 15 items of 2 old files", out.getvalue())
            trace.write_text(FULL_TRACE, encoding="utf-8")
            out = io.StringIO()
            with redirect_stdout(out):
                self.assertEqual(1, audit.main(["x", "--commit", "HEAD", "--trace", str(trace)]))
            self.assertIn("needs-review/lane.md: row I00", out.getvalue())
            self.assertIn("needs-review/lane.md: heading Lane", out.getvalue())
        finally:
            os.chdir(old_cwd)
            os.environ.clear()
            os.environ.update(old_env)


if __name__ == "__main__":
    unittest.main()
