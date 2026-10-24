import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_solution as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


def slnx(*paths):
    return "<Solution>" + "".join(f'<Project Path="{p}" />' for p in paths) + "</Solution>"


class CheckSolution(unittest.TestCase):
    def test_a_complete_solution_passes(self):
        root = tree({"DbDataKit.slnx": slnx("A/A.csproj"), "A/A.csproj": "<Project />"})
        self.assertEqual([], check.check(root))

    def test_a_project_missing_from_the_solution_fails(self):
        root = tree({"DbDataKit.slnx": slnx("A/A.csproj"), "A/A.csproj": "<Project />", "B/B.csproj": "<Project />"})
        self.assertEqual(["DbDataKit.slnx: B/B.csproj is not listed"], check.check(root))

    def test_a_listed_project_that_does_not_exist_fails(self):
        root = tree({"DbDataKit.slnx": slnx("A/A.csproj", "Gone/Gone.csproj"), "A/A.csproj": "<Project />"})
        self.assertEqual(["DbDataKit.slnx: Gone/Gone.csproj does not exist"], check.check(root))

    def test_a_filter_naming_an_unlisted_project_fails(self):
        filt = json.dumps({"solution": {"path": "../DbDataKit.slnx", "projects": ["A/A.csproj", "X/X.csproj"]}})
        root = tree({"DbDataKit.slnx": slnx("A/A.csproj"), "A/A.csproj": "<Project />", "A/A.slnf": filt})
        self.assertEqual(["A/A.slnf: X/X.csproj is not in DbDataKit.slnx"], check.check(root))

    def test_the_repository_passes(self):
        here = os.path.dirname(os.path.abspath(__file__))
        self.assertEqual([], check.check(os.path.abspath(os.path.join(here, "..", ".."))))


if __name__ == "__main__":
    unittest.main()
