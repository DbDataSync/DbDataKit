import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_no_wip as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    os.makedirs(os.path.join(root, "planning"))
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


class CheckNoWip(unittest.TestCase):
    def test_no_wip_folder_passes(self):
        root = tree({"planning/README.md": "# Planning\n"})
        self.assertEqual([], check.problems(root))
        self.assertEqual(0, check.main(["x", root]))

    def test_an_empty_wip_folder_passes(self):
        root = tree({})
        os.makedirs(os.path.join(root, "planning", "wip", "INSTALLER"))
        self.assertEqual(0, check.main(["x", root]))

    def test_a_wip_file_fails_and_its_open_items_are_listed(self):
        root = tree({"planning/wip/INSTALLER/INSTALLER-3.md": "# INSTALLER-3\n\nBuilt: the library.\n\n## Defaults\n\n- ports by binding\n1. ask the owner\n"})
        found = check.problems(root)
        self.assertIn("planning/wip/INSTALLER/INSTALLER-3.md: a work-in-progress file; resolve its items and delete it before merging into main", found)
        self.assertIn("  ## Defaults", found)
        self.assertIn("  - ports by binding", found)
        self.assertIn("  1. ask the owner", found)
        self.assertNotIn("  Built: the library.", found)
        self.assertEqual(1, check.main(["x", root]))

    def test_any_file_counts_whatever_its_name(self):
        root = tree({"planning/wip/README.md": "kept on purpose\n", "planning/wip/x.txt": "notes\n"})
        self.assertEqual(2, len(check.wip_files(root)))
        self.assertEqual(1, check.main(["x", root]))


if __name__ == "__main__":
    unittest.main()
