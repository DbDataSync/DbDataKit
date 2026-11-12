import json
import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_compatibility as check  # noqa: E402


def write(root, rel, text):
    path = os.path.join(root, *rel.split("/"))
    os.makedirs(os.path.dirname(path), exist_ok=True)
    with open(path, "w", encoding="utf-8") as f:
        f.write(text)


def generation_cs(n):
    return f"public static class SuiteCompatibility {{ public const int Generation = {n}; }}\n"


def formats_json(**formats):
    return json.dumps({"formats": [{"id": k, "current": v[0], "previous": v[1]} for k, v in formats.items()]})


def current(generation=3, **formats):
    root = tempfile.mkdtemp()
    write(root, check.GENERATION_FILE, generation_cs(generation))
    write(root, check.FORMATS_FILE, formats_json(**(formats or {"model": (2, 1)})))
    write(root, check.SUITE_FILE, json.dumps({"members": [{"product": "dbdatakit"}, {"product": "dbdatagateway"}]}))
    return root


def previous(**releases):
    """releases: product -> (tag, generation or None, formats or None)."""
    folder = tempfile.mkdtemp()
    for product, (tag, generation, formats) in releases.items():
        write(folder, f"{product}/tag", tag)
        if generation is not None:
            write(folder, f"{product}/SuiteCompatibility.cs", generation_cs(generation))
        if formats is not None:
            write(folder, f"{product}/formats.json", formats_json(**formats))
    return folder


class CompatibilityTests(unittest.TestCase):
    def test_a_previous_release_one_generation_behind_passes_and_every_pair_is_in_the_matrix(self):
        matrix, problems = check.build(current(3), previous(dbdatakit=("dbdatakit/v2026.12.1.100", 2, {"model": (2, 1)})))
        self.assertEqual([], problems)
        row = matrix[0]
        self.assertEqual(6, len(row["pairs"]))
        self.assertEqual({"ok"}, {p["verdict"] for p in row["pairs"]})
        self.assertIn("console N with server N-1", [p["pair"] for p in row["pairs"]])
        self.assertIn("Flight SQL client N-1 with serving layer N", [p["pair"] for p in row["pairs"]])
        self.assertEqual([{"format": "model", "writes": 2, "reads": [1, 2], "verdict": "ok"}], row["formats"])
        self.assertEqual("no stable release yet: nothing to run against", matrix[1]["note"])

    def test_a_stub_two_generations_behind_fails_naming_the_pair(self):
        _, problems = check.build(current(3), previous(dbdatagateway=("dbdatagateway/v2026.12.1.100", 1, None)))
        self.assertEqual(6, len(problems))
        self.assertIn("dbdatagateway: serving protocols, Flight SQL client N-1 with serving layer N", "\n".join(problems))
        self.assertIn("generations 3 and 1, 2 apart", problems[0])

    def test_a_stub_two_generations_ahead_fails_too(self):
        _, problems = check.build(current(1), previous(dbdatakit=("dbdatakit/v2026.12.1.100", 3, None)))
        self.assertTrue(problems)

    def test_a_release_that_predates_the_generation_is_in_range(self):
        matrix, problems = check.build(current(5), previous(dbdatakit=("dbdatakit/v2026.11.6.837", None, None)))
        self.assertEqual([], problems)
        self.assertIn("predates the generation", matrix[0]["pairs"][0]["verdict"])

    def test_a_format_the_build_no_longer_reads_fails_naming_it(self):
        _, problems = check.build(current(2, model=(3, 2)), previous(dbdatakit=("dbdatakit/v2026.12.1.100", 2, {"model": (1, 1)})))
        self.assertEqual(1, len(problems))
        self.assertIn("format model: the release dbdatakit/v2026.12.1.100 writes format 1 and this build reads 2 to 3", problems[0])

    def test_the_matrix_is_generated_from_the_registry(self):
        matrix, _ = check.build(current(2, a=(1, 1), b=(2, 1)), previous(dbdatakit=("dbdatakit/v2026.12.1.100", 2, {"a": (1, 1)})))
        self.assertEqual(["a", "b"], [f["format"] for f in matrix[0]["formats"]])
        self.assertIn("no entry", matrix[0]["formats"][1]["verdict"])

    def test_the_latest_stable_tag_ignores_betas_and_snapshots_and_reads_the_source_at_it(self):
        root = tempfile.mkdtemp()
        env = {**os.environ, "GIT_AUTHOR_NAME": "t", "GIT_AUTHOR_EMAIL": "t@t", "GIT_COMMITTER_NAME": "t", "GIT_COMMITTER_EMAIL": "t@t"}

        def run(*args):
            subprocess.run(["git", "-C", root, "-c", "commit.gpgsign=false", *args], check=True, capture_output=True, env=env)

        run("init", "-q")
        write(root, check.GENERATION_FILE, generation_cs(2))
        run("add", "-A")
        run("commit", "-q", "-m", "one")
        for tag in ("dbdatakit/v2026.11.6.837", "dbdatakit/v2026.10.1.100", "dbdatakit/v2026.12.9.100-beta", "dbdatakit/v2026.12.9.100-snapshot.gabc"):
            run("tag", tag)
        write(root, check.GENERATION_FILE, generation_cs(9))
        run("commit", "-qam", "two")
        self.assertEqual("dbdatakit/v2026.11.6.837", check.latest_stable_tag(root, "dbdatakit"))
        tag, generation, formats = check.previous_from_git(root, "dbdatakit")
        self.assertEqual((2, None), (generation, formats))
        self.assertIsNone(check.previous_from_git(root, "dbdatabuild"))

    def test_the_repository_passes(self):
        root = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
        _, problems = check.build(root)
        self.assertEqual([], problems)


if __name__ == "__main__":
    unittest.main()
