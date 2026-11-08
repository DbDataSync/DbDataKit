#!/usr/bin/env python3
"""Every workflow job that needs the DuckDB engine reads it from the cache (docs/ci-lessons.md, "GitHub release downloads fail").

GitHub's release downloads fail now and then, so no job downloads the pinned archives itself: the action .github/actions/duckdb-cache restores them
from the Actions cache (keyed on the hash of duckdb.pin) or downloads them once with retries, and sets DUCKDB_RELEASE_BASE for the rest of the job.
This fails when

  * a job has a step that needs DuckDB (runs fetch-duckdb, publish.sh / publish-runtime.sh, builds a Docker image or compose file whose Dockerfile fetches
    DuckDB, or uses setup-product without `duckdb: 'false'`) and no earlier step of the job uses the duckdb-cache action, and the job, the workflow or the
    step does not set DUCKDB_RELEASE_BASE itself,
  * a Dockerfile fetches DuckDB without taking it from the build context's .duckdb-archives (DUCKDB_RELEASE_BASE), or
  * the actions are not as the rule needs them: setup-product uses duckdb-cache, and duckdb-cache keys on the hash of duckdb.pin, runs
    fetch-duckdb.sh --archives and exports DUCKDB_RELEASE_BASE.

Run from anywhere: python3 scripts/check_duckdb_cache.py [root]
"""
import os
import re
import sys

CACHE_ACTION = "./.github/actions/duckdb-cache"
SETUP_ACTION = "./.github/actions/setup-product"
SKIP_DIRS = {"node_modules", "bin", "obj", ".git", ".claude", ".duckdb", ".duckdb-archives"}
# commands that fetch DuckDB themselves (the scripts call fetch-duckdb)
FETCHING_COMMANDS = re.compile(r"fetch-duckdb|\bpublish-runtime\.sh|(?<![\w-])scripts/publish\.sh|\bbuild-catalog-cache")
DOCKER_COMMANDS = re.compile(r"\bdocker\s+(?:buildx\s+)?build\b|\bdocker\s+compose\b|dist/compose/check\.sh")


def read(path):
    with open(path, encoding="utf-8") as f:
        return f.read()


def walk(root, predicate):
    for dirpath, dirnames, names in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in names:
            if predicate(name):
                yield os.path.join(dirpath, name)


def rel(root, path):
    return os.path.relpath(path, root).replace(os.sep, "/")


def duckdb_dockerfiles(root):
    """Dockerfiles (as paths relative to the root) that fetch the DuckDB engine."""
    found = {}
    for path in walk(root, lambda n: n == "Dockerfile" or n.startswith("Dockerfile.") and not n.endswith(".dockerignore")):
        text = read(path)
        if re.search(r"fetch-duckdb|build-catalog-cache|DuckDbEngine", text):
            found[rel(root, path)] = text
    return found


def duckdb_compose_files(root, dockerfiles):
    """Compose files (relative) that build one of the DuckDB Dockerfiles."""
    found = set()
    for path in walk(root, lambda n: n.endswith((".yml", ".yaml"))):
        r = rel(root, path)
        if r.startswith(".github/"):
            continue
        text = read(path)
        if re.search(r"^\s*build:", text, re.M) and any(re.search(r"dockerfile:\s*" + re.escape(d) + r"\s*$", text, re.M) for d in dockerfiles):
            found.add(r)
    return found


def split_job_steps(job_text):
    """(header text before `steps:`, [step text, ...])."""
    lines = job_text.split("\n")
    start = next((i for i, l in enumerate(lines) if re.match(r"^\s{4}steps:\s*$", l)), None)
    if start is None:
        return job_text, []
    header = "\n".join(lines[:start])
    steps, current, indent = [], None, None
    for line in lines[start + 1:]:
        m = re.match(r"^(\s*)- ", line)
        if m and (indent is None or len(m.group(1)) == indent):
            indent = len(m.group(1))
            if current is not None:
                steps.append("\n".join(current))
            current = [line]
        elif current is not None:
            current.append(line)
    if current is not None:
        steps.append("\n".join(current))
    return header, steps


def split_jobs(text):
    """(workflow header, {job id: job text})."""
    m = re.search(r"^jobs:\s*$", text, re.M)
    if not m:
        return text, {}
    header, body = text[:m.start()], text[m.end():]
    jobs, name, buf = {}, None, []
    for line in body.split("\n"):
        j = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if j:
            if name:
                jobs[name] = "\n".join(buf)
            name, buf = j.group(1), []
        elif name:
            buf.append(line)
    if name:
        jobs[name] = "\n".join(buf)
    return header, jobs


def step_needs_duckdb(step, dockerfiles, compose_files):
    """Why this step needs DuckDB, or None."""
    uses = re.search(r"^\s*-?\s*uses:\s*(\S+)", step, re.M)
    if uses and uses.group(1) == SETUP_ACTION:
        return None if re.search(r"duckdb:\s*['\"]?false['\"]?\s*$", step, re.M) else "setup-product fetches it (it applies the cache itself)"
    if uses and uses.group(1).startswith("docker/build-push-action"):
        target = re.search(r"^\s*file:\s*(\S+)", step, re.M)
        if target and target.group(1) in dockerfiles:
            return f"it builds {target.group(1)}, which fetches DuckDB"
        return None
    run = re.search(r"^\s*(?:-\s*)?run:\s*(.*)$", step, re.M)
    if not run:
        return None
    body = step[run.start():]
    command = re.sub(r"#.*", "", body)  # comments in a run block are not commands
    if FETCHING_COMMANDS.search(command):
        return "it runs a script that fetches DuckDB"
    if DOCKER_COMMANDS.search(command):
        if "matrix.dockerfile" in command:
            return "it builds a Dockerfile named by the matrix"
        for d in dockerfiles:
            if d in command:
                return f"it builds {d}, which fetches DuckDB"
        for c in compose_files:
            if c in command:
                return f"it starts {c}, which builds an image that fetches DuckDB"
        if "dist/compose/check.sh" in command and compose_files:
            return "it starts a compose file whose images fetch DuckDB"
    return None


def check_actions(root):
    problems = []
    setup = os.path.join(root, ".github/actions/setup-product/action.yml")
    cache = os.path.join(root, ".github/actions/duckdb-cache/action.yml")
    if os.path.exists(setup) and CACHE_ACTION not in read(setup):
        problems.append(".github/actions/setup-product/action.yml: does not use the duckdb-cache action, so every job built on it downloads DuckDB itself")
    if not os.path.exists(cache):
        problems.append(".github/actions/duckdb-cache/action.yml: missing")
        return problems
    text = read(cache)
    for needle, why in (
        ("hashFiles('duckdb.pin')", "the cache key must contain the hash of duckdb.pin, or a changed pin could be served from an old cache"),
        ("fetch-duckdb.sh --archives", "the archives are fetched (hash-checked) by fetch-duckdb.sh --archives"),
        ("DUCKDB_RELEASE_BASE=", "it must export DUCKDB_RELEASE_BASE for the jobs' fetches"),
        ("actions/cache/save", "a miss must be saved"),
    ):
        if needle not in text:
            problems.append(f".github/actions/duckdb-cache/action.yml: missing `{needle}`: {why}")
    return problems


def check(root):
    problems = check_actions(root)
    dockerfiles = duckdb_dockerfiles(root)
    for path, text in sorted(dockerfiles.items()):
        if "DUCKDB_RELEASE_BASE" not in text or ".duckdb-archive" not in text:
            problems.append(f"{path}: fetches DuckDB without taking it from the build context's .duckdb-archives "
                            "(COPY duckdb.pin .duckdb-archive[s] /duckdb-archives/ and DUCKDB_RELEASE_BASE=file:///duckdb-archives)")
    compose_files = duckdb_compose_files(root, dockerfiles)
    workflows = os.path.join(root, ".github", "workflows")
    if not os.path.isdir(workflows):
        return problems
    for name in sorted(os.listdir(workflows)):
        if not name.endswith((".yml", ".yaml")):
            continue
        text = read(os.path.join(workflows, name))
        header, jobs = split_jobs(text)
        workflow_sets = "DUCKDB_RELEASE_BASE" in header
        for job, job_text in sorted(jobs.items()):
            job_header, steps = split_job_steps(job_text)
            if workflow_sets or "DUCKDB_RELEASE_BASE" in job_header:
                continue
            cached = False
            for number, step in enumerate(steps, 1):
                uses = re.search(r"^\s*-?\s*uses:\s*(\S+)", step, re.M)
                if uses and uses.group(1) == CACHE_ACTION:
                    cached = True
                    continue
                why = step_needs_duckdb(step, dockerfiles, compose_files)
                if why is None:
                    continue
                if uses and uses.group(1) == SETUP_ACTION:
                    cached = True  # setup-product applies the cache (checked in check_actions)
                    continue
                if cached or "DUCKDB_RELEASE_BASE" in step:
                    continue
                problems.append(f".github/workflows/{name}: job {job}, step {number}: {why}, but no step before it uses {CACHE_ACTION} "
                                "(or sets DUCKDB_RELEASE_BASE); GitHub's release downloads fail now and then")
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
    print("every job that needs DuckDB reads it from the cache")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
