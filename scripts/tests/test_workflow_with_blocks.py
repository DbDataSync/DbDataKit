"""A `with:` block of a workflow step or action must have its inputs indented under it.

`with:` followed by a line at the same indent makes the step's inputs null and the file unparseable for GitHub ("Unexpected value ''"):
a called workflow that fails to parse stops every release that calls it (docs/ci-lessons.md). The workflow ran fine on its own until
someone dispatched suite-release.yml, so nothing else catches it before a release."""
import glob
import os
import re
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


class WithBlocks(unittest.TestCase):
    def test_every_with_has_its_inputs_indented(self):
        files = glob.glob(os.path.join(ROOT, ".github", "workflows", "*.yml")) + glob.glob(os.path.join(ROOT, ".github", "actions", "*", "action.yml"))
        self.assertTrue(files)
        problems = []
        for path in sorted(files):
            with open(path, encoding="utf-8") as f:
                lines = f.read().split("\n")
            for i, line in enumerate(lines[:-1]):
                m = re.match(r"^(\s*)with:\s*$", line)
                if not m:
                    continue
                nxt = lines[i + 1]
                if nxt.strip() and len(nxt) - len(nxt.lstrip()) <= len(m.group(1)):
                    problems.append(f"{os.path.relpath(path, ROOT)}:{i + 1}: `with:` is followed by `{nxt.strip()}` at the same indent")
        self.assertEqual([], problems)


if __name__ == "__main__":
    unittest.main()
