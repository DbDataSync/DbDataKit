"""SC31c: the secret-name rename script rewrites `dbdata:<service>:` to `dbdatakit:<service>:` (console is server, proxy is gateway, kit is the console's identity), the
segments composed in code and the names as they show in environment variables and credential files, leaves what tells history and the message of an app's frame alone,
stops on a frozen file, a dry run touches nothing and a second run does nothing."""
import contextlib
import io
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import rename_secret_names as rename  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
from gitenv import run_git  # noqa: E402

FILES = {
    "a/Names.cs": (
        'const string A = "dbdata:sync:connection:erp"; const string B = "secret:dbdata:build:git:sales";\n'
        'var c = $"dbdata:{Product}:{Kind}:"; var d = "dbdata:"; // dbdata:&lt;product&gt;:&lt;kind&gt;:&lt;name&gt;[:&lt;part&gt;]\n'
        'public const string Root = "dbdata";\n'
        'var s = new SecretName("console", "state", n); var m = new("console", "model-provider", p, "key"); var g = new SecretName("proxy", "datasource", "erp");\n'
        'var k = new("kit", "console", "signing-key"); var ok = new SecretName("sync", "connection", "x");\n'),
    "a/names.md": (
        "`dbdata:console:state:<name>`, `dbdata:proxy:datasource` and `dbdata:kit:console:signing-key`; `dbdata:<product>:<kind>`.\n"
        "Variable `DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_ORDERS`, `DBDATAKIT_SECRET_DBDATA_CONSOLE_STATE_X`, file `dbdata_gateway_datasource_erp_password`.\n"
        "Not names: dbdata_roles, `{dbdata: 'app/1'}`, postMessage({dbdata:'app/1'}), type R = { dbdata: string }, dbdatakit:server:x, console:state.\n"),
    "docs/ci-lessons.md": "the lesson names `dbdata:build:connection:x:read`\n",
    "docs/plans/33-fewer-services.md": "old `dbdata:console:` new `dbdatakit:server:`\n",
    "docs/plans/RELEASE-NOTES-PENDING.md": "- unreleased `dbdata:sync:config:x`\n<!-- secret-names:begin -->\n| `dbdata:console:state:x` | `dbdatakit:server:state:x` |\n<!-- secret-names:end -->\n",
    "Shared/tests/DbDataKit.State.Engines.Tests/SuiteEnvironmentTests.cs": '"DBDATA_SECRET_DBDATA_SYNC_CONNECTION_ORDERS", "DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_ORDERS"\n',
    "scripts/rename_proxy_to_gateway.py": 'SECRET_PREFIX = re.compile(r"dbdata:proxy:")\n',
    "scripts/rename_secret_names.py": "dbdata:console:\n",
    "bin/data.bin": b"\x00\x01dbdata:sync:connection:x",
}


def make(files=None, frozen=()):
    root = Path(tempfile.mkdtemp())
    entries = {"formats": [{"id": "f", "status": "frozen", "files": [{"path": p} for p in frozen]}]}
    for rel, text in {**(files or FILES), "docs/formats.json": json.dumps(entries)}.items():
        path = root / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_bytes(text if isinstance(text, bytes) else text.encode("utf-8"))
    run_git(root, "init", "-q")
    run_git(root, "add", "-A")
    return root


def read(root, rel):
    return (root / rel).read_text(encoding="utf-8")


class RenameSecretNames(unittest.TestCase):
    def setUp(self):
        quiet = contextlib.redirect_stdout(io.StringIO())
        quiet.__enter__()
        self.addCleanup(quiet.__exit__, None, None, None)

    def test_names_in_code_and_documents_are_rewritten(self):
        root = make()
        self.assertEqual(0, rename.run(root, dry_run=False))
        code = read(root, "a/Names.cs")
        self.assertIn('"dbdatakit:sync:connection:erp"', code)
        self.assertIn('"secret:dbdatakit:build:git:sales"', code)
        self.assertIn('$"dbdatakit:{Product}:{Kind}:"', code)
        self.assertIn('var d = "dbdatakit:";', code)
        self.assertIn("dbdatakit:&lt;service&gt;:&lt;kind&gt;", code)
        self.assertIn('Root = "dbdatakit";', code)

    def test_console_is_server_proxy_is_gateway_and_kit_is_the_identity_of_the_server(self):
        root = make()
        rename.run(root, dry_run=False)
        text = read(root, "a/names.md")
        self.assertIn("`dbdatakit:server:state:<name>`, `dbdatakit:gateway:datasource` and `dbdatakit:server:identity:signing-key`; `dbdatakit:<service>:<kind>`.", text)
        code = read(root, "a/Names.cs")
        self.assertIn('new SecretName("server", "state", n)', code)
        self.assertIn('new("server", "model-provider", p, "key")', code)
        self.assertIn('new SecretName("gateway", "datasource", "erp")', code)
        self.assertIn('new("server", "identity", "signing-key")', code)
        self.assertIn('new SecretName("sync", "connection", "x")', code)

    def test_the_names_in_environment_variables_and_credential_files_follow(self):
        root = make()
        rename.run(root, dry_run=False)
        text = read(root, "a/names.md")
        self.assertIn("`DBDATAKIT_SECRET_DBDATAKIT_SYNC_CONNECTION_ORDERS`", text)
        self.assertIn("`DBDATAKIT_SECRET_DBDATAKIT_SERVER_STATE_X`", text)
        self.assertIn("`dbdatakit_gateway_datasource_erp_password`", text)

    def test_what_is_not_a_secret_is_left_alone(self):
        root = make()
        rename.run(root, dry_run=False)
        text = read(root, "a/names.md")
        self.assertIn("dbdata_roles, `{dbdata: 'app/1'}`, postMessage({dbdata:'app/1'}), type R = { dbdata: string }, dbdatakit:server:x, console:state.", text)
        self.assertEqual(b"\x00\x01dbdata:sync:connection:x", (root / "bin/data.bin").read_bytes())

    def test_history_and_the_tests_of_the_refusal_are_kept(self):
        root = make()
        rename.run(root, dry_run=False)
        for rel in ("docs/ci-lessons.md", "docs/plans/33-fewer-services.md", "scripts/rename_proxy_to_gateway.py", "scripts/rename_secret_names.py",
                    "Shared/tests/DbDataKit.State.Engines.Tests/SuiteEnvironmentTests.cs"):
            self.assertEqual(FILES[rel], read(root, rel), rel)

    def test_the_release_notes_are_rewritten_except_the_table_of_old_and_new_names(self):
        root = make()
        rename.run(root, dry_run=False)
        self.assertEqual(
            "- unreleased `dbdatakit:sync:config:x`\n<!-- secret-names:begin -->\n| `dbdata:console:state:x` | `dbdatakit:server:state:x` |\n<!-- secret-names:end -->\n",
            read(root, "docs/plans/RELEASE-NOTES-PENDING.md"))

    def test_dry_run_touches_nothing(self):
        root = make()
        self.assertEqual(0, rename.run(root, dry_run=True))
        self.assertEqual(FILES["a/Names.cs"], read(root, "a/Names.cs"))

    def test_a_second_run_does_nothing(self):
        root = make()
        rename.run(root, dry_run=False)
        once = {p: read(root, p) for p in ("a/Names.cs", "a/names.md")}
        self.assertEqual(0, rename.run(root, dry_run=False))
        self.assertEqual(once, {p: read(root, p) for p in once})
        self.assertEqual({}, rename.plan(root))

    def test_a_frozen_file_with_an_old_name_stops_the_run_before_anything_changes(self):
        files = {**FILES, "formats/frozen.json": '{"secret": "secret:dbdata:sync:connection:x"}\n'}
        root = make(files, frozen=["formats/frozen.json"])
        self.assertEqual(1, rename.run(root, dry_run=False))
        self.assertEqual(files["a/Names.cs"], read(root, "a/Names.cs"))
        self.assertEqual(files["formats/frozen.json"], read(root, "formats/frozen.json"))


if __name__ == "__main__":
    unittest.main()
