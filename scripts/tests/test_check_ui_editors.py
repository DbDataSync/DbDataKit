import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_ui_editors as check  # noqa: E402

HEADER = "| Feature | Authored | Format | Editor | State | Gap |\n| --- | --- | --- | --- | --- | --- |\n"
COUNTS = "Counts: full {full}, partial {partial}, view-only {view}, none {none}, planned {planned}.\n"
PLAN = "| UE01 | a screen |\n| UE02 | another |\n"


def formats(*ids):
    return json.dumps({"formats": [{"id": i} for i in ids]})


def tree(rows, ids=("alpha",), counts=None, plan=PLAN, extra=None):
    root = tempfile.mkdtemp()
    files = {"docs/formats.json": formats(*ids), "docs/plans/33-ui-editors.md": plan,
             "docs/ui-editors.md": HEADER + "".join(r + "\n" for r in rows) + "\n" + (counts or "")}
    files.update(extra or {})
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


def c(**kw):
    n = dict(full=0, partial=0, view=0, none=0, planned=0)
    n.update(kw)
    return COUNTS.format(**n)


class CheckUiEditors(unittest.TestCase):
    def test_a_full_row_for_the_format_passes(self):
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | `F.tsx` | full | |"], counts=c(full=1), extra={"DbDataKit/web/m/src/pages/F.tsx": ""})))

    def test_a_format_without_a_row_fails_naming_it(self):
        found = check.problems(tree(["| A | x | - | none | none (no editor wanted) | |"], ids=("alpha", "beta"), counts=c(none=1)))
        self.assertEqual(2, len(found))
        self.assertTrue(all("`alpha`" in f or "`beta`" in f for f in found))
        self.assertIn("no row for the stored format `beta`", " ".join(found))

    def test_an_exemption_in_the_state_cell_passes(self):
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | none (generated, never edited by hand) | |"], counts=c(none=1))))

    def test_a_gap_that_names_a_slice_passes(self):
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | none | UE02 |"], counts=c(none=1))))

    def test_another_plans_slice_passes(self):
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | planned | DBR27 |"], counts=c(planned=1))))

    def test_a_gap_with_no_slice_and_no_reason_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | none | later |"], counts=c(none=1)))
        self.assertEqual(1, len(found))
        self.assertIn("neither which slice", found[0])

    def test_a_slice_that_is_not_in_plan_33_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | none | UE09 |"], counts=c(none=1)))
        self.assertEqual(1, len(found))
        self.assertIn("UE09", found[0])

    def test_an_unknown_state_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | mostly | UE01 |"], counts=c()))
        self.assertTrue(any("state 'mostly'" in f for f in found))

    def test_a_format_that_is_not_registered_fails(self):
        found = check.problems(tree(["| A | x | `alpha` `gamma` | none | none (x) | |"], counts=c(none=1)))
        self.assertEqual(1, len(found))
        self.assertIn("`gamma`", found[0])

    def test_a_screen_file_that_does_not_exist_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | `Gone.tsx` | full | |"], counts=c(full=1)))
        self.assertEqual(1, len(found))
        self.assertIn("Gone.tsx", found[0])

    def test_a_wrong_counts_line_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | none (x) | |"], counts=c(full=1)))
        self.assertEqual(1, len(found))
        self.assertIn("Counts", found[0])

    def test_a_missing_document_fails(self):
        self.assertEqual(["docs/ui-editors.md does not exist"], check.problems(tempfile.mkdtemp()))

    def test_the_real_repository_passes(self):
        self.assertEqual([], check.problems(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))))


if __name__ == "__main__":
    unittest.main()
