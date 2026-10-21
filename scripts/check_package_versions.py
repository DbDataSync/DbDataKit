#!/usr/bin/env python3
"""One version of each NuGet package in the repository (plan 13, slice F06).

Versions are chosen in the root Directory.Packages.props and nowhere else. This fails when

  * a project, props or targets file gives a PackageReference its own Version,
  * a package is listed twice in a Directory.Packages.props, or in two of them with different versions, or
  * a project overrides a version (VersionOverride) for a package that is not in ALLOWED_OVERRIDES.

ALLOWED_OVERRIDES is the list of known exceptions; the slice that removes an exception removes it here.
Run from anywhere: python3 scripts/check_package_versions.py [root]
"""
import os
import re
import sys

ALLOWED_OVERRIDES = {
    "Oracle.ManagedDataAccess.Core",  # Sync stays on 23.9.1 until A09 (the shared Oracle composer)
}

SKIP_DIRS = {"node_modules", "bin", "obj", ".git"}
PROJECT_FILES = (".csproj", ".props", ".targets", ".fsproj", ".vbproj")


def files(root):
    for dirpath, dirnames, names in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in names:
            if name.endswith(PROJECT_FILES):
                yield os.path.join(dirpath, name)


def check(root):
    problems = []
    declared = {}  # package -> (version, file)
    for path in files(root):
        rel = os.path.relpath(path, root)
        text = open(path, encoding="utf-8-sig").read()
        if os.path.basename(path) == "Directory.Packages.props":
            seen_here = set()
            for package, version in re.findall(r'<PackageVersion\s+Include="([^"]+)"\s+Version="([^"]+)"', text):
                key = package.lower()
                if key in seen_here:
                    problems.append(f"{rel}: {package} is listed twice")
                seen_here.add(key)
                if key in declared and declared[key][0] != version:
                    problems.append(f"{rel}: {package} {version}, but {declared[key][1]} has {declared[key][0]}")
                declared.setdefault(key, (version, rel))
            continue
        for tag in re.findall(r"<PackageReference\b[^>]*?/?>", text, re.S):
            include = re.search(r'Include="([^"]+)"', tag)
            name = include.group(1) if include else "(no Include)"
            if re.search(r'\sVersion="', tag):
                problems.append(f"{rel}: PackageReference {name} has its own Version; set it in the root Directory.Packages.props")
            override = re.search(r'\sVersionOverride="', tag)
            if override and name not in ALLOWED_OVERRIDES:
                problems.append(f"{rel}: PackageReference {name} overrides its version; one version of each package in the repository")
    return problems


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems = check(root)
    for problem in problems:
        print(problem)
    if problems:
        print(f"{len(problems)} problem(s)")
        return 1
    print("one version of each package")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
