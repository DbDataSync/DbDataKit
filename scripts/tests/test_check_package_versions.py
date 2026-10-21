import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_package_versions as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


PACKAGES = '<Project><ItemGroup><PackageVersion Include="Npgsql" Version="10.0.3" /></ItemGroup></Project>'


class CheckPackageVersions(unittest.TestCase):
    def test_a_clean_tree_passes(self):
        root = tree({
            "Directory.Packages.props": PACKAGES,
            "A/A.csproj": '<Project><ItemGroup><PackageReference Include="Npgsql" /></ItemGroup></Project>',
        })
        self.assertEqual([], check.check(root))

    def test_a_version_on_a_reference_fails(self):
        root = tree({
            "Directory.Packages.props": PACKAGES,
            "A/A.csproj": '<Project><ItemGroup><PackageReference Include="Npgsql" Version="9.0.3" /></ItemGroup></Project>',
        })
        problems = check.check(root)
        self.assertEqual(1, len(problems))
        self.assertIn("A/A.csproj", problems[0].replace(os.sep, "/"))

    def test_a_version_on_a_multi_line_reference_fails(self):
        root = tree({
            "A/A.csproj": '<Project><ItemGroup><PackageReference\n  Include="Npgsql"\n  Version="9.0.3">\n<PrivateAssets>all</PrivateAssets></PackageReference></ItemGroup></Project>',
        })
        self.assertEqual(1, len(check.check(root)))

    def test_an_override_fails_unless_it_is_listed(self):
        root = tree({
            "A/A.csproj": '<Project><ItemGroup><PackageReference Include="Npgsql" VersionOverride="9.0.3" />'
                          '<PackageReference Include="Oracle.ManagedDataAccess.Core" VersionOverride="23.9.1" /></ItemGroup></Project>',
        })
        problems = check.check(root)
        self.assertEqual(1, len(problems))
        self.assertIn("Npgsql", problems[0])

    def test_a_package_listed_twice_or_with_two_versions_fails(self):
        root = tree({
            "Directory.Packages.props": PACKAGES.replace("</ItemGroup>", '<PackageVersion Include="npgsql" Version="10.0.3" /></ItemGroup>'),
            "P/Directory.Packages.props": PACKAGES.replace("10.0.3", "9.0.3"),
        })
        self.assertEqual(2, len(check.check(root)))

    def test_build_output_and_node_modules_are_not_scanned(self):
        root = tree({
            "node_modules/x/x.csproj": '<Project><PackageReference Include="Npgsql" Version="1" /></Project>',
            "A/obj/A.csproj": '<Project><PackageReference Include="Npgsql" Version="1" /></Project>',
        })
        self.assertEqual([], check.check(root))

    def test_the_repository_passes(self):
        here = os.path.dirname(os.path.abspath(__file__))
        self.assertEqual([], check.check(os.path.abspath(os.path.join(here, "..", ".."))))


if __name__ == "__main__":
    unittest.main()
