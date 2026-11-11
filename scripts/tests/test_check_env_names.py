import os
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_env_names as check  # noqa: E402


def tree(files):
    root = tempfile.mkdtemp()
    for rel, text in files.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "wb") as f:
            f.write(text if isinstance(text, bytes) else text.encode("utf-8"))
    return root


class CheckEnvNames(unittest.TestCase):
    def problems(self, files):
        return check.check(tree(files))

    def test_the_new_scheme_passes(self):
        self.assertEqual([], self.problems({
            "a.cs": 'Environment.GetEnvironmentVariable("DBDATAKIT_DATA_RUNNER"); // DBDATAKIT_Gateway__Management__Port, DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_ORDERS\n',
            "b.md": "`DBDATAKIT_BUILD_WAREHOUSE_READ` and `DBDATAKIT_STATE_STORE_ENGINE`\n",
        }))

    def test_every_product_prefix_is_found_with_its_file_and_line(self):
        for name in ("DBDATASTORE_X", "DBDATARUNNER_X", "DBDATABUILD_X", "DBDATAWORKSPACE_X", "DBDATASYNC_X", "DBDATAGATEWAY_X", "DBDATAPROXY_X",
                     "DBGATEWAY_Gateway__Listen__Port", "DBPROXY_Proxy__Listen__Port", "DBDATA_SECRET_X", "DBDATA_BUILD_X_READ", "DBKIT_ANYTHING"):
            with self.subTest(name):
                problems = self.problems({"src/a.cs": f'ok\nvar x = "{name}";\n'})
                self.assertEqual(1, len(problems), problems)
                self.assertTrue(problems[0].startswith("src/a.cs:2: "), problems[0])
                self.assertIn(name, problems[0])

    def test_a_name_is_found_in_any_kind_of_text_file(self):
        for path in ("docs/a.md", "dist/compose/a.yml", "x/run.sh", ".github/workflows/w.yml", "web/a.ts", "Dockerfile"):
            with self.subTest(path):
                self.assertEqual(1, len(self.problems({path: "  DBGATEWAY_PASSWORD: x\n"})))

    def test_the_name_of_a_secret_inside_a_variable_is_not_a_variable(self):
        self.assertEqual([], self.problems({"a.md": "DBDATAKIT_SECRET_DBDATA_SYNC_CONNECTION_ORDERS and MY_DBDATA_X and xDBGATEWAY_Y\n"}))

    def test_the_old_variables_of_tests_are_not_allowed_any_more(self):
        for name in ("DBDATA_TEST_POSTGRES", "DBKIT_TEST_MSSQL", "DBDATASYNC_TEST_MSSQL_SERVER", "DBDATABUILD_TEST_FLIGHT", "DBPROXY_TEST_", "DBGATEWAY_TEST_SQLSERVER",
                     "DBDATA_E2E_PG", "DBGATEWAY_E2E_ADDRESS", "DBDATA_CONFORMANCE_MYSQL", "DBDATA_REQUIRE_POSTGRES", "DBGATEWAY_REQUIRE_SQLSERVER", "DBDATASYNC_MATRIX_UPDATE",
                     "DBDATASYNC_HARNESS_TABLES", "DBDATASYNC_DUCKDB_ENGINE_SOURCE", "DBDATA_UPDATE_GOLDEN", "DBDATA_BENCH_MAX_ROWS", "DBGATEWAY_LIVE_USER",
                     "DBGATEWAY_ADMIN_TESTS", "DBDATA_APP_STORE"):
            with self.subTest(name):
                self.assertEqual(1, len(self.problems({"a.md": name + "\n"})))

    def test_the_variables_of_tests_are_dbdatakit_test(self):
        self.assertEqual([], self.problems({"a.md": "DBDATAKIT_TEST_POSTGRES DBDATAKIT_TEST_REQUIRE_SQLSERVER DBDATAKIT_TEST_E2E_PG DBDATAKIT_TEST_PULL\n"}))

    def test_a_product_setting_that_looks_like_a_test_variable_is_not_allowed(self):
        self.assertEqual(1, len(self.problems({"a.md": "DBDATARUNNER_CHANNELS and DBDATAKIT_TEST_X\n"})))
        self.assertEqual(1, len(self.problems({"a.md": "DBGATEWAY_PASSWORD\n"})))

    def test_the_files_that_hold_the_refusal_list_and_the_notes_are_exempt(self):
        text = "DBGATEWAY_PASSWORD DBDATA_SECRET_X\n"
        files = {path: text for path in (
            "DbDataKit/src/DbDataKit.State.Engines/SuiteEnvironment.cs",
            "DbDataKit/tests/DbDataKit.State.Engines.Tests/SuiteEnvironmentTests.cs",
            "DbDataRunner/tests/DbDataRunner.Tests/SuiteEnvironmentRefusalTests.cs",
            "docs/plans/RELEASE-NOTES-PENDING.md", "docs/plans/33-fewer-services.md", "docs/ci-lessons.md", "docs/testing-environment.md")}
        self.assertEqual([], self.problems(files))
        # and another plan is not
        self.assertEqual(2, len(self.problems({"docs/plans/28c-engines-on-runner.md": text})))

    def test_binary_files_are_skipped(self):
        self.assertEqual([], self.problems({"a.bin": b"\x00\x01DBGATEWAY_PASSWORD\n"}))

    def test_git_lists_the_tracked_files_only(self):
        sys.path.insert(0, os.path.dirname(__file__))
        from gitenv import run_git  # noqa: E402
        from pathlib import Path
        root = tree({"tracked.md": "DBGATEWAY_PASSWORD\n", "untracked.md": "DBGATEWAY_PASSWORD\n"})
        run_git(Path(root), "init", "-q")
        run_git(Path(root), "add", "tracked.md")
        problems = check.check(root)
        self.assertEqual(1, len(problems))
        self.assertTrue(problems[0].startswith("tracked.md:1: "), problems[0])

    def test_the_repository_is_clean(self):
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
        self.assertEqual([], check.check(root))


if __name__ == "__main__":
    unittest.main()
