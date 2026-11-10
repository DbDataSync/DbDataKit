import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_default_ports as check  # noqa: E402

PATTERN = r'DefaultListen = "https://[^":]+:(\d+)"'


def write(root, rel, text):
    path = os.path.join(root, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


class DefaultPortsTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="ports-")
        self.addCleanup(lambda: __import__("shutil").rmtree(self.root, ignore_errors=True))

    def listeners(self):
        return [("A", "a.cs", PATTERN, 1), ("B", "b.cs", PATTERN, 1)]

    def test_the_repository_has_no_shared_default(self):
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        problems, seen = check.check(root)
        self.assertEqual([], problems)
        self.assertGreaterEqual(len(seen), 10)

    def test_distinct_ports_pass(self):
        write(self.root, "a.cs", 'public const string DefaultListen = "https://127.0.0.1:8001";')
        write(self.root, "b.cs", 'public const string DefaultListen = "https://127.0.0.1:8002";')
        problems, _ = check.check(self.root, self.listeners())
        self.assertEqual([], problems)

    def test_a_shared_port_fails_naming_both(self):
        write(self.root, "a.cs", 'public const string DefaultListen = "https://127.0.0.1:8001";')
        write(self.root, "b.cs", 'public const string DefaultListen = "https://localhost:8001";')
        problems, _ = check.check(self.root, self.listeners())
        self.assertEqual(1, len(problems))
        self.assertIn("8001", problems[0])
        self.assertIn("'A'", problems[0])
        self.assertIn("'B'", problems[0])

    def test_a_declaration_that_moved_fails(self):
        write(self.root, "a.cs", 'public const string DefaultListen = "https://127.0.0.1:8001";')
        write(self.root, "b.cs", "nothing here")
        problems, _ = check.check(self.root, self.listeners())
        self.assertEqual(1, len(problems))
        self.assertIn("no default port found", problems[0])

    def test_a_missing_file_fails(self):
        write(self.root, "a.cs", 'public const string DefaultListen = "https://127.0.0.1:8001";')
        problems, _ = check.check(self.root, self.listeners())
        self.assertEqual(1, len(problems))
        self.assertIn("does not exist", problems[0])


if __name__ == "__main__":
    unittest.main()
