import os
import unittest

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


def read(rel):
    with open(os.path.join(ROOT, rel), encoding="utf-8") as f:
        return f.read()


class SkipJdbcTests(unittest.TestCase):
    """docs/testing-environment.md, "Building without JDBC": the projects it names keep their SkipJdbc conditions."""

    def test_the_root_props_turn_the_environment_variable_into_the_property(self):
        text = read("Directory.Build.props")
        self.assertIn("DBDATAKIT_TEST_SKIP_JDBC", text)
        self.assertIn("<SkipJdbc ", text)

    def test_the_jdbc_references_are_conditional(self):
        for rel in (
            "DbDataSync/src/DbDataSync.Api/DbDataSync.Api.csproj",
            "DbDataSync/src/DbDataSync.TaskRunner/DbDataSync.TaskRunner.csproj",
            "DbDataSync/tests/DbDataSync.Drivers.Jdbc.Tests/DbDataSync.Drivers.Jdbc.Tests.csproj",
        ):
            with self.subTest(rel):
                text = read(rel)
                self.assertRegex(
                    text,
                    r'<ProjectReference Condition="\'\$\(SkipJdbc\)\' != \'true\'" Include="[^"]*DbDataSync\.Drivers\.Jdbc\.csproj"',
                )

    def test_ikvm_is_only_referenced_when_not_skipped(self):
        for rel in (
            "DbDataSync/src/DbDataSync.Drivers.Jdbc/DbDataSync.Drivers.Jdbc.csproj",
            "DbDataSync/tests/DbDataSync.Api.Tests/DbDataSync.Api.Tests.csproj",
            "DbDataSync/tests/DbDataSync.Drivers.Jdbc.Tests/DbDataSync.Drivers.Jdbc.Tests.csproj",
        ):
            with self.subTest(rel):
                text = read(rel)
                self.assertRegex(text, r'<PackageReference Condition="\'\$\(SkipJdbc\)\' != \'true\'" Include="IKVM"')

    def test_the_projects_with_jdbc_dependent_code_define_skip_jdbc(self):
        for rel in (
            "DbDataSync/src/DbDataSync.Api/DbDataSync.Api.csproj",
            "DbDataSync/tests/DbDataSync.Api.Tests/DbDataSync.Api.Tests.csproj",
        ):
            with self.subTest(rel):
                text = read(rel)
                self.assertIn("SKIP_JDBC", text)
                self.assertIn("'$(SkipJdbc)' == 'true'", text)


if __name__ == "__main__":
    unittest.main()
