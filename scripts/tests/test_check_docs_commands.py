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


SHARED = {
    "DbDataKit/src/DbDataKit.Cli/Nouns/ServerNoun.cs": """
public static class ServerNoun {
    public static Noun Create(Func<string[], CliContext, Task<int>> run) => new("server", "The long-running process",
    [
        new Verb("run", "Run the server", EffectClass.OfflineOnly, "[options]", run),
    ]);
}
""",
    "DbDataKit/src/DbDataKit.Web/PairingVerbs.cs": """
public static class PairingVerbs {
    public static Verb[] Create(Func<string[], PairingFiles> files) =>
    [
        new Verb("pair", "Make a code", EffectClass.RepoFilesOnly, "", null),
        new Verb("unpair", "Remove one", EffectClass.RepoFilesOnly, "", null),
    ];
}
""",
    "DbDataConsole/src/DbDataConsole.Server/ConsoleCli.cs": """
public static class ConsoleCli {
    static NounRouter Router() => new("dbdatakit", [ ServerNoun.Create(Run).With([.. PairingVerbs.Create(f)]), InstanceNoun(), UserNoun() ]);
    private static Noun InstanceNoun() => new("instance", "The servers this console manages",
    [
        new Verb("add", "Pair", EffectClass.RepoFilesOnly, "", null),
        new Verb("rotate-key", "Rotate", EffectClass.RepoFilesOnly, "", null),
    ]);
    private static Noun UserNoun() => new("user", "The people",
    [
        Forward("invite", "Print an invitation, with \\"quotes\\"", EffectClass.RepoFilesOnly, "", null),
    ]);
}
""",
    "DbDataBuild/src/DbDataBuild.Cli/Program.cs": "static class P { }\n",
    "DbDataBuild/docs/commands.md": "| Command | Purpose |\n|---|---|\n| [`project show graph`](#project-show-graph) | `P` | x |\n",
}


class CommandsThatExist(unittest.TestCase):
    def tree(self, extra):
        return tree({**SHARED, **extra})

    def problems(self, text, path="DbDataConsole/README.md"):
        return check.check_exist(self.tree({path: text}))

    def test_the_commands_are_read_from_the_sources(self):
        root = self.tree({})
        self.assertEqual({("server", "run"), ("server", "pair"), ("server", "unpair"), ("instance", "add"), ("instance", "rotate-key"), ("user", "invite")},
                         check.commands_of(root, "dbdatakit"))
        self.assertIn(("project", "show", "graph"), check.commands_of(root, "dbdatabuild"))

    def test_a_command_that_exists_passes(self):
        text = "```\ndbdatakit server run --listen https://x\ndbdatakit instance add https://b --code c\n```\nOr `dbdatakit user invite`, `dbdatakit instance` and `dbdatakit.exe server pair`.\n"
        self.assertEqual([], self.problems(text))

    def test_a_build_command_of_three_words_passes(self):
        self.assertEqual([], self.problems("`dbdatabuild project show graph --format json`\n", "DbDataBuild/README.md"))

    def test_a_verb_that_does_not_exist_fails_and_names_the_ones_that_do(self):
        problems = self.problems("`dbdatakit user remove bob`\n")
        self.assertEqual(1, len(problems))
        self.assertIn("DbDataConsole/README.md:1", problems[0])
        self.assertIn("dbdatakit user remove", problems[0])
        self.assertIn("invite", problems[0])

    def test_a_noun_that_does_not_exist_fails(self):
        self.assertEqual(1, len(self.problems("```\ndbdatakit console start\n```\n")))
        self.assertEqual(1, len(self.problems("`dbdatabuild ui web`\n", "DbDataBuild/README.md")))

    def test_prose_outside_code_is_not_a_command(self):
        self.assertEqual([], self.problems("dbdatakit manages the servers, and dbdatakit console is a phrase in a sentence.\n"))
        self.assertEqual([], self.problems("```\nRun dbdatakit with the options you like.\n```\n"))

    def test_the_marker_lets_a_line_name_a_command_that_is_gone(self):
        self.assertEqual([], self.problems("`dbdatakit console start` <!-- retired-name-ok -->\n"))
        self.assertEqual([], self.problems("<!-- retired-name-ok -->\n`dbdatakit console start`\n"))

    def test_history_and_fixtures_are_skipped(self):
        files = {"docs/plans/13-x.md": "`dbdatakit console start`\n", "DbDataConsole/tests/Fixture/a.md": "`dbdatakit nope`\n", "DbDataConsole/architecture/p.md": "`dbdatakit nope`\n"}
        self.assertEqual([], check.check_exist(self.tree(files)))

    def test_a_product_without_sources_is_not_audited(self):
        self.assertEqual([], check.check_exist(tree({"DbDataProxy/README.md": "`dbdataproxy nothing here`\n"})))

    def test_a_product_whose_commands_cannot_be_read_is_reported(self):
        problems = check.check_exist(tree({"DbDataStore/src/x.cs": "class X {}\n"}))
        self.assertEqual(1, len(problems))
        self.assertIn("DbDataStore", problems[0])

    def test_the_consoles_readme_must_show_every_noun(self):
        problems = check.check_readmes(self.tree({"DbDataConsole/README.md": "`dbdatakit server run`\n`dbdatakit user invite`\n"}))
        self.assertEqual(["DbDataConsole/README.md: the command line has `dbdatakit instance` and the README does not show it"], problems)
        self.assertEqual([], check.check_readmes(self.tree({"DbDataConsole/README.md": "dbdatakit server run\ndbdatakit user invite\ndbdatakit instance add\n"})))

    def test_the_repository_itself_passes(self):
        self.assertEqual([], check.check(os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))))


if __name__ == "__main__":
    unittest.main()
