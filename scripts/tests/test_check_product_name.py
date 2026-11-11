import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_product_name as check  # noqa: E402

OLD = "Dbdata".replace("data", "Data") + "Console"  # DbDataConsole, spelled so this file is not a hit if the exemption is removed


def tree(files):
    root = tempfile.mkdtemp(prefix="product-name-")
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(text if isinstance(text, bytes) else text.encode("utf-8"))
    return root


class CheckProductName(unittest.TestCase):
    def run_check(self, files):
        return check.check(tree(files))

    def test_the_repository_is_clean(self):
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        problems, _ = check.check(root)
        self.assertEqual([], problems)

    def test_the_product_name_passes(self):
        problems, warnings = self.run_check({"a.cs": 'const string Name = "DbDataKit";\n', "b.md": "the console\n"})
        self.assertEqual(([], []), (problems, warnings))

    def test_both_old_names_are_found_with_file_and_line(self):
        for name in (OLD, "DbDataKit" + "Console"):
            with self.subTest(name):
                problems, _ = self.run_check({"src/a.cs": f'ok\nvar x = "{name}";\n'})
                self.assertEqual(1, len(problems), problems)
                self.assertTrue(problems[0].startswith("src/a.cs:2: "), problems[0])

    def test_any_kind_of_text_file_is_scanned_and_binary_is_not(self):
        problems, _ = self.run_check({"docs/a.md": OLD, "x/w.yml": f"name: {OLD} CI", "bin.dat": b"\0" + OLD.encode()})
        self.assertEqual(["docs/a.md:1", "x/w.yml:1"], sorted(p.split(":", 2)[0] + ":" + p.split(":", 2)[1] for p in problems))

    def test_the_rename_scripts_and_the_history_files_are_exempt(self):
        problems, _ = self.run_check({
            "scripts/rename_x.py": OLD, "scripts/tests/test_rename_x.py": OLD, "docs/ci-lessons.md": OLD,
            "docs/releases.md": OLD, "docs/plans/RELEASE-NOTES-PENDING.md": OLD,
        })
        self.assertEqual([], problems)

    def test_a_baseline_entry_tolerates_exactly_its_count(self):
        files = {"docs/p.md": f"{OLD} and {OLD}\n", check.BASELINE: "docs/p.md: 2\n"}
        problems, warnings = self.run_check(files)
        self.assertEqual([], problems)
        self.assertEqual(["docs/p.md: 2 (baseline)"], warnings)

    def test_more_than_the_baseline_fails(self):
        problems, _ = self.run_check({"docs/p.md": f"{OLD} {OLD} {OLD}\n", check.BASELINE: "docs/p.md: 2\n"})
        self.assertEqual(1, len(problems), problems)
        self.assertIn("3 occurrences, the baseline allows 2", problems[0])

    def test_fewer_than_the_baseline_fails_so_it_only_shrinks(self):
        problems, _ = self.run_check({"docs/p.md": f"{OLD}\n", check.BASELINE: "docs/p.md: 2\n"})
        self.assertEqual(1, len(problems), problems)
        self.assertIn("lower it", problems[0])

    def test_a_baseline_entry_without_a_hit_fails(self):
        problems, _ = self.run_check({"docs/p.md": "clean\n", check.BASELINE: "docs/p.md: 1\n"})
        self.assertEqual(1, len(problems), problems)
        self.assertIn("delete the line", problems[0])

    def test_a_malformed_baseline_line_is_refused(self):
        with self.assertRaises(ValueError):
            self.run_check({"a.md": "x\n", check.BASELINE: "docs/p.md\n"})


if __name__ == "__main__":
    unittest.main()
