import os
import subprocess
import sys
import tempfile
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import check_native_cache as check  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))

CACHE_ACTION = """name: cache
runs:
  using: composite
  steps:
    - uses: actions/cache/restore@v4
      with:
        key: native-${{ hashFiles('DbDataBuild/scripts/polyglot.pin') }}
    - run: DbDataBuild/scripts/fetch-native.sh --archives .native-archives
    - uses: actions/cache/save@v4
    - run: echo "POLYGLOT_RELEASE_BASE=file:///x" >> "$GITHUB_ENV"
"""
GOOD_DOCKERFILE = "COPY DbDataBuild/scripts/polyglot.pin .native-archive[s] /native-archives/\nRUN POLYGLOT_RELEASE_BASE=file:///native-archives scripts/fetch-native.sh\n"


def tree(files):
    root = tempfile.mkdtemp()
    base = {
        ".github/actions/native-cache/action.yml": CACHE_ACTION,
        "App/Dockerfile": GOOD_DOCKERFILE,
    }
    base.update(files)
    for rel, text in base.items():
        path = os.path.join(root, rel)
        os.makedirs(os.path.dirname(path), exist_ok=True)
        with open(path, "w", encoding="utf-8") as f:
            f.write(text)
    return root


def workflow(steps, header=""):
    return f"name: t\n{header}on: push\njobs:\n  one:\n    runs-on: ubuntu-latest\n    steps:\n{steps}"


CHECKOUT = "      - uses: actions/checkout@v7\n"
CACHE = "      - uses: ./.github/actions/native-cache\n"
FETCH = "      - name: Fetch\n        run: scripts/fetch-native.sh\n"


class CheckNativeCache(unittest.TestCase):
    def problems(self, steps, header="", **files):
        return check.check(tree({".github/workflows/t.yml": workflow(steps, header), **files}))

    def test_a_job_with_the_cache_before_the_fetch_passes(self):
        self.assertEqual([], self.problems(CHECKOUT + CACHE + FETCH))

    def test_a_fetch_without_the_cache_fails_and_names_job_and_step(self):
        problems = self.problems(CHECKOUT + FETCH)
        self.assertEqual(1, len(problems))
        self.assertIn("job one, step 2", problems[0])

    def test_the_cache_after_the_fetch_is_too_late(self):
        self.assertEqual(1, len(self.problems(CHECKOUT + FETCH + CACHE)))

    def test_setting_the_variable_by_hand_passes(self):
        step = "      - name: Fetch\n        env:\n          POLYGLOT_RELEASE_BASE: file:///x\n        run: scripts/fetch-native.sh\n"
        self.assertEqual([], self.problems(CHECKOUT + step))
        self.assertEqual([], self.problems(CHECKOUT + FETCH, header="env:\n  POLYGLOT_RELEASE_BASE: file:///x\n"))

    def test_a_docker_build_of_a_dockerfile_that_fetches_the_library_needs_the_cache(self):
        build = "      - name: Image\n        run: docker build -f App/Dockerfile -t app .\n"
        self.assertEqual(1, len(self.problems(CHECKOUT + build)))
        self.assertEqual([], self.problems(CHECKOUT + CACHE + build))
        other = "      - name: Image\n        run: docker build -f Other/Dockerfile -t other .\n"
        self.assertEqual([], self.problems(CHECKOUT + other, **{"Other/Dockerfile": "FROM scratch\n"}))

    def test_a_comment_in_a_run_block_is_not_a_command(self):
        step = "      - name: Note\n        run: |\n          # scripts/fetch-native.sh is not run here\n          echo hi\n"
        self.assertEqual([], self.problems(CHECKOUT + step))

    def test_a_compose_file_that_builds_such_a_dockerfile_needs_the_cache(self):
        compose = "services:\n  app:\n    build:\n      context: .\n      dockerfile: App/Dockerfile\n"
        step = "      - name: Up\n        run: dist/compose/check.sh dist/compose/app.yml app\n"
        files = {"dist/compose/app.yml": compose}
        self.assertEqual(1, len(self.problems(CHECKOUT + step, **files)))
        self.assertEqual([], self.problems(CHECKOUT + CACHE + step, **files))

    def test_a_dockerfile_that_downloads_the_library_itself_fails(self):
        problems = check.check(tree({"App/Dockerfile": "RUN scripts/fetch-native.sh\n"}))
        self.assertEqual(1, len(problems))
        self.assertIn("App/Dockerfile", problems[0])

    def test_the_actions_must_keep_the_pin_hash_in_the_key(self):
        problems = check.check(tree({".github/actions/native-cache/action.yml": CACHE_ACTION.replace("hashFiles('DbDataBuild/scripts/polyglot.pin')", "'x'")}))
        self.assertEqual(1, len(problems))
        self.assertIn("hash of the pin", problems[0])

    def test_this_repository_passes(self):
        result = subprocess.run([sys.executable, "-I", os.path.join(ROOT, "scripts", "check_native_cache.py"), ROOT], capture_output=True, text=True)
        self.assertEqual(0, result.returncode, result.stdout)


if __name__ == "__main__":
    unittest.main()
