import json
import os
import shutil
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_doc_inputs as check  # noqa: E402

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))


class Tree:
    def __init__(self, files):
        self.root = tempfile.mkdtemp()
        for path, text in files.items():
            self.write(path, text)

    def write(self, path, text):
        full = os.path.join(self.root, path)
        os.makedirs(os.path.dirname(full), exist_ok=True)
        with open(full, "w", encoding="utf-8") as f:
            f.write(text)

    def cleanup(self):
        shutil.rmtree(self.root, ignore_errors=True)


def module_tree(pages, order, package_docs=None, extra=None):
    files = {"Shared/web/workspace/package.json": json.dumps({"dbdatakit": {"docs": package_docs or {"id": "sync", "title": "DbDataSync", "dir": "../../../DbDataSync/docs"}}}),
             "DbDataSync/docs/order.txt": order}
    files.update({f"DbDataSync/docs/{slug}.md": text for slug, text in pages.items()})
    files.update(extra or {})
    return Tree(files)


class ModuleDocsTests(unittest.TestCase):
    def run_on(self, tree):
        try:
            return check.check_module_docs(tree.root)
        finally:
            tree.cleanup()

    def test_sound_docs_pass(self):
        tree = module_tree({"install": "# Install\n\n## Default folder\n\nSee [concepts](concepts.md#the-drain) and [below](#default-folder).\n",
                            "concepts": "# Concepts\n\n## The drain\n\n![a picture](../screenshots/shots/a.png)\n[home](../README.md) [repo](../../docs/x.md)\n"},
                           "# pages\ninstall: Install\nconcepts: Concepts\n", extra={"DbDataSync/screenshots/shots/a.png": "png"})
        self.assertEqual([], self.run_on(tree))

    def test_a_page_not_listed_and_a_listed_page_missing_are_named(self):
        problems = self.run_on(module_tree({"install": "# Install\n", "stray": "# Stray\n"}, "install: Install\nghost: Ghost\ninstall: Again\n"))
        text = "\n".join(problems)
        self.assertIn("stray.md is not listed", text)
        self.assertIn("lists ghost, which has no ghost.md", text)
        self.assertIn("install is listed twice", text)

    def test_a_missing_order_file_is_named(self):
        tree = Tree({"Shared/web/console/package.json": json.dumps({"dbdatakit": {"docs": {"id": "console", "title": "C", "dir": "../../../DbDataKit/docs"}}}),
                     "DbDataKit/docs/a.md": "# A\n"})
        self.assertIn("order.txt is missing", "\n".join(self.run_on(tree)))

    def test_a_broken_anchor_and_a_link_to_a_page_that_does_not_ship_are_named(self):
        problems = self.run_on(module_tree({"install": "# Install\n\n[x](concepts.md#nothing) [y](#nope) [z](other.md)\n", "concepts": "# Concepts\n\n## Something\n"},
                                           "install: Install\nconcepts: Concepts\n"))
        text = "\n".join(problems)
        self.assertIn("concepts.md#nothing names no heading of concepts.md", text)
        self.assertIn("the link #nope names no heading", text)
        self.assertIn("other.md names a page that does not ship", text)

    def test_links_inside_code_are_not_links(self):
        problems = self.run_on(module_tree({"install": "# Install\n\n```\n[x](ghost.md)\n```\n`[y](#nope)`\n"}, "install: Install\n"))
        self.assertEqual([], problems)

    def test_pictures_must_be_under_screenshots_and_exist(self):
        problems = self.run_on(module_tree({"install": "# Install\n![a](../screenshots/shots/gone.png)\n![b](img/b.png)\n"}, "install: Install\n"))
        text = "\n".join(problems)
        self.assertIn("../screenshots/shots/gone.png, which is not there", text)
        self.assertIn("img/b.png, which is not a picture under ../screenshots/", text)

    def test_a_bad_id_and_an_id_used_twice_are_named(self):
        tree = module_tree({"install": "# I\n"}, "install: I\n", package_docs={"id": "Sync!", "title": "S", "dir": "../../../DbDataSync/docs"},
                           extra={"Shared/web/console/package.json": json.dumps({"dbdatakit": {"docs": {"id": "Sync!", "title": "S", "dir": "../../../DbDataSync/docs"}}})})
        text = "\n".join(self.run_on(tree))
        self.assertIn("is not lower-case letters", text)
        self.assertIn("registered by more than one package", text)

    def test_heading_slugs_follow_github_slugger(self):
        slugs = check.heading_slugs("# Getting started\n## Bulk loading a table\n## Bulk loading a table\n### `tool install` (Linux)\n```\n# not a heading\n```\n")
        self.assertEqual({"getting-started", "bulk-loading-a-table", "bulk-loading-a-table-1", "tool-install-linux"}, slugs)


class SyncInstallTests(unittest.TestCase):
    SOURCE = ('    public static string DefaultToolDir =>\n        OperatingSystem.IsWindows()\n'
              '            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "DbDataSync")\n'
              '            : OperatingSystem.IsMacOS() ? "/usr/local/dbdatasync"\n            : "/opt/dbdatasync";\n')

    def run_on(self, doc):
        tree = Tree({"DbDataSync/src/DbDataSync.Cli/CliOptions.cs": self.SOURCE, "DbDataSync/docs/install.md": doc})
        try:
            return check.check_sync_install(tree.root)
        finally:
            tree.cleanup()

    def test_both_spellings_present_pass(self):
        self.assertEqual([], self.run_on("into `$env:ProgramFiles\\DbDataSync` on Windows or `/opt/dbdatasync` on Linux"))

    def test_a_missing_folder_is_named(self):
        problems = self.run_on("into `$env:ProgramFiles\\DbDataSync` on Windows or `/usr/lib/dbdatasync` on Linux")
        self.assertEqual(1, len(problems))
        self.assertIn("/opt/dbdatasync", problems[0])


ENGINE_DOC = """# Engine support

## By role

| Engine | Sync source | Gateway source |
| --- | --- | --- |
| DuckDB | yes | yes |
| ODBC | no | yes |

## What each says

### DuckDB

### ODBC
"""


class EngineSupportTests(unittest.TestCase):
    def run_on(self, doc=ENGINE_DOC, readme=None):
        files = {"docs/engine-support.md": doc}
        if readme is not None:
            files["DbDataGateway/README.md"] = readme
        tree = Tree(files)
        try:
            return check.check_engine_support(tree.root)
        finally:
            tree.cleanup()

    def readme(self, rows):
        return ("# Gateway\n<!-- engine-support:begin gateway -->\n| Engine | Gateway source |\n| --- | --- |\n" + rows +
                "\nnotes in [engine-support.md](../docs/engine-support.md).\n<!-- engine-support:end -->\n")

    def test_agreeing_files_pass(self):
        self.assertEqual([], self.run_on(readme=self.readme("| DuckDB | yes |\n| ODBC | yes |\n")))

    def test_a_readme_cell_that_differs_is_named(self):
        problems = self.run_on(readme=self.readme("| DuckDB | yes |\n| ODBC | no |\n"))
        self.assertEqual(1, len(problems))
        self.assertIn("ODBC / Gateway source is 'no'", problems[0])

    def test_missing_markers_and_sections_are_named(self):
        self.assertIn("engine-support:begin gateway", self.run_on(readme="# Gateway\n")[0])
        doc = ENGINE_DOC.replace("### ODBC\n", "### Oracle\n")
        text = "\n".join(self.run_on(doc=doc))
        self.assertIn("ODBC is in the table but has no", text)
        self.assertIn("\"### Oracle\" has no row", text)


class BuildDocsTests(unittest.TestCase):
    COMMANDS = ("# Command reference\n\n| Command | Purpose |\n|---|---|\n| [`project compile`](#project-compile) | x |\n\n"
                "## project compile\n\n`--project` `--offline`\n")
    SKILL = ("---\nname: dbdatabuild\ndescription: " + "d" * 120 + "\n---\n\nRun `dbdatabuild project compile --offline`, see DDB-101 and schemas/model.schema.json.\n"
             "If the dbdatabuild tools are available, the screens `plans`.\n\n```yaml\nname: marts.fct_orders\n```\n")
    TESTS = ('Assert.Contains("If the dbdatabuild tools are available", Skill);\n'
             'foreach (var screen in new[] { "plans" }) Assert.Contains($"`{screen}`", Skill);\n'
             'private static readonly string[] ConfigOnly = ["--allow-writes"];\n')

    def tree(self, skill=SKILL, commands=COMMANDS, design="## 6. Model definitions\n\n```yaml\nname: a\n```\n"):
        return Tree({"DbDataBuild/docs/commands.md": commands,
                     "DbDataBuild/src/DbDataBuild.Cli/AgentKit/SKILL.md": skill,
                     "DbDataBuild/src/DbDataBuild.Core/Diagnostics.cs": 'E("101", "YAML syntax error",',
                     "DbDataBuild/schemas/model.schema.json": "{}",
                     "DbDataBuild/tests/DbDataBuild.Tests.Unit/AgentKitTests.cs": self.TESTS,
                     "DbDataBuild/tests/DbDataBuild.Tests.Unit/SchemaConformanceTests.cs": 'var yaml = YamlBlockAfter("## 6. Model definitions");',
                     "DbDataBuild/DESIGN.md": design})

    def run_on(self, **kw):
        tree = self.tree(**kw)
        try:
            return check.check_command_reference(tree.root) + check.check_agent_kit(tree.root) + check.check_design_examples(tree.root)
        finally:
            tree.cleanup()

    def test_sound_documents_pass(self):
        self.assertEqual([], self.run_on())

    def test_an_unknown_code_option_schema_and_a_missing_sentence_are_named(self):
        skill = self.SKILL.replace("DDB-101", "DDB-999").replace("--offline", "--nowhere").replace("model.schema", "ghost.schema").replace("If the dbdatabuild tools are available", "Hello")
        text = "\n".join(self.run_on(skill=skill))
        for expected in ("DDB-999", "--nowhere", "schemas/ghost.schema.json", "AgentKitTests looks for"):
            self.assertIn(expected, text)

    def test_broken_front_matter_and_example_are_named(self):
        text = "\n".join(self.run_on(skill="# no front matter\n"))
        self.assertIn("front matter", text)
        self.assertIn("no ```yaml example", text)

    def test_the_command_index_and_sections_must_agree(self):
        text = "\n".join(self.run_on(commands=self.COMMANDS.replace("## project compile", "## project build")))
        self.assertIn("project compile is in the index but has no", text)
        self.assertIn("\"## project build\" is not in the index", text)

    def test_a_design_marker_that_moved_is_named(self):
        self.assertIn("is not there", "\n".join(self.run_on(design="## 6. Models\n")))


class PackedTests(unittest.TestCase):
    def test_a_packed_readme_and_a_copied_one_must_exist(self):
        tree = Tree({"Shared/src/A/A.csproj": '<None Include="README.md" Pack="true" PackagePath="/" />\n<PackageReadmeFile>README.md</PackageReadmeFile>',
                     "Shared/src/B/B.csproj": '<None Include="README.md" Pack="true" />', "Shared/src/B/README.md": "# B",
                     "DbDataKit/scripts/package.sh": 'cp ../LICENSE README.md "$STAGE/"\n', "LICENSE": "x"})
        try:
            problems = check.check_packed(tree.root)
        finally:
            tree.cleanup()
        self.assertEqual(2, len(problems), problems)
        self.assertTrue(any("Shared/src/A/A.csproj: includes README.md" in p for p in problems))
        self.assertTrue(any("DbDataKit/scripts/package.sh: copies README.md" in p for p in problems))


class RepositoryTests(unittest.TestCase):
    def test_the_repository_passes(self):
        self.assertEqual([], check.check(REPO))


if __name__ == "__main__":
    unittest.main()
