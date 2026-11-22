import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_default_ports as check  # noqa: E402

PATTERN = r'DefaultListen = "https://[^":]+:(\d+)"'
TABLE = check.TABLE

BLOCKS = """
    public static PortRange SuitePorts { get; } = new(7800, 7899);
    public static PortRange ConsolePorts { get; } = new(7800, 7819);
    public static PortRange RunnerPorts { get; } = new(7840, 7859);
"""


def write(root, rel, text):
    path = os.path.join(root, rel)
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


def listen(port):
    return f'public const string DefaultListen = "https://127.0.0.1:{port}";'


class DefaultPortsTests(unittest.TestCase):
    def setUp(self):
        self.root = tempfile.mkdtemp(prefix="ports-")
        self.addCleanup(lambda: __import__("shutil").rmtree(self.root, ignore_errors=True))
        write(self.root, TABLE, BLOCKS)

    def listeners(self, a="Console", b="Runner"):
        return [("A", "a.cs", PATTERN, 1, a), ("B", "b.cs", PATTERN, 1, b)]

    def test_the_repository_passes(self):
        root = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
        problems, seen = check.check(root)
        self.assertEqual([], problems)
        self.assertGreaterEqual(len(seen), 4)

    def test_defaults_in_their_sub_blocks_pass(self):
        write(self.root, "a.cs", listen(7800))
        write(self.root, "b.cs", listen(7840))
        problems, _ = check.check(self.root, self.listeners(), gateway=[])
        self.assertEqual([], problems)

    def test_a_shared_port_fails_naming_both(self):
        write(self.root, "a.cs", listen(7801))
        write(self.root, "b.cs", 'public const string DefaultListen = "https://localhost:7801";')
        problems, _ = check.check(self.root, self.listeners(b="Console"), gateway=[])
        self.assertEqual(1, len(problems))
        self.assertIn("7801", problems[0])
        self.assertIn("'A'", problems[0])
        self.assertIn("'B'", problems[0])

    def test_a_default_outside_the_range_fails(self):
        write(self.root, "a.cs", listen(8480))
        write(self.root, "b.cs", listen(7840))
        problems, _ = check.check(self.root, self.listeners(), gateway=[])
        self.assertEqual(["'A' defaults to 8480, outside its sub-block Console (7800 to 7819)"], problems)

    def test_a_default_in_another_services_sub_block_fails(self):
        write(self.root, "a.cs", listen(7841))
        write(self.root, "b.cs", listen(7840))
        problems, _ = check.check(self.root, self.listeners(), gateway=[])
        self.assertEqual(["'A' defaults to 7841, outside its sub-block Console (7800 to 7819)"], problems)

    def test_two_overlapping_sub_blocks_fail(self):
        write(self.root, TABLE, BLOCKS + "    public static PortRange WorkspacePorts { get; } = new(7855, 7879);\n")
        problems, _ = check.check(self.root, [], gateway=[])
        self.assertEqual(["the sub-blocks Runner (7840 to 7859) and Workspace (7855 to 7879) overlap"], problems)

    def test_a_sub_block_outside_the_range_fails(self):
        write(self.root, TABLE, BLOCKS + "    public static PortRange SparePorts { get; } = new(7890, 7910);\n")
        problems, _ = check.check(self.root, [], gateway=[])
        self.assertEqual(["the sub-block Spare (7890 to 7910) lies outside the suite range 7800 to 7899"], problems)

    def test_a_default_that_is_a_windows_default_port_fails(self):
        write(self.root, "a.cs", listen(5985))
        write(self.root, "b.cs", listen(7840))
        problems, _ = check.check(self.root, [("A", "a.cs", PATTERN, 1, None), ("B", "b.cs", PATTERN, 1, "Runner")], gateway=[])
        self.assertEqual(["'A' defaults to 5985, one of Windows' default ports"], problems)

    def test_a_range_that_holds_a_windows_default_port_fails(self):
        write(self.root, TABLE, "public static PortRange SuitePorts { get; } = new(7600, 7699);")
        problems, _ = check.check(self.root, [], gateway=[])
        self.assertEqual(["the suite range 7600 to 7699 holds Windows' default port 7680"], problems)

    def test_a_listener_with_no_sub_block_is_checked_only_for_sharing_and_windows_ports(self):
        write(self.root, "a.cs", listen(8815))
        write(self.root, "b.cs", listen(7840))
        problems, _ = check.check(self.root, self.listeners(a=None), gateway=[])
        self.assertEqual([], problems)

    def test_a_gateway_listener_with_a_fixed_default_fails_and_one_without_passes(self):
        gateway = [("G", "g.cs", r"Port \{ get; set; \} = (\d+);", "Runner")]
        write(self.root, "g.cs", "public int Port { get; set; } = 8815;")
        problems, _ = check.check(self.root, [], gateway=gateway)
        self.assertEqual(1, len(problems))
        self.assertIn("gives a listener a fixed default again", problems[0])
        write(self.root, "g.cs", "public List<string> Listen { get; set; } = [];")
        self.assertEqual([], check.check(self.root, [], gateway=gateway)[0])

    def test_a_gateway_block_that_is_not_declared_fails(self):
        write(self.root, "g.cs", "nothing")
        problems, _ = check.check(self.root, [], gateway=[("G", "g.cs", r"x", "Gateway")])
        self.assertEqual(1, len(problems))
        self.assertIn("the sub-block Gateway is not declared", problems[0])

    def test_a_declaration_that_moved_fails(self):
        write(self.root, "a.cs", listen(7800))
        write(self.root, "b.cs", "nothing here")
        problems, _ = check.check(self.root, self.listeners(), gateway=[])
        self.assertEqual(1, len(problems))
        self.assertIn("no default port found", problems[0])

    def test_a_missing_file_fails(self):
        write(self.root, "a.cs", listen(7800))
        problems, _ = check.check(self.root, self.listeners(), gateway=[])
        self.assertEqual(1, len(problems))
        self.assertIn("does not exist", problems[0])

    def test_a_table_without_the_suite_range_fails(self):
        write(self.root, TABLE, "public static PortRange RunnerPorts { get; } = new(7840, 7859);")
        problems, _ = check.check(self.root, [], gateway=[])
        self.assertEqual(1, len(problems))
        self.assertIn("no SuitePorts declaration", problems[0])


if __name__ == "__main__":
    unittest.main()
