import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import delete_branches as tool  # noqa: E402


class Decide(unittest.TestCase):
    def test_the_rules(self):
        got = dict((n, (a, r)) for n, a, r in tool.decide(
            ["main", "suite/open-pr", "suite/stacked", "suite/gone", "suite/old"],
            "main", {"suite/open-pr"}, {"suite/stacked"}, {"main", "suite/open-pr", "suite/stacked", "suite/old"}))
        self.assertEqual("skip", got["main"][0])
        self.assertEqual("skip", got["suite/open-pr"][0])
        self.assertEqual("skip", got["suite/stacked"][0])
        self.assertEqual("skip", got["suite/gone"][0])
        self.assertEqual("delete", got["suite/old"][0])

    def test_the_list_file_ignores_comments_blanks_and_repeats(self):
        with tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8") as f:
            f.write("# header\n\nsuite/a  # why\nsuite/b\nsuite/a\n")
        try:
            self.assertEqual(["suite/a", "suite/b"], tool.load(f.name))
        finally:
            os.unlink(f.name)

    def test_the_committed_list_loads_and_never_names_the_default_branch(self):
        names = tool.load(tool.LIST)
        self.assertTrue(names)
        self.assertNotIn("main", names)


class Run(unittest.TestCase):
    def fake(self, deleted):
        def runner(*args):
            joined = " ".join(args)
            if "--method DELETE" in joined:
                deleted.append(args[3].split("heads/", 1)[1])
                return 0, "", ""
            if ".default_branch" in joined:
                return 0, "main\n", ""
            if "pulls?" in joined and "head.ref" in joined:
                return 0, "suite/open-pr\n", ""
            if "pulls?" in joined and "base.ref" in joined:
                return 0, "main\n", ""
            if "branches?" in joined:
                return 0, "main\nsuite/open-pr\nsuite/old\n", ""
            raise AssertionError(joined)
        return runner

    def write(self, text):
        f = tempfile.NamedTemporaryFile("w", suffix=".txt", delete=False, encoding="utf-8")
        f.write(text)
        f.close()
        self.addCleanup(os.unlink, f.name)
        return f.name

    def test_a_dry_run_deletes_nothing(self):
        deleted = []
        path = self.write("suite/old\nsuite/open-pr\n")
        self.assertEqual(0, tool.main(["--repo", "o/r", "--list", path], runner=self.fake(deleted)))
        self.assertEqual([], deleted)

    def test_apply_deletes_only_what_the_rules_allow(self):
        deleted = []
        path = self.write("suite/old\nsuite/open-pr\nmain\nsuite/gone\n")
        self.assertEqual(0, tool.main(["--repo", "o/r", "--list", path, "--apply"], runner=self.fake(deleted)))
        self.assertEqual(["suite/old"], deleted)


if __name__ == "__main__":
    unittest.main()
