import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_docs_commands as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


class CheckDocsCommands(unittest.TestCase):
    def test_current_command_names_pass(self):
        root = tree({"DbDataSync/README.md": "Run `dbdatasync server run` and `dbdatasync server setup`.\n"})
        self.assertEqual([], check.check(root))

    def test_a_retired_command_is_reported_with_its_line_and_replacement(self):
        root = tree({"DbDataSync/README.md": "intro\nStart it with dbdatasync serve --repo x\n"})
        problems = check.check(root)
        self.assertEqual(1, len(problems))
        self.assertIn("DbDataSync/README.md:2", problems[0])
        self.assertIn("dbdatasync server run", problems[0])

    def test_each_retired_proxy_form_is_found(self):
        for text in ["dbdataproxy hash-password", "dbdataproxy install", "dbdataproxy query select 1", "dbdataproxy --healthcheck", "DbDataProxy.exe"]:
            with self.subTest(text=text):
                self.assertEqual(1, len(check.check(tree({"DbDataProxy/README.md": text + "\n"}))))

    def test_the_new_proxy_forms_pass(self):
        root = tree({"DbDataProxy/README.md": "dbdataproxy query run \"select 1\"\ndbdataproxy secret hash\ndbdataproxy.exe server run\n"})
        self.assertEqual([], check.check(root))

    def test_history_is_not_checked(self):
        root = tree({"docs/plans/13-x.md": "dbdatasync serve\n", "DbDataSync/architecture/implementation/p.md": "dbdatasync setup\n", "docs/RELEASE-NOTES-PENDING.md": "dbdatasync serve\n"})
        self.assertEqual([], check.check(root))

    def test_a_line_can_name_the_old_form_on_purpose(self):
        root = tree({"x/README.md": "# retired-name-ok\nmv DbDataProxy.exe dbdataproxy.exe\n"})
        self.assertEqual([], check.check(root))


if __name__ == "__main__":
    unittest.main()
