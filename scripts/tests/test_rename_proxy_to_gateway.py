"""The O14 rename script: moves with git, rewrites names, leaves history and HTTP proxies alone, dry run touches nothing, a second run does nothing."""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import rename_proxy_to_gateway as rename  # noqa: E402

FILES = {
    "DbDataProxy/DbDataProxy.slnf": '{"solution": {"projects": ["src\\\\DbDataProxy.Server\\\\DbDataProxy.Server.csproj"]}}\n',
    "DbDataProxy/src/DbDataProxy.Server/ProxyHost.cs": (
        "namespace DbDataProxy.Server;\npublic sealed class ProxyHost { public const string Program = \"dbdataproxy\"; }\n"
        "// the proxy listens\n// a reverse proxy in front sets X-Forwarded-For\n// a line about a reverse proxy only\n"
        "var env = \"DBPROXY_Proxy__Listen__Port\"; var secret = \"dbdata:proxy:state:session-key\";\n"
    ),
    "DbDataProxy/docs/review.md": "# DbDataProxy review (history)\n",
    "DbDataKit/web/proxy/package.json": '{"name": "@dbdatakit/proxy"}\n',
    "DbDataKit/web/package-lock.json": '{"workspaces": ["build",\n        "proxy",\n "x"], "node_modules/@dbdatakit/proxy": {"resolved": "proxy"}, "node_modules/proxy-from-env": {}}\n',
    "DbDataKit/web/package.json": '{\n  "workspaces": [\n    "build",\n    "proxy"\n  ]\n}\n',
    ".github/workflows/dbdataproxy-ci.yml": "name: DbDataProxy CI\npaths: '^(DbDataProxy/|DbDataKit/)'\n",
    "docs/plans/13-suite.md": "history: DbDataProxy did this in batch 3\n",
    "docs/guide.md": "Start the Proxy with `dbdataproxy server run`.\nBehind a reverse proxy set X-Forwarded-Proto.\n",
    "DbDataBuild/ServerCli.cs": "// server proxy add pairs Build with a gateway\n// the Proxy is DbDataProxy\n",
    "scripts/tests/test_rename_proxy_to_gateway.py": "keeps DbDataProxy\n",
}


def git(root: Path, *args: str) -> str:
    return subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=True).stdout


class RenameTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.root = Path(self.tmp.name)
        git(self.root, "init", "-q")
        git(self.root, "config", "user.email", "t@example.invalid")
        git(self.root, "config", "user.name", "t")
        for name, text in FILES.items():
            path = self.root / name
            path.parent.mkdir(parents=True, exist_ok=True)
            path.write_text(text, encoding="utf-8")
        git(self.root, "add", "-A")
        git(self.root, "commit", "-qm", "before")

    def tearDown(self):
        self.tmp.cleanup()

    def read(self, name: str) -> str:
        return (self.root / name).read_text(encoding="utf-8")

    def test_dry_run_lists_and_touches_nothing(self):
        before = git(self.root, "status", "--porcelain")
        self.assertEqual(rename.run(self.root, dry_run=True), 0)
        self.assertEqual(git(self.root, "status", "--porcelain"), before)
        self.assertTrue((self.root / "DbDataProxy").is_dir())

    def test_paths_move_with_git_and_names_change(self):
        rename.run(self.root, dry_run=False)
        self.assertFalse((self.root / "DbDataProxy").exists())
        self.assertTrue((self.root / "DbDataGateway/DbDataGateway.slnf").is_file())
        self.assertTrue((self.root / "DbDataGateway/src/DbDataGateway.Server/GatewayHost.cs").is_file())
        self.assertTrue((self.root / "DbDataKit/web/gateway/package.json").is_file())
        self.assertTrue((self.root / ".github/workflows/dbdatagateway-ci.yml").is_file())
        status = git(self.root, "status", "--porcelain")
        self.assertIn("DbDataProxy/DbDataProxy.slnf -> DbDataGateway/DbDataGateway.slnf", status)
        host = self.read("DbDataGateway/src/DbDataGateway.Server/GatewayHost.cs")
        self.assertIn("namespace DbDataGateway.Server;", host)
        self.assertIn("class GatewayHost", host)
        self.assertIn('"dbdatagateway"', host)
        self.assertIn("// the gateway listens", host)
        self.assertIn("DBGATEWAY_Gateway__Listen__Port", host)
        self.assertIn("dbdata:gateway:state:session-key", host)
        self.assertEqual(self.read(".github/workflows/dbdatagateway-ci.yml"), "name: DbDataGateway CI\npaths: '^(DbDataGateway/|DbDataKit/)'\n")
        self.assertIn("@dbdatakit/gateway", self.read("DbDataKit/web/gateway/package.json"))

    def test_http_proxies_history_and_other_products_vocabulary_are_left_alone(self):
        rename.run(self.root, dry_run=False)
        host = self.read("DbDataGateway/src/DbDataGateway.Server/GatewayHost.cs")
        self.assertIn("// a line about a reverse proxy only", host)
        self.assertIn("a reverse proxy in front sets X-Forwarded-For", host)
        self.assertEqual(self.read("DbDataGateway/docs/review.md"), "# DbDataProxy review (history)\n")
        self.assertEqual(self.read("docs/plans/13-suite.md"), "history: DbDataProxy did this in batch 3\n")
        self.assertEqual(self.read("scripts/tests/test_rename_proxy_to_gateway.py"), "keeps DbDataProxy\n")
        self.assertIn("Start the Gateway with `dbdatagateway server run`.\nBehind a reverse proxy set", self.read("docs/guide.md"))
        self.assertEqual(self.read("DbDataBuild/ServerCli.cs"), "// server proxy add pairs Build with a gateway\n// the Proxy is DbDataGateway\n")

    def test_the_workspace_files_change_only_the_workspace_and_package(self):
        rename.run(self.root, dry_run=False)
        lock = self.read("DbDataKit/web/package-lock.json")
        self.assertIn('"gateway",', lock)
        self.assertIn('"node_modules/@dbdatakit/gateway": {"resolved": "gateway"}', lock)
        self.assertIn("node_modules/proxy-from-env", lock)
        self.assertIn('"gateway"', self.read("DbDataKit/web/package.json"))

    def test_second_run_does_nothing(self):
        rename.run(self.root, dry_run=False)
        git(self.root, "add", "-A")
        snapshot = git(self.root, "status", "--porcelain")
        self.assertEqual(rename.run(self.root, dry_run=False), 0)
        self.assertEqual(git(self.root, "status", "--porcelain"), snapshot)
        moves, changes, _ = rename.plan(self.root)
        self.assertEqual((moves, changes), ([], {}))


if __name__ == "__main__":
    unittest.main()
