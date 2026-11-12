#!/usr/bin/env python3
"""The compatibility check against the previous release (plan 24a UC07, QU9; the direction is plan 24 section 2.3).

For each product of dist/suite.json this reads the **latest stable release** (the newest tag `<product>/v<year>.<m>.<d>.<hhmm>`, no `-beta`, no `-snapshot`)
and what that release said about itself in its source: the compatibility generation of `Shared/src/DbDataKit.Web/SuiteCompatibility.cs` and the format
registry `docs/formats.json`. It then builds the matrix of pairs the suite promises to work in, one row per contract and direction:

  management API and pairing   console N with a server N-1, and a console N-1 with a server N   (the generation, SuiteCompatibility.Supports)
  publish                      a Build or Store server N-1 publishing to a serving group N, and the reverse
  serving protocols            a Flight SQL client N-1 against the serving layer N, and the reverse
  definition formats           for each format of docs/formats.json: the current code reads the format the previous release writes (its `current`
                               is not below this code's `previous`), so a definition published by the previous release is read, not refused

and fails, naming the pair, when a row does not hold: a previous release more than one generation from this build (in either direction), or one that writes
a format this code no longer reads. A release that predates the generation (its source has no SuiteCompatibility.cs) or the registry (no docs/formats.json)
is taken to be in range, as the pairing check does, and the matrix says so. Running the previous release's programs against this build is the part this
script does not do (see docs/plans/24a-upgrade-and-cache-slices.md, UC07); the contract it proves is the one both sides declare.

  python3 -I scripts/check_compatibility.py [root] [--matrix] [--previous-dir <dir>]

`--matrix` prints the matrix as JSON (the CI job puts it in the run's summary). `--previous-dir` reads the previous releases from a folder instead of git
tags, for the test of the job: `<dir>/<product>/tag`, `<dir>/<product>/SuiteCompatibility.cs` and `<dir>/<product>/formats.json`, each optional but the tag.
"""
import json
import os
import re
import subprocess
import sys

GENERATION_FILE = "Shared/src/DbDataKit.Web/SuiteCompatibility.cs"
FORMATS_FILE = "docs/formats.json"
SUITE_FILE = "dist/suite.json"
GENERATION = re.compile(r"public const int Generation = (\d+);")
STABLE = r"v(\d{4})\.(\d+)\.(\d+)\.(\d+)"
ALIASES = {"dbdatagateway": ["dbdataproxy"]}  # the product's name before the rename: its older tags are its releases too

CONTRACTS = [
    ("management API and pairing", "console", "server"),
    ("publish", "build or store server", "serving group"),
    ("serving protocols", "Flight SQL client", "serving layer"),
]


def read(path):
    with open(path, encoding="utf-8-sig") as f:
        return f.read()


def generation_of(text):
    m = GENERATION.search(text or "")
    return int(m.group(1)) if m else None


def formats_of(text):
    """{id: (current, previous)} of a docs/formats.json text; None when there is none or it cannot be read."""
    if text is None:
        return None
    try:
        return {f["id"]: (int(f["current"]), int(f["previous"])) for f in json.loads(text)["formats"]}
    except (ValueError, KeyError, TypeError):
        return None


def products(root):
    with open(os.path.join(root, *SUITE_FILE.split("/")), encoding="utf-8") as f:
        return [m["product"] for m in json.load(f)["members"]]


def git(root, *args):
    result = subprocess.run(["git", "-C", root, *args], capture_output=True, text=True, encoding="utf-8")
    return result.stdout if result.returncode == 0 else None


def latest_stable_tag(root, product):
    best = None
    for name in [product, *ALIASES.get(product, [])]:
        for tag in (git(root, "tag", "-l", f"{name}/v*") or "").split():
            m = re.fullmatch(re.escape(name) + "/" + STABLE, tag)
            if m and (best is None or tuple(map(int, m.groups())) > best[0]):
                best = (tuple(map(int, m.groups())), tag)
    return best[1] if best else None


def previous_from_git(root, product):
    """(tag, generation, formats) of the product's latest stable release, or None when it has none."""
    tag = latest_stable_tag(root, product)
    if tag is None:
        return None
    return tag, generation_of(git(root, "show", f"{tag}:{GENERATION_FILE}")), formats_of(git(root, "show", f"{tag}:{FORMATS_FILE}"))


def previous_from_dir(folder, product):
    base = os.path.join(folder, product)
    tag_path = os.path.join(base, "tag")
    if not os.path.exists(tag_path):
        return None

    def optional(name):
        path = os.path.join(base, name)
        return read(path) if os.path.exists(path) else None

    return read(tag_path).strip(), generation_of(optional("SuiteCompatibility.cs")), formats_of(optional("formats.json"))


def build(root, previous_dir=None):
    """Returns (matrix, problems)."""
    own = generation_of(read(os.path.join(root, *GENERATION_FILE.split("/"))))
    if own is None:
        return [], [f"{GENERATION_FILE}: no `public const int Generation = <n>;` found"]
    own_formats = formats_of(read(os.path.join(root, *FORMATS_FILE.split("/"))))
    if own_formats is None:
        return [], [f"{FORMATS_FILE}: cannot be read"]
    matrix, problems = [], []
    for product in products(root):
        found = previous_from_dir(previous_dir, product) if previous_dir else previous_from_git(root, product)
        if found is None:
            matrix.append({"product": product, "previous": None, "note": "no stable release yet: nothing to run against"})
            continue
        tag, generation, formats = found
        row = {"product": product, "previous": tag, "previous_generation": generation, "generation": own, "pairs": [], "formats": []}
        matrix.append(row)
        for contract, side_a, side_b in CONTRACTS:
            for direction, left, right in ((f"{side_a} N with {side_b} N-1", f"{side_a} (this build, generation {own})", f"{side_b} ({tag})"),
                                           (f"{side_a} N-1 with {side_b} N", f"{side_a} ({tag})", f"{side_b} (this build, generation {own})")):
                if generation is None:
                    verdict = "in range: the release predates the generation"
                elif abs(generation - own) <= 1:
                    verdict = "ok"
                else:
                    verdict = "refused"
                    problems.append(f"{product}: {contract}, {direction}: {left} and {right} state generations {own} and {generation}, "
                                    f"{abs(generation - own)} apart; they work together up to 1 apart. Upgrade the side with the lower generation, "
                                    "or bump the generation in steps")
                row["pairs"].append({"contract": contract, "pair": direction, "verdict": verdict})
        for fid, (current, prior) in sorted(own_formats.items()):
            if formats is None or fid not in formats:
                row["formats"].append({"format": fid, "verdict": "in range: the release has no entry for it"})
                continue
            theirs = formats[fid][0]
            if theirs < prior:
                verdict = "refused"
                problems.append(f"{product}: format {fid}: the release {tag} writes format {theirs} and this build reads {prior} to {current}; "
                                f"a definition published by that release would be refused. Keep reading format {theirs} (previous = {theirs}) "
                                "or bump the generation in steps")
            else:
                verdict = "ok"
            row["formats"].append({"format": fid, "writes": theirs, "reads": [prior, current], "verdict": verdict})
    return matrix, problems


def main(argv):
    args = argv[1:]
    previous_dir = None
    if "--previous-dir" in args:
        i = args.index("--previous-dir")
        previous_dir = os.path.abspath(args[i + 1])
        del args[i:i + 2]
    show = "--matrix" in args
    args = [a for a in args if a != "--matrix"]
    root = os.path.abspath(args[0]) if args else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    matrix, problems = build(root, previous_dir)
    if show:
        print(json.dumps(matrix, indent=2))
    out = sys.stderr if show else sys.stdout  # the matrix alone on stdout, so a job can keep it as a file
    for p in problems:
        print(p, file=out)
    print(f"{len(problems)} problem(s)" if problems else f"this build and the previous stable release of {sum(1 for r in matrix if r['previous'])} product(s) are within N and N-1", file=out)
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
