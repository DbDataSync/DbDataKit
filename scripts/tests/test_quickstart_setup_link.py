"""Plan 23a FQ23: the quickstart brings the first administrator in with the setup link, and no longer tells the reader to go and find FIRST-RUN.txt."""
import os
import re
import unittest

ROOT = os.path.join(os.path.dirname(__file__), "..", "..")


def read(rel):
    with open(os.path.join(ROOT, rel), encoding="utf-8") as f:
        return f.read()


class QuickstartSetupLinkTests(unittest.TestCase):
    def test_the_quickstart_makes_the_setup_link_with_the_command_and_does_not_send_the_reader_to_a_file(self):
        text = read("docs/quickstart.md")
        self.assertIn("dbdatakit.dll setup link", text)
        self.assertNotIn("FIRST-RUN.txt", text)

    def test_the_command_the_quickstart_names_is_one_the_console_has(self):
        source = read("DbDataKit/src/DbDataKit.Server/ConsoleCli.cs")
        self.assertRegex(source, r'new\("setup",')
        self.assertRegex(source, r'new Verb\("link",')

    def test_the_setup_checklist_the_quickstart_mentions_is_a_page_of_the_console(self):
        self.assertIn("**Setup**", read("docs/quickstart.md"))
        app = read("Shared/web/console/src/App.tsx")
        self.assertTrue(re.search(r'path="/setup"', app))


if __name__ == "__main__":
    unittest.main()
