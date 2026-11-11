import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_secret_names as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(text if isinstance(text, bytes) else text.encode("utf-8"))
    return root


class CheckSecretNames(unittest.TestCase):
    def problems(self, files):
        return check.check(tree(files))

    def test_the_new_scheme_passes(self):
        self.assertEqual([], self.problems({
            "a.cs": 'var name = "dbdatakit:gateway:state:session-key"; var r = "secret:dbdatakit:server:state:x"; // DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_ORDERS\n',
            "b.md": "`dbdatakit:<service>:<kind>:<name>[:<part>]` and `dbdatakit_sync_connection_erp` and the dbdatakit: prefix\n",
        }))

    def test_every_form_of_an_old_name_is_found_with_its_file_and_line(self):
        for text in ("dbdata:sync:connection:x", "secret:dbdata:build:git:sales", "dbdata:console:state:x", "dbdata:proxy:datasource:erp", "dbdata:kit:console:signing-key",
                     "$\"dbdata:{Product}:{Kind}:\"", "\"dbdata:\"", "dbdata:<product>:<kind>", "dbdata:&lt;product&gt;", "dbdata:a:b",
                     "DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_ORDERS", "dbdata_gateway_datasource_erp_password", 'public const string Root = "dbdata";'):
            with self.subTest(text):
                problems = self.problems({"src/a.cs": f"ok\nvar x = {text};\n"})
                self.assertEqual(1, len(problems), problems)
                self.assertTrue(problems[0].startswith("src/a.cs:2: "), problems[0])

    def test_a_name_is_found_in_any_kind_of_text_file(self):
        for path in ("docs/a.md", "dist/compose/a.yml", "x/run.sh", ".github/workflows/w.yml", "web/a.ts", "Dockerfile"):
            with self.subTest(path):
                self.assertEqual(1, len(self.problems({path: "  secret: secret:dbdata:sync:connection:x\n"})))

    def test_the_message_of_an_apps_frame_is_not_a_secret(self):
        self.assertEqual([], self.problems({"a.ts": "window.parent.postMessage({ dbdata: 'app/1', id }, '*')\nconst m = {dbdata:'app/1'}\ntype Reply = { dbdata: string }\n"}))

    def test_words_that_only_contain_the_root_are_not_names(self):
        self.assertEqual([], self.problems({"a.md": "dbdata_roles dbdata_apps dbdata~gateway~x my-dbdata:x and xdbdata:y dbdatakit:server:state:z\n"}))

    def test_the_files_that_tell_history_are_exempt(self):
        text = "dbdata:sync:connection:x\n"
        files = {path: text for path in (
            "docs/plans/RELEASE-NOTES-PENDING.md", "docs/plans/33-fewer-services.md", "docs/ci-lessons.md", "scripts/rename_secret_names.py",
            "scripts/tests/test_rename_secret_names.py", "scripts/check_secret_names.py", "scripts/tests/test_check_secret_names.py")}
        self.assertEqual([], self.problems(files))
        # and another plan is not
        self.assertEqual(1, len(self.problems({"docs/plans/28c-engines-on-runner.md": text})))

    def test_binary_files_are_skipped(self):
        self.assertEqual([], self.problems({"a.bin": b"\x00\x01dbdata:sync:connection:x\n"}))

    def test_git_lists_the_tracked_files_only(self):
        sys.path.insert(0, os.path.dirname(__file__))
        from gitenv import run_git  # noqa: E402
        from pathlib import Path
        root = tree({"tracked.md": "dbdata:sync:connection:x\n", "untracked.md": "dbdata:sync:connection:x\n"})
        run_git(Path(root), "init", "-q")
        run_git(Path(root), "add", "tracked.md")
        problems = check.check(root)
        self.assertEqual(1, len(problems))
        self.assertTrue(problems[0].startswith("tracked.md:1: "), problems[0])

    def test_the_repository_is_clean(self):
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
        self.assertEqual([], check.check(root))


if __name__ == "__main__":
    unittest.main()
