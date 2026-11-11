"""SC30: the folder rename script moves DbDataKit/ to Shared/ and then DbDataConsole/ to DbDataKit/, rewrites the paths and project names (and only those),
leaves the machine folder, the GitHub repository, the product name and the frozen files alone, dry run touches nothing, a second run does nothing."""
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))
import rename_to_shared_and_dbdatakit as rename  # noqa: E402

sys.path.insert(0, str(Path(__file__).resolve().parent))
from gitenv import run_git  # noqa: E402

FROZEN = "DbDataKit/src/DbDataKit.Config/schemas/frozen.json"

FILES = {
    "DbDataKit.slnx": '<Solution>\n  <Folder Name="/DbDataConsole/src/">\n    <Project Path="DbDataConsole/src/DbDataConsole.Server/DbDataConsole.Server.csproj" />\n'
                      '  </Folder>\n  <Folder Name="/DbDataKit/src/">\n    <Project Path="DbDataKit/src/DbDataKit.Config/DbDataKit.Config.csproj" />\n  </Folder>\n</Solution>\n',
    "DbDataKit/DbDataKit.Shared.slnx": "<Solution />\n",
    "DbDataKit/global.json": "{}\n",
    "DbDataKit/README.md": "# DbDataKit/\n",
    "DbDataKit/src/DbDataKit.Config/DbDataKit.Config.csproj": "<Project />\n",
    FROZEN: '{"see": "DbDataKit/src/x"}\n',
    "DbDataKit/web/package.json": '{"workspaces": ["console"]}\n',
    "DbDataKit/tests/DbDataKit.Web.Tests/UsesPaths.cs": (
        'var web = Path.Combine(root, "DbDataKit", "web");\nvar sample = Path.Combine(root, "..", "DbDataKit", "src", "x");\n'
        'var machine = Path.Combine(programData, "DbDataKit", name);\nvar server = new AuthProduct("DbDataKit");\n'),
    "DbDataConsole/DbDataConsole.slnf": '{"solution": {"path": "..\\\\DbDataKit.slnx", "projects": ["src\\\\DbDataConsole.Server\\\\DbDataConsole.Server.csproj"]}}\n',
    "DbDataConsole/Directory.Build.props": (
        "<DbDataKitRoot>$([MSBuild]::NormalizeDirectory('$(MSBuildThisFileDirectory)', '..', 'DbDataKit'))</DbDataKitRoot>\n"
        '<!-- DbDataConsole/ and DbDataKit/ (the shared libraries) are siblings -->\n'),
    "DbDataConsole/src/DbDataConsole.Server/DbDataConsole.Server.csproj": (
        '<Project><ItemGroup><ProjectReference Include="..\\..\\..\\DbDataKit\\src\\DbDataKit.Config\\DbDataKit.Config.csproj" />\n'
        '<InternalsVisibleTo Include="DbDataConsole.Tests" /></ItemGroup></Project>\n'),
    "DbDataConsole/src/DbDataConsole.Server/Host.cs": (
        "namespace DbDataConsole.Server;\npublic sealed class Host { const string Log = \"DbDataConsole.Runner\"; }\n"
        '// "DbDataConsole" is a prose word in a source file and stays\n'),
    "DbDataConsole/tests/DbDataConsole.Tests/DbDataConsole.Tests.csproj": '<Project />\n',
    "DbDataConsole/tests/DbDataConsole.Servers.Tests/A.cs": "using DbDataConsole.Server;\n",
    "DbDataConsole/tests/DbDataConsole.Web.Tests/tests/a.spec.ts": "path.join(repoRoot, 'DbDataConsole/src/DbDataConsole.Server/bin/dbdatakit.dll')\n",
    ".github/workflows/dbdataconsole-ci.yml": (
        "name: DbDataConsole CI\n"
        "      paths: '^(DbDataConsole/|DbDataKit/|DbDataBuild/|Directory\\.Build\\.props)'\n"
        "    working-directory: DbDataConsole\n"
        "      - run: dotnet build DbDataConsole.slnf -c Release\n"
        "      - run: dotnet test --project tests/DbDataConsole.Servers.Tests\n"
        "          product: DbDataConsole\n"),
    ".github/workflows/dbdatakit-ci.yml": (
        'name: DbDataKit CI\non:\n  paths:\n    - "DbDataKit/**"\n    working-directory: DbDataKit\n'
        "      cache-dependency-path: DbDataKit/**/*.csproj\n"
        "      | grep -E '^DbDataKit/src/DbDataKit\\.Semantic/Emit/X\\.cs$'\n"),
    ".github/workflows/suite-release.yml": "select(.name == \"DbDataConsole CI\") and meta: DbDataConsole/dist.json\n",
    "DbDataBuild/tests/B.csproj": '<Compile Include="..\\..\\..\\DbDataKit\\tests\\DbDataKit.Config.Git.Tests\\A.cs" />\n',
    "DbDataGateway/dist.json": (
        '{"homepage": "https://github.com/DbDataSync/DbDataKit/tree/main/DbDataGateway", "repo": "DbDataSync/DbDataKit", "notes": "%ProgramData%\\\\DbDataKit\\\\gateway\\\\config"}\n'),
    "DbDataConsole/dist.json": '{"homepage": "https://github.com/DbDataSync/DbDataKit/tree/main/DbDataConsole"}\n',
    "dist/tests/t.py": 'PRODUCTS = {"DbDataConsole": "dbdatakit"}\nsrc = "DbDataKit/src/x"  # the build needs DbDataKit/ too\n',
    "docs/guide.md": (
        "Open `DbDataKit/src/DbDataKit.Config/README.md`. The console is DbDataConsole in prose (SC32 changes it) and `DbDataConsole/README.md` is a path.\n"
        "Data lives in `%ProgramData%\\DbDataKit\\runner` and `/Library/Application Support/DbDataKit/sync`; the shared DbDataKit/ code.\n"),
    "docs/formats.json": json.dumps({"formats": [{"id": "f", "status": "frozen", "files": [{"path": FROZEN, "sha256": "x"}]}]}) + "\n",
    "docs/plans/33-fewer-services.md": "`DbDataKit/` to `Shared/`, then `DbDataConsole/` to `DbDataKit/`\n",
    "scripts/tests/test_rename_to_shared_and_dbdatakit.py": "keeps DbDataKit/src and DbDataConsole/\n",
}


def git(root: Path, *args: str) -> str:
    return run_git(root, *args)


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
        self.assertTrue((self.root / "DbDataConsole").is_dir())
        self.assertFalse((self.root / "Shared").exists())

    def test_the_two_folders_and_the_projects_move_with_git(self):
        self.assertEqual(rename.run(self.root, dry_run=False), 0)
        self.assertFalse((self.root / "DbDataConsole").exists())
        self.assertTrue((self.root / "Shared/src/DbDataKit.Config/DbDataKit.Config.csproj").is_file())
        self.assertTrue((self.root / "Shared/DbDataKit.Shared.slnx").is_file())
        self.assertTrue((self.root / "DbDataKit/DbDataKit.slnf").is_file())
        self.assertTrue((self.root / "DbDataKit/src/DbDataKit.Server/DbDataKit.Server.csproj").is_file())
        self.assertTrue((self.root / "DbDataKit/tests/DbDataKit.Server.Tests/DbDataKit.Server.Tests.csproj").is_file())
        self.assertTrue((self.root / "DbDataKit/tests/DbDataKit.Server.Servers.Tests/A.cs").is_file())
        self.assertTrue((self.root / "DbDataKit/tests/DbDataKit.Server.Web.Tests/tests/a.spec.ts").is_file())
        git(self.root, "add", "-A")
        status = git(self.root, "status", "--porcelain")
        # a moved file keeps its history (git follows renames of files whose content is the same, as the frozen ones are)
        self.assertIn("DbDataKit/src/DbDataKit.Config/DbDataKit.Config.csproj -> Shared/src/DbDataKit.Config/DbDataKit.Config.csproj", status)
        self.assertIn("DbDataKit/src/DbDataKit.Config/schemas/frozen.json -> Shared/src/DbDataKit.Config/schemas/frozen.json", status)
        self.assertIn("DbDataConsole/dist.json -> DbDataKit/dist.json", status)

    def test_solution_filter_props_and_projects_name_the_new_folders(self):
        rename.run(self.root, dry_run=False)
        slnx = self.read("DbDataKit.slnx")
        self.assertIn('<Folder Name="/DbDataKit/src/">', slnx)
        self.assertIn('Path="DbDataKit/src/DbDataKit.Server/DbDataKit.Server.csproj"', slnx)
        self.assertIn('<Folder Name="/Shared/src/">', slnx)
        self.assertIn('Path="Shared/src/DbDataKit.Config/DbDataKit.Config.csproj"', slnx)
        self.assertIn('"src\\\\DbDataKit.Server\\\\DbDataKit.Server.csproj"', self.read("DbDataKit/DbDataKit.slnf"))
        self.assertIn("'..', 'Shared'", self.read("DbDataKit/Directory.Build.props"))
        self.assertIn("DbDataKit/ and Shared/ (the shared libraries) are siblings", self.read("DbDataKit/Directory.Build.props"))
        project = self.read("DbDataKit/src/DbDataKit.Server/DbDataKit.Server.csproj")
        self.assertIn("..\\..\\..\\Shared\\src\\DbDataKit.Config\\DbDataKit.Config.csproj", project)
        self.assertIn('InternalsVisibleTo Include="DbDataKit.Server.Tests"', project)
        self.assertIn("..\\..\\..\\Shared\\tests\\DbDataKit.Config.Git.Tests", self.read("DbDataBuild/tests/B.csproj"))
        host = self.read("DbDataKit/src/DbDataKit.Server/Host.cs")
        self.assertIn("namespace DbDataKit.Server;", host)
        self.assertIn('"DbDataKit.Server.Runner"', host)
        self.assertIn('// "DbDataConsole" is a prose word', host)
        self.assertIn("using DbDataKit.Server;", self.read("DbDataKit/tests/DbDataKit.Server.Servers.Tests/A.cs"))
        self.assertIn("'DbDataKit/src/DbDataKit.Server/bin/dbdatakit.dll'", self.read("DbDataKit/tests/DbDataKit.Server.Web.Tests/tests/a.spec.ts"))

    def test_code_paths_built_from_quoted_segments(self):
        rename.run(self.root, dry_run=False)
        text = self.read("Shared/tests/DbDataKit.Web.Tests/UsesPaths.cs")
        self.assertIn('Path.Combine(root, "Shared", "web")', text)
        self.assertIn('Path.Combine(root, "..", "Shared", "src", "x")', text)
        self.assertIn('Path.Combine(programData, "DbDataKit", name)', text)   # the machine folder
        self.assertIn('new AuthProduct("DbDataKit")', text)                   # the product name

    def test_workflows(self):
        rename.run(self.root, dry_run=False)
        console = self.read(".github/workflows/dbdataconsole-ci.yml")
        self.assertIn("name: DbDataConsole CI\n", console)                    # other workflows select runs by this name
        self.assertIn("paths: '^(DbDataKit/|Shared/|DbDataBuild/|Directory\\.Build\\.props)'", console)
        self.assertIn("working-directory: DbDataKit\n", console)
        self.assertIn("dotnet build DbDataKit.slnf -c Release", console)
        self.assertIn("tests/DbDataKit.Server.Servers.Tests", console)
        self.assertIn("product: DbDataKit\n", console)
        libraries = self.read(".github/workflows/dbdatakit-ci.yml")
        self.assertIn('- "Shared/**"', libraries)
        self.assertIn("working-directory: Shared\n", libraries)
        self.assertIn("cache-dependency-path: Shared/**/*.csproj", libraries)
        self.assertIn("'^Shared/src/DbDataKit\\.Semantic/Emit/X\\.cs$'", libraries)
        self.assertEqual(self.read(".github/workflows/suite-release.yml"), 'select(.name == "DbDataConsole CI") and meta: DbDataKit/dist.json\n')

    def test_the_github_repository_and_the_machine_folder_keep_their_names(self):
        rename.run(self.root, dry_run=False)
        self.assertEqual(self.read("DbDataGateway/dist.json"),
                         '{"homepage": "https://github.com/DbDataSync/DbDataKit/tree/main/DbDataGateway", "repo": "DbDataSync/DbDataKit", "notes": "%ProgramData%\\\\DbDataKit\\\\gateway\\\\config"}\n')
        self.assertEqual(self.read("DbDataKit/dist.json"), '{"homepage": "https://github.com/DbDataSync/DbDataKit/tree/main/DbDataKit"}\n')
        guide = self.read("docs/guide.md")
        self.assertIn("`Shared/src/DbDataKit.Config/README.md`", guide)
        self.assertIn("DbDataConsole in prose", guide)                         # prose keeps the word (SC32)
        self.assertIn("`DbDataKit/README.md` is a path", guide)
        self.assertIn("%ProgramData%\\DbDataKit\\runner", guide)
        self.assertIn("/Library/Application Support/DbDataKit/sync", guide)
        self.assertIn("the Shared/ code", guide)
        self.assertIn('PRODUCTS = {"DbDataKit": "dbdatakit"}', self.read("dist/tests/t.py"))
        self.assertIn('src = "Shared/src/x"  # the build needs Shared/ too', self.read("dist/tests/t.py"))

    def test_frozen_files_plan_33_and_the_scripts_tests_are_not_edited(self):
        rename.run(self.root, dry_run=False)
        self.assertEqual(self.read("Shared/src/DbDataKit.Config/schemas/frozen.json"), '{"see": "DbDataKit/src/x"}\n')
        self.assertEqual(self.read("docs/plans/33-fewer-services.md"), "`DbDataKit/` to `Shared/`, then `DbDataConsole/` to `DbDataKit/`\n")
        self.assertEqual(self.read("scripts/tests/test_rename_to_shared_and_dbdatakit.py"), "keeps DbDataKit/src and DbDataConsole/\n")
        self.assertIn('"path": "Shared/src/DbDataKit.Config/schemas/frozen.json"', self.read("docs/formats.json"))

    def test_second_run_does_nothing(self):
        rename.run(self.root, dry_run=False)
        git(self.root, "add", "-A")
        snapshot = git(self.root, "status", "--porcelain")
        self.assertEqual(rename.run(self.root, dry_run=False), 0)
        self.assertEqual(git(self.root, "status", "--porcelain"), snapshot)
        self.assertTrue(rename.already_done(self.root))


if __name__ == "__main__":
    unittest.main()
