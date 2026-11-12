import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_state_migrations as check  # noqa: E402

Q = '"' * 3


def module(migrations, compat=1, name="demo"):
    items = ",\n".join(f"{Q}\n{m}\n{Q}" for m in migrations)
    tail = f",\n        compatibleWithPrevious: {compat}" if compat is not None else ""
    return (f"public static class DemoModule\n{{\n    public static StateModule Module {{ get; }} = new(\n        \"{name}\",\n        StateScopeKind.Group,\n"
            f"        [\n{items},\n        ]{tail});\n}}\n")


def tree(source):
    root = tempfile.mkdtemp()
    path = os.path.join(root, "src", "DemoModule.cs")
    os.makedirs(os.path.dirname(path))
    with open(path, "w", encoding="utf-8") as f:
        f.write(source)
    return root


CREATE = "CREATE TABLE {table:t} (id {{key}} NOT NULL, PRIMARY KEY (id));"
ADD = "ALTER TABLE {table:t} {{addcolumn}} note {{text}} NULL;\nALTER TABLE {table:t} {{addcolumn}} n {{int}} NOT NULL DEFAULT 0;"


class StateMigrationTests(unittest.TestCase):
    def problems(self, source):
        return check.check(tree(source))

    def test_additive_migrations_pass(self):
        self.assertEqual([], self.problems(module([CREATE, ADD + "\nCREATE INDEX {index:i} ON {table:t}(note);"])))

    def test_dropped_column_marked_compatible_fails(self):
        p = self.problems(module([CREATE, "ALTER TABLE {table:t} DROP COLUMN note;"]))
        self.assertEqual(1, len(p))
        self.assertIn("migration 2", p[0])
        self.assertIn("removes", p[0])

    def test_rename_fails(self):
        p = self.problems(module([CREATE, "ALTER TABLE {table:t} RENAME COLUMN note TO remark;"]))
        self.assertTrue(any("renames" in x for x in p))

    def test_dropped_table_and_changed_type_fail(self):
        self.assertTrue(self.problems(module([CREATE, "DROP TABLE {table:t};"])))
        self.assertTrue(self.problems(module([CREATE, "ALTER TABLE {table:t} ALTER COLUMN id {{int}} NOT NULL;"])))

    def test_not_null_column_without_default_fails(self):
        p = self.problems(module([CREATE, "ALTER TABLE {table:t} {{addcolumn}} must {{int}} NOT NULL;"]))
        self.assertTrue(any("NOT NULL column with no default" in x for x in p))

    def test_removal_with_a_contract_note_passes(self):
        note = "-- Contract: 2026.12.1 stopped reading note\nALTER TABLE {table:t} DROP COLUMN note;"
        self.assertEqual([], self.problems(module([CREATE, note])))

    def test_removal_with_no_release_in_the_note_fails(self):
        self.assertTrue(self.problems(module([CREATE, "-- Contract:\nALTER TABLE {table:t} DROP COLUMN note;"])))

    def test_module_marked_incompatible_may_remove(self):
        self.assertEqual([], self.problems(module([CREATE, "ALTER TABLE {table:t} DROP COLUMN note;"], compat=0)))
        self.assertEqual([], self.problems(module([CREATE, "ALTER TABLE {table:t} DROP COLUMN note;"], compat=None)))

    def test_only_the_promised_migrations_are_judged_but_all_are_listed(self):
        source = module([CREATE, "ALTER TABLE {table:t} DROP COLUMN note;", ADD])
        root = tree(source)
        listing = []
        self.assertEqual([], check.check(root, listing))
        self.assertEqual(1, len(listing))
        self.assertIn("migration 2 of 3", listing[0])

    def test_comments_are_not_statements(self):
        self.assertEqual([], self.problems(module([CREATE, "-- we do not DROP anything; DROP TABLE is not run\n" + ADD])))

    def test_a_promise_over_a_list_that_is_not_literal_fails(self):
        source = ("public static class M\n{\n    public static StateModule Module { get; } = new(\n        \"x\",\n        StateScopeKind.Group,\n"
                  "        Migrations,\n        compatibleWithPrevious: 1);\n}\n")
        self.assertIn("literal list", self.problems(source)[0])

    def test_the_repository_passes(self):
        self.assertEqual([], check.check(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))))


if __name__ == "__main__":
    unittest.main()
