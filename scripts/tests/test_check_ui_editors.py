import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_ui_editors as check  # noqa: E402

HEADER = "| Feature | Authored | Format | Editor | State | Gap |\n| --- | --- | --- | --- | --- | --- |\n"
COUNTS = "Counts: full {full}, partial {partial}, view-only {view}, none {none}, planned {planned}.\n"
PLAN = "| DBR27 | a screen |\n| GP18 | another |\n"


def formats(*ids):
    return json.dumps({"formats": [{"id": i} for i in ids]})


def tree(rows, ids=("alpha",), counts=None, plan=PLAN, extra=None):
    root = tempfile.mkdtemp()
    files = {"docs/formats.json": formats(*ids), "docs/plans/32-example.md": plan,
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
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | none | GP18 |"], counts=c(none=1))))

    def test_a_slice_split_into_lettered_parts_is_found_in_a_plan(self):
        plan = "| SL13a | the editor |\n| **SL13b** | the storage |\n"
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | planned | SL13b |"], counts=c(planned=1), plan=plan)))
        found = check.problems(tree(["| A | x | `alpha` | none | planned | SL13c |"], counts=c(planned=1), plan=plan))
        self.assertEqual(1, len(found))
        self.assertIn("SL13c", found[0])

    def test_a_planned_row_names_a_slice_of_a_plan_that_exists(self):
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | planned | DBR27 |"], counts=c(planned=1))))
        found = check.problems(tree(["| A | x | `alpha` | none | planned | DBR99 |"], counts=c(planned=1)))
        self.assertEqual(1, len(found))
        self.assertIn("DBR99", found[0])

    def test_a_planned_row_whose_plan_was_deleted_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | planned | DBR27 |"], counts=c(planned=1), plan=""))
        self.assertEqual(1, len(found))
        self.assertIn("DBR27", found[0])

    def test_a_slice_of_a_built_row_is_history_and_is_not_looked_up(self):
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | partial | closed by FQ17 once built |"], counts=c(partial=1))))

    def test_a_gap_with_no_slice_and_no_reason_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | none | later |"], counts=c(none=1)))
        self.assertEqual(1, len(found))
        self.assertIn("neither which slice", found[0])

    def test_a_reference_to_the_removed_editors_plan_fails_in_any_cell(self):
        found = check.problems(tree(["| A | x | `alpha` | none | full | UE09 built: the form |"], counts=c(full=1)))
        self.assertEqual(1, len(found))
        self.assertIn("UE09", found[0])

    def test_an_unknown_state_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | mostly | GP18 |"], counts=c()))
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
