import os
import re
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import ci_paths  # noqa: E402

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
WORKFLOWS = os.path.join(REPO, ".github", "workflows")

# Changes that start no product suite, and changes that must.
DOCS_ONLY = ["planning/ROLES.md", "planning/wip/X/X-1.md", ".claude/agents/reviewer.md", "docs/ci-lessons.md", "docs/formats.json",
             "docs/examples/excel-hello/taskpane.js", "CLAUDE.md", "DbDataBuild/DESIGN.md", "Shared/src/DbDataKit.Store/README.md",
             "DbDataKit/docs/order.txt", "DbDataSync/docs/install.md", "DbDataBuild/docs/research/function-matrix.csv",
             "DbDataBuild/src/DbDataBuild.Cli/AgentKit/SKILL.md", "DbDataGateway/docs/review.md"]
CODE = ["DbDataBuild/src/DbDataBuild.Cli/CliApp.cs", "Shared/web/shell/src/docs/DocsViewer.tsx", "Shared/src/DbDataKit.Store/Store.cs",
        "Directory.Packages.props", ".github/workflows/dbdatakit-ci.yml", "testenv/docker-compose.yml"]


def workflow(name):
    with open(os.path.join(WORKFLOWS, name), encoding="utf-8") as f:
        return f.read()


class GlobTests(unittest.TestCase):
    def test_patterns_match_as_github_does(self):
        self.assertTrue(ci_paths.matches(["**.md"], "CLAUDE.md"))
        self.assertTrue(ci_paths.matches(["**.md"], "a/b/c.md"))
        self.assertTrue(ci_paths.matches(["docs/**"], "docs/a/b.json"))
        self.assertFalse(ci_paths.matches(["docs/**"], "Shared/web/shell/src/docs/x.ts"))
        self.assertTrue(ci_paths.matches(["**/docs/**"], "Shared/web/shell/src/docs/x.ts"))
        self.assertTrue(ci_paths.matches(["**/*.cs"], "a.cs"))
        self.assertFalse(ci_paths.matches(["scripts/fetch-duckdb.*"], "scripts/x/fetch-duckdb.sh"))
        self.assertTrue(ci_paths.matches([".claude/**"], ".claude/agents/worker.md"))

    def test_paths_with_negations_take_the_last_match(self):
        paths = ["Shared/**", "!**.md"]
        self.assertFalse(ci_paths.would_run(paths=paths, changed=["Shared/README.md"]))
        self.assertTrue(ci_paths.would_run(paths=paths, changed=["Shared/README.md", "Shared/src/a.cs"]))
        self.assertFalse(ci_paths.would_run(paths=paths, changed=["planning/a.md"]))


class WorkflowFilterTests(unittest.TestCase):
    def test_every_product_ci_ignores_the_same_paths_on_both_triggers(self):
        for name in ci_paths.PRODUCT_WORKFLOWS:
            text = workflow(name)
            for event in ("push", "pull_request"):
                self.assertEqual(ci_paths.PRODUCT_IGNORE, ci_paths.trigger_filter(text, event, "paths-ignore"), f"{name} {event}")

    def test_no_other_workflow_runs_on_every_push_and_pull_request_unfiltered_except_the_fast_ones(self):
        # A new CI workflow that triggers on main without a path filter would run its suite for a planning merge: add it to PRODUCT_WORKFLOWS (or here, if it is fast).
        fast = {"planning-wip.yml", "repo-checks.yml", "backup-branch-cleanup.yml", "delete-merged-branch.yml"}
        for name in sorted(os.listdir(WORKFLOWS)):
            if not name.endswith(".yml") or name in fast or name in ci_paths.PRODUCT_WORKFLOWS:
                continue
            text = workflow(name)
            for event in ("push", "pull_request"):
                if not re.search(rf"^  {event}:", text, re.M):
                    continue
                filtered = ci_paths.trigger_filter(text, event, "paths") or ci_paths.trigger_filter(text, event, "paths-ignore")
                self.assertTrue(filtered, f"{name} runs on every {event}: give it the product filter (scripts/ci_paths.py)")

    def test_docs_only_changes_start_no_product_suite(self):
        for name in ci_paths.PRODUCT_WORKFLOWS + ["shared-ci.yml", "dist-ci.yml", "dist-msi-ci.yml"]:
            text = workflow(name)
            for event in ("push", "pull_request"):
                paths = ci_paths.trigger_filter(text, event, "paths")
                ignore = ci_paths.trigger_filter(text, event, "paths-ignore")
                if paths is None and ignore is None:
                    continue                     # dist-msi-ci has no push trigger
                for f in DOCS_ONLY:
                    self.assertFalse(ci_paths.would_run(paths, ignore, [f]), f"{name} {event} starts for {f}")

    def test_code_changes_still_start_the_product_suites(self):
        for name in ci_paths.PRODUCT_WORKFLOWS:
            text = workflow(name)
            for event in ("push", "pull_request"):
                ignore = ci_paths.trigger_filter(text, event, "paths-ignore")
                for f in CODE:
                    self.assertTrue(ci_paths.would_run(None, ignore, [f]), f"{name} {event} skips {f}")
                self.assertTrue(ci_paths.would_run(None, ignore, ["planning/ROLES.md", CODE[0]]))
        shared = workflow("shared-ci.yml")
        for event in ("push", "pull_request"):
            self.assertTrue(ci_paths.would_run(ci_paths.trigger_filter(shared, event, "paths"), None, ["Shared/src/DbDataKit.Store/Store.cs"]))
            # a shared test that reads another product's source runs when that source changes
            for scanned in ["DbDataSync/src/DbDataSync.TaskRunner/Runner/SetPreflight.cs", "DbDataBuild/src/DbDataBuild.Execution/MutationGate.cs",
                            "DbDataGateway/src/DbDataGateway.Server/Data/GatewaySources.cs"]:
                self.assertTrue(ci_paths.would_run(ci_paths.trigger_filter(shared, event, "paths"), None, [scanned]), f"Shared CI {event} skips {scanned}")

    def test_repository_checks_and_planning_wip_start_for_docs_only_changes(self):
        checks = workflow("repo-checks.yml")
        for event in ("push", "pull_request"):
            paths = ci_paths.trigger_filter(checks, event, "paths")
            for f in DOCS_ONLY:
                self.assertTrue(ci_paths.would_run(paths, None, [f]), f"Repository checks {event} skips {f}")
        wip = workflow("planning-wip.yml")
        for event in ("push", "pull_request"):
            self.assertIsNone(ci_paths.trigger_filter(wip, event, "paths"))
            self.assertIsNone(ci_paths.trigger_filter(wip, event, "paths-ignore"))

    def test_repository_checks_skips_its_builds_for_exactly_what_the_products_ignore(self):
        m = re.search(r"^      ignore: '(.*)'$", workflow("repo-checks.yml"), re.M)
        self.assertIsNotNone(m)
        expression = re.compile(m.group(1))
        for f in DOCS_ONLY:
            self.assertTrue(expression.search(f), f)
        for f in CODE:
            self.assertFalse(expression.search(f), f)

    def test_the_release_guards_use_the_script(self):
        for name in ["dbdatagateway-release.yml", "dbdatarunner-release.yml", "dbdatastore-release.yml", "dbdatasync-release.yml", "dbdataworkspace-release.yml", "suite-release.yml"]:
            text = workflow(name)
            self.assertIn("scripts/ci_paths.py require-green --workflow .github/workflows/", text, name)
            self.assertNotIn("head_sha=$SHA&status=success", text, name)
            for target in re.findall(r"require-green --workflow \.github/workflows/([\w.-]+)", text):
                self.assertIn(target, ci_paths.PRODUCT_WORKFLOWS, name)


class TriggerFilterTests(unittest.TestCase):
    TEXT = """name: X CI

on:
  # a comment
  pull_request:
    branches: [main]
    paths-ignore:
      - 'planning/**'
      - "**.md"
  push:
    branches: [main]
    paths:
      - 'src/**'
  workflow_dispatch:
    inputs:
      paths:
        description: not a filter

jobs:
  a:
    paths-ignore:
      - 'no'
"""

    def test_reads_the_list_of_one_trigger(self):
        self.assertEqual("X CI", ci_paths.workflow_name(self.TEXT))
        self.assertEqual(["planning/**", "**.md"], ci_paths.trigger_filter(self.TEXT, "pull_request", "paths-ignore"))
        self.assertIsNone(ci_paths.trigger_filter(self.TEXT, "push", "paths-ignore"))
        self.assertEqual(["src/**"], ci_paths.trigger_filter(self.TEXT, "push", "paths"))
        self.assertIsNone(ci_paths.trigger_filter(self.TEXT, "workflow_dispatch", "paths"))


class FindGreenTests(unittest.TestCase):
    """History c1 <- c2 <- c3 <- c4 (first parents); `changes` is what each commit changed."""

    def run_walk(self, green, changes, sha="c4"):
        order = ["c1", "c2", "c3", "c4"]

        def parent(c):
            i = order.index(c)
            return order[i - 1] if i else None

        def changed_between(a, b):
            files = []
            for c in order[order.index(a) + 1: order.index(b) + 1]:
                files += changes[c]
            return files

        return ci_paths.find_green(sha, ci_paths.PRODUCT_IGNORE, lambda c: c in green, changed_between, parent)

    def test_a_commit_with_its_own_green_run_passes(self):
        self.assertEqual(("c4", None), self.run_walk({"c4"}, {"c2": [], "c3": [], "c4": ["a.cs"]}))

    def test_docs_only_commits_after_a_green_one_are_covered_by_it(self):
        self.assertEqual(("c2", None), self.run_walk({"c2"}, {"c2": ["a.cs"], "c3": ["planning/A.md"], "c4": ["docs/x.md", "DbDataSync/docs/order.txt"]}))

    def test_an_untested_code_change_in_between_fails(self):
        found, reason = self.run_walk({"c1"}, {"c2": ["a.cs"], "c3": [], "c4": ["planning/A.md"]})
        self.assertIsNone(found)
        self.assertIn("a.cs", reason)

    def test_no_green_run_at_all_fails(self):
        found, reason = self.run_walk(set(), {"c2": [], "c3": [], "c4": ["planning/A.md"]})
        self.assertIsNone(found)
        self.assertIn("no commit before it", reason)


if __name__ == "__main__":
    unittest.main()
