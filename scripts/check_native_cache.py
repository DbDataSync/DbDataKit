#!/usr/bin/env python3
"""Every workflow job that needs the polyglot-sql library reads it from the cache (docs/ci-lessons.md, "GitHub release downloads fail").

The sibling of check_duckdb_cache.py, for the Build native library that DbDataBuild/scripts/fetch-native.sh downloads from a pinned upstream release.
No job downloads the pinned archives itself: the action .github/actions/native-cache restores them from the Actions cache (keyed on the hash of
DbDataBuild/scripts/polyglot.pin) or downloads them once with retries, and sets POLYGLOT_RELEASE_BASE for the rest of the job. This fails when

  * a job has a step that needs the library (runs fetch-native.sh, or builds a Docker image or compose file whose Dockerfile fetches it) and no earlier
    step of the job uses the native-cache action, and the job, the workflow or the step does not set POLYGLOT_RELEASE_BASE itself,
  * a Dockerfile fetches the library without taking it from the build context's .native-archives (POLYGLOT_RELEASE_BASE), or
  * the action is not as the rule needs it: native-cache keys on the hash of the pin, runs fetch-native.sh --archives, saves a miss and exports
    POLYGLOT_RELEASE_BASE.

Run from anywhere: python3 scripts/check_native_cache.py [root]
"""
import os
import re
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from check_duckdb_cache import DOCKER_COMMANDS, read, rel, split_job_steps, split_jobs, walk  # noqa: E402

CACHE_ACTION = "./.github/actions/native-cache"
FETCHING_COMMANDS = re.compile(r"fetch-native")
VARIABLE = "POLYGLOT_RELEASE_BASE"


def native_dockerfiles(root):
    """Dockerfiles (as paths relative to the root) that fetch the library."""
    found = {}
    for path in walk(root, lambda n: n == "Dockerfile" or n.startswith("Dockerfile.") and not n.endswith(".dockerignore")):
        text = read(path)
        if FETCHING_COMMANDS.search(text):
            found[rel(root, path)] = text
    return found


def native_compose_files(root, dockerfiles):
    """Compose files (relative) that build one of those Dockerfiles."""
    found = set()
    for path in walk(root, lambda n: n.endswith((".yml", ".yaml"))):
        r = rel(root, path)
        if r.startswith(".github/"):
            continue
        text = read(path)
        if re.search(r"^\s*build:", text, re.M) and any(re.search(r"dockerfile:\s*" + re.escape(d) + r"\s*$", text, re.M) for d in dockerfiles):
            found.add(r)
    return found


def step_needs_native(step, dockerfiles, compose_files):
    """Why this step needs the library, or None."""
    uses = re.search(r"^\s*-?\s*uses:\s*(\S+)", step, re.M)
    if uses and uses.group(1).startswith("docker/build-push-action"):
        target = re.search(r"^\s*file:\s*(\S+)", step, re.M)
        if target and target.group(1) in dockerfiles:
            return f"it builds {target.group(1)}, which fetches the polyglot library"
        return None
    run = re.search(r"^\s*(?:-\s*)?run:\s*(.*)$", step, re.M)
    if not run:
        return None
    command = re.sub(r"#.*", "", step[run.start():])  # comments in a run block are not commands
    if FETCHING_COMMANDS.search(command):
        return "it runs fetch-native.sh"
    if DOCKER_COMMANDS.search(command):
        if "matrix.dockerfile" in command:
            return "it builds a Dockerfile named by the matrix"
        for d in dockerfiles:
            if d in command:
                return f"it builds {d}, which fetches the polyglot library"
        for c in compose_files:
            if c in command:
                return f"it starts {c}, which builds an image that fetches the polyglot library"
        if "dist/compose/check.sh" in command and compose_files:
            return "it starts a compose file whose images fetch the polyglot library"
    return None


def check_action(root):
    cache = os.path.join(root, ".github/actions/native-cache/action.yml")
    if not os.path.exists(cache):
        return [".github/actions/native-cache/action.yml: missing"]
    text = read(cache)
    problems = []
    for needle, why in (
        ("hashFiles('DbDataBuild/scripts/polyglot.pin')", "the cache key must contain the hash of the pin, or a changed pin could be served from an old cache"),
        ("fetch-native.sh --archives", "the archives are fetched (hash-checked) by fetch-native.sh --archives"),
        (VARIABLE + "=", f"it must export {VARIABLE} for the jobs' fetches"),
        ("actions/cache/save", "a miss must be saved"),
    ):
        if needle not in text:
            problems.append(f".github/actions/native-cache/action.yml: missing `{needle}`: {why}")
    return problems


def check(root):
    problems = check_action(root)
    dockerfiles = native_dockerfiles(root)
    for path, text in sorted(dockerfiles.items()):
        if VARIABLE not in text or ".native-archive" not in text:
            problems.append(f"{path}: fetches the polyglot library without taking it from the build context's .native-archives "
                            f"(COPY DbDataBuild/scripts/polyglot.pin .native-archive[s] /native-archives/ and {VARIABLE}=file:///native-archives)")
    compose_files = native_compose_files(root, dockerfiles)
    workflows = os.path.join(root, ".github", "workflows")
    if not os.path.isdir(workflows):
        return problems
    for name in sorted(os.listdir(workflows)):
        if not name.endswith((".yml", ".yaml")):
            continue
        header, jobs = split_jobs(read(os.path.join(workflows, name)))
        if VARIABLE in header:
            continue
        for job, job_text in sorted(jobs.items()):
            job_header, steps = split_job_steps(job_text)
            if VARIABLE in job_header:
                continue
            cached = False
            for number, step in enumerate(steps, 1):
                uses = re.search(r"^\s*-?\s*uses:\s*(\S+)", step, re.M)
                if uses and uses.group(1) == CACHE_ACTION:
                    cached = True
                    continue
                why = step_needs_native(step, dockerfiles, compose_files)
                if why is None or cached or VARIABLE in step:
                    continue
                problems.append(f".github/workflows/{name}: job {job}, step {number}: {why}, but no step before it uses {CACHE_ACTION} "
                                f"(or sets {VARIABLE}); GitHub's release downloads fail now and then")
                break
    return problems


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems = check(root)
    for problem in problems:
        print(problem)
    if problems:
        print(f"{len(problems)} problem(s)")
        return 1
    print("every job that needs the polyglot library reads it from the cache")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
