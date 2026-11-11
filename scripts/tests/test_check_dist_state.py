import json
import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_dist_state as check  # noqa: E402


def tree(notes):
    root = tempfile.mkdtemp()
    for folder, text in notes.items():
        os.makedirs(os.path.join(root, folder))
        with open(os.path.join(root, folder, "dist.json"), "w", encoding="utf-8") as f:
            json.dump({"notes": text}, f)
    return root


GOOD = {"DbDataKit": "see dbdatakit state init", "DbDataGateway": "dbdatagateway state init", "DbDataBuild": "run dbdatabuild state init", "DbDataRunner": "dbdatarunner state init", "DbDataStore": "dbdatastore state init", "DbDataWorkspace": "dbdataworkspace state init"}


class CheckDistState(unittest.TestCase):
    def test_notes_that_name_the_verb_pass(self):
        self.assertEqual([], check.problems(tree(GOOD)))

    def test_a_missing_note_is_named(self):
        found = check.problems(tree({**GOOD, "DbDataGateway": "nothing"}))
        self.assertEqual(1, len(found))
        self.assertIn("dbdatagateway state init", found[0])

    def test_the_repository_itself_passes(self):
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        self.assertEqual([], check.problems(root))


if __name__ == "__main__":
    unittest.main()
