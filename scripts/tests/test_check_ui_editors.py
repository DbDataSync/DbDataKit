import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_ui_editors as check  # noqa: E402

HEADER = "| Feature | Authored | Format | Editor | State | Gap |\n| --- | --- | --- | --- | --- | --- |\n"
COUNTS = "Counts: full {full}, partial {partial}, view-only {view}, none {none}, planned {planned}.\n"
PLAN = "| PAGEFORMAT-3 | a screen |\n| ALERTING-2 | another |\n"


def formats(*ids):
    return json.dumps({"formats": [{"id": i} for i in ids]})


def tree(rows, ids=("alpha",), counts=None, plan=PLAN, extra=None):
    root = tempfile.mkdtemp()
    files = {"docs/formats.json": formats(*ids), "planning/PAGEFORMAT.md": plan,
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
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | `F.tsx` | full | |"], counts=c(full=1), extra={"Shared/web/m/src/pages/F.tsx": ""})))

    def test_a_format_without_a_row_fails_naming_it(self):
        found = check.problems(tree(["| A | x | - | none | none (no editor wanted) | |"], ids=("alpha", "beta"), counts=c(none=1)))
        self.assertEqual(2, len(found))
        self.assertTrue(all("`alpha`" in f or "`beta`" in f for f in found))
        self.assertIn("no row for the stored format `beta`", " ".join(found))

    def test_an_exemption_in_the_state_cell_passes(self):
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | none (generated, never edited by hand) | |"], counts=c(none=1))))

    def test_a_gap_that_names_a_task_passes(self):
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | none | ALERTING-2 |"], counts=c(none=1))))

    def test_a_named_task_must_exist_in_any_state(self):
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | planned | PAGEFORMAT-3 |"], counts=c(planned=1))))
        for state, key in (("planned", "planned"), ("partial", "partial"), ("none (x)", "none")):
            with self.subTest(state=state):
                found = check.problems(tree([f"| A | x | `alpha` | none | {state} | PAGEFORMAT-99 |"], counts=c(**{key: 1})))
                self.assertEqual(1, len(found))
                self.assertIn("PAGEFORMAT-99, which is not a task", found[0])

    def test_a_task_of_a_short_slug_closes_a_gap_and_must_exist(self):
        extra = {"planning/SEO4.md": "| SEO4-1 | a screen |\n"}
        self.assertEqual([], check.problems(tree(["| A | x | `alpha` | none | planned | SEO4-1 |"], counts=c(planned=1), extra=extra)))
        found = check.problems(tree(["| A | x | `alpha` | none | planned | SEO4-9 |"], counts=c(planned=1), extra=extra))
        self.assertEqual(1, len(found))
        self.assertIn("SEO4-9, which is not a task", found[0])

    def test_a_short_word_with_a_number_that_is_no_slug_is_not_a_task(self):
        found = check.problems(tree(["| A | x | `alpha` | none | partial | hashed with SHA-256 |"], counts=c(partial=1)))
        self.assertEqual(1, len(found))
        self.assertIn("neither which task", found[0])

    def test_a_row_whose_subject_was_deleted_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | planned | PAGEFORMAT-3 |"], counts=c(planned=1), plan=""))
        self.assertEqual(1, len(found))
        self.assertIn("PAGEFORMAT-3", found[0])

    def test_an_old_slice_id_is_history_and_does_not_close_a_gap(self):
        found = check.problems(tree(["| A | x | `alpha` | none | partial | closed by FQ17 once built |"], counts=c(partial=1)))
        self.assertEqual(1, len(found))
        self.assertIn("neither which task", found[0])

    def test_a_gap_with_no_task_and_no_reason_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | none | later |"], counts=c(none=1)))
        self.assertEqual(1, len(found))
        self.assertIn("neither which task", found[0])

    def test_a_reference_to_the_removed_editors_plan_fails_in_any_cell(self):
        found = check.problems(tree(["| A | x | `alpha` | none | full | UE09 built: the form |"], counts=c(full=1)))
        self.assertEqual(1, len(found))
        self.assertIn("UE09", found[0])

    def test_an_unknown_state_fails(self):
        found = check.problems(tree(["| A | x | `alpha` | none | mostly | ALERTING-2 |"], counts=c()))
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
