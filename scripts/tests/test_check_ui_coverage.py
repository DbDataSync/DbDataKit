import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_ui_coverage as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


PAGES = "DbDataKit/web/sync/src/pages/"


class CheckUiCoverage(unittest.TestCase):
    def test_a_named_screen_passes(self):
        root = tree({PAGES + "FooPage.tsx": "", "docs/ui-coverage.md": "| `FooPage.tsx` |"})
        self.assertEqual([], check.problems(root))

    def test_a_screen_the_document_leaves_out_fails(self):
        root = tree({PAGES + "FooPage.tsx": "", PAGES + "BarTab.tsx": "", "docs/ui-coverage.md": "| `FooPage.tsx` |"})
        self.assertEqual(["docs/ui-coverage.md does not name BarTab.tsx (sync)"], check.problems(root))

    def test_a_file_the_document_names_that_is_gone_fails(self):
        root = tree({PAGES + "FooPage.tsx": "", "docs/ui-coverage.md": "`FooPage.tsx` `GonePage.tsx`"})
        self.assertEqual(1, len(check.problems(root)))
        self.assertIn("GonePage.tsx", check.problems(root)[0])

    def test_tests_and_helpers_are_not_screens(self):
        root = tree({PAGES + "FooPage.tsx": "", PAGES + "FooPage.test.tsx": "", PAGES + "helper.ts": "", "docs/ui-coverage.md": "`FooPage.tsx`"})
        self.assertEqual([], check.problems(root))

    def test_a_missing_document_fails(self):
        self.assertEqual(["docs/ui-coverage.md does not exist"], check.problems(tree({PAGES + "FooPage.tsx": ""})))

    def test_the_real_repository_passes(self):
        self.assertEqual([], check.problems(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))))


if __name__ == "__main__":
    unittest.main()
