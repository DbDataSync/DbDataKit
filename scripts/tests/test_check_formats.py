import hashlib
import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_formats as check  # noqa: E402

LOADER = "src/Loader.cs"
SCHEMA = "src/thing.schema.json"
LOADER_TEXT = "public const int Version = 2;\n"
SCHEMA_TEXT = '{ "type": "object" }\n'


def sha(text):
    return hashlib.sha256(text.encode()).hexdigest()


def entry(**changes):
    e = {
        "id": "thing", "current": 2, "previous": 1, "status": "frozen",
        "version_source": {"file": LOADER, "pattern": r"public const int Version = (\d+);"},
        "files": [{"path": SCHEMA, "sha256": sha(SCHEMA_TEXT)}],
    }
    e.update(changes)
    return e


def tree(entries, loader=LOADER_TEXT, schema=SCHEMA_TEXT):
    root = tempfile.mkdtemp()
    files = {LOADER: loader, SCHEMA: schema, "docs/formats.json": json.dumps({"formats": entries})}
    for rel, text in files.items():
        path = os.path.join(root, *rel.split("/"))
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8", newline="") as f:
            f.write(text)
    return root


class CheckFormats(unittest.TestCase):
    def problems(self, entries, **kw):
        return check.check(tree(entries, **kw))

    def test_a_consistent_registry_passes(self):
        self.assertEqual([], self.problems([entry()]))

    def test_an_open_format_may_be_edited(self):
        open_entry = entry(status="open", files=[{"path": SCHEMA, "sha256": None}])
        self.assertEqual([], self.problems([open_entry], schema='{ "type": "array" }\n'))

    def test_an_edited_frozen_schema_fails(self):
        found = self.problems([entry()], schema='{ "type": "array" }\n')
        self.assertEqual(1, len(found))
        self.assertIn("frozen, but src/thing.schema.json changed", found[0])

    def test_line_endings_do_not_count(self):
        self.assertEqual([], self.problems([entry()], schema=SCHEMA_TEXT.replace("\n", "\r\n")))

    def test_a_version_bumped_in_the_code_without_an_entry_fails(self):
        found = self.problems([entry()], loader="public const int Version = 3;\n")
        self.assertEqual(1, len(found))
        self.assertIn("declares version 3", found[0])
        self.assertIn("current 2", found[0])

    def test_an_entry_the_code_does_not_say_yet_fails(self):
        self.assertIn("declares version 2", self.problems([entry(current=3)])[0])

    def test_a_frozen_format_needs_its_hash(self):
        found = self.problems([entry(files=[{"path": SCHEMA, "sha256": None}])])
        self.assertIn("needs its sha256", found[0])

    def test_an_entry_that_is_not_well_formed_fails(self):
        for bad, text in [
            (entry(status="draft"), "`open` or `frozen`"),
            (entry(previous=3), "above `current`"),
            (entry(current="2"), "whole numbers"),
            (entry(files=[]), "`files`"),
            (entry(files=[{"path": "src/missing.json", "sha256": None}]), "does not exist"),
            (entry(version_source={"file": LOADER, "pattern": "no such text (\\d+)"}), "matches 0 times"),
            (entry(version_source={"file": "src/gone.cs", "pattern": "(\\d)"}), "does not exist"),
        ]:
            with self.subTest(text):
                self.assertTrue(any(text in p for p in self.problems([bad])), self.problems([bad]))

    def test_a_format_listed_twice_fails(self):
        self.assertTrue(any("listed twice" in p for p in self.problems([entry(), entry()])))

    def test_a_missing_registry_fails(self):
        self.assertIn("registry is missing", check.check(tempfile.mkdtemp())[0])

    def test_the_repository_agrees_with_its_registry(self):
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
        self.assertEqual([], check.check(root))


if __name__ == "__main__":
    unittest.main()
