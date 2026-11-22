import io
import os
import sys
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import review_dates as review  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


def run(root, *flags):
    out, err = io.StringIO(), io.StringIO()
    with redirect_stdout(out), redirect_stderr(err):
        code = review.main(["x", root, *flags])
    return code, out.getvalue(), err.getvalue()


class ReviewDates(unittest.TestCase):
    def test_a_dated_and_an_undated_file_are_reported(self):
        root = tree({
            "docs/formats.md": "# File formats\n<!-- reviewed: 2026-11-20 -->\n\nText.\n",
            "docs/install.md": "# Install\n\nText.\n",
        })
        code, out, _ = run(root)
        self.assertEqual(0, code)
        self.assertIn("reviewed 2026-11-20  docs/formats.md", out)
        self.assertIn("not reviewed      docs/install.md", out)

    def test_missing_lists_only_the_undated(self):
        root = tree({
            "docs/formats.md": "# File formats\n<!-- reviewed: 2026-11-20 -->\n",
            "docs/install.md": "# Install\n",
        })
        _, out, _ = run(root, "--missing")
        self.assertNotIn("formats.md", out)
        self.assertIn("docs/install.md", out)

    def test_a_malformed_review_line_fails_naming_the_file(self):
        root = tree({"docs/formats.md": "# File formats\n<!-- reviewed: 20 November -->\n"})
        code, _, err = run(root)
        self.assertEqual(1, code)
        self.assertIn("docs/formats.md", err)

    def test_which_files_are_read(self):
        root = tree({
            "planning/README.md": "# Planning\n",
            "planning/ROLES.md": "# Roles\n",
            "planning/INSTALLER.md": "# INSTALLER\n",
            "planning/wip/INSTALLER/INSTALLER-1.md": "# wip\n",
            "docs/release-notes/x.md": "# note\n",
            "Shared/web/node_modules/pkg/README.md": "# pkg\n",
            "DbDataKit/README.md": "# dbdatakit\n",
            "DbDataKit/notes.md": "# notes\n",
        })
        self.assertEqual(["DbDataKit/README.md", "planning/INSTALLER.md", "planning/ROLES.md"], review.files(root))


if __name__ == "__main__":
    unittest.main()
