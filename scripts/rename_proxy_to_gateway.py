#!/usr/bin/env python3
"""O14 (plan 20): rename the product DbDataProxy to DbDataGateway, mechanically and idempotently.

    python3 -I scripts/rename_proxy_to_gateway.py --dry-run     # list what would move and change, touch nothing
    python3 -I scripts/rename_proxy_to_gateway.py               # do it (git mv for every path, then the file contents)
    python3 -I scripts/rename_proxy_to_gateway.py --root PATH   # another repository root (the test uses this)

What it does, in this order:

1. Paths: every tracked path that carries the old name moves with `git mv`, so history follows (the folder `DbDataProxy/`, `DbDataProxy.slnf`, the
   workflows, the testenv map, the web workspace `DbDataKit/web/proxy`, the product's `Proxy*` files, and the few files outside the product that are
   about it: see PATH_MOVES).
2. Contents of every tracked text file, with three levels:
   * everywhere: the product's names (`DbDataProxy`, `dbdataproxy`, `DBDATAPROXY`, the `DBPROXY_` environment prefix, `dbdata:proxy:` secret names)
     and the type names the product declared;
   * in the product's own files, its web workspace, the console's screens and code, dist/, testenv/, the workflows and the documents: the word
     `proxy` / `Proxy` / `proxies` (and `ProxyHost`-style identifiers) where it names the product, line by line (a line about a reverse proxy or
     `X-Forwarded-*` is left as it is, see HTTP_PROXY_LINE);
   * never: the plans' history (docs/plans, except plan 20 and the index), the product's `docs/review.md` (a review of the past), this script and its
     test, and the files listed in KEEP_OLD_NAMES (the code and tests that refuse the old names with a message and so must say them).
3. Nothing else: Build's `server proxy` noun, `/api/v1/proxies`, `build/proxies.json` and the Store's `StoreProxies` are those products' own
   vocabulary for "a server I publish to" and keep their names (plan 20 O14b).

The script is idempotent: a second run finds no old path and no old name outside the files it leaves alone, and says so. Python standard library only.
"""
from __future__ import annotations

import argparse
import re
import subprocess
import sys
from pathlib import Path

# ---- what is left alone ------------------------------------------------------------------------------------------------------------------

#: Files that must keep the old names: the script and its test, and the refusal of the old names (they name the old and the new one).
KEEP_OLD_NAMES = {
    "scripts/rename_proxy_to_gateway.py",
    "scripts/tests/test_rename_proxy_to_gateway.py",
    "DbDataGateway/src/DbDataGateway.Server/Hosting/RenamedFrom.cs",
    "DbDataGateway/tests/DbDataGateway.Tests/Hosting/RenamedFromTests.cs",
    "docs/release-notes/dbdatagateway-rename.md",
    "docs/serving-layer.md",                # these three say "it was DbDataProxy"
    "docs/ci-lessons.md",
    "docs/plans/20-serving-layer.md",
    "DbDataConsole/src/DbDataConsole.Server/RenamedProducts.cs",
    "DbDataConsole/tests/DbDataConsole.Tests/RenamedProductTests.cs",
    # generic samples of the legacy-prefix mechanism (an environment prefix and a section name), not the product's own
    "DbDataKit/src/DbDataKit.Config/Overrides.cs",
    "DbDataKit/tests/DbDataKit.Config.Tests/LayeringTests.cs",
}

#: Prefixes of history: plans and the review of the past keep what they said when they said it.
HISTORY_PREFIXES = ("docs/plans/", "DbDataProxy/docs/review.md", "DbDataGateway/docs/review.md")
#: Inside docs/plans these two describe the present.
HISTORY_EXCEPTIONS = {"docs/plans/20-serving-layer.md", "docs/plans/README.md"}

# ---- paths -------------------------------------------------------------------------------------------------------------------------------

#: Files outside the product's folder that are about the product. (path or folder prefix, new)
PATH_MOVES = [
    ("DbDataKit/web/proxy", "DbDataKit/web/gateway"),
    ("dist/compose/proxy.yml", "dist/compose/gateway.yml"),
    ("dist/compose/quickstart/apps-proxy.sh", "dist/compose/quickstart/apps-gateway.sh"),
    ("DbDataConsole/tests/DbDataConsole.Web.Tests/tests/proxy-apps.spec.ts", "DbDataConsole/tests/DbDataConsole.Web.Tests/tests/gateway-apps.spec.ts"),
    ("DbDataConsole/tests/DbDataConsole.Servers.Tests/ProxyAudit.cs", "DbDataConsole/tests/DbDataConsole.Servers.Tests/GatewayAudit.cs"),
    ("DbDataKit/tests/DbDataKit.Connections.FlightSql.Tests/ProxyEndToEndTests.cs", "DbDataKit/tests/DbDataKit.Connections.FlightSql.Tests/GatewayEndToEndTests.cs"),
    ("DbDataSync/tests/DbDataSync.Drivers.ArrowFlightSql.Tests/LiveProxyTests.cs", "DbDataSync/tests/DbDataSync.Drivers.ArrowFlightSql.Tests/LiveGatewayTests.cs"),
]

#: Types outside the product's folder named for it (they are not declared there, so they cannot be found by scanning it).
EXTRA_IDENTIFIERS = {"ProxyAudit": "GatewayAudit", "ProxyEndToEndTests": "GatewayEndToEndTests", "LiveProxyTests": "LiveGatewayTests"}

NAME_RULES = [("DbDataProxy", "DbDataGateway"), ("dbdataproxy", "dbdatagateway"), ("DBDATAPROXY", "DBDATAGATEWAY"), ("DBPROXY_", "DBGATEWAY_")]


def new_path(path: str, declared: dict[str, str]) -> str:
    for old, new in PATH_MOVES:
        if path == old or path.startswith(old + "/"):
            path = new + path[len(old):]
            break
    for old, new in NAME_RULES:
        path = path.replace(old, new)
    parts = path.split("/")
    if parts[0] == "DbDataGateway" or path.startswith("DbDataKit/web/gateway/"):
        parts = [("gateway" if c == "proxy" else re.sub(r"Proxy", "Gateway", c)) for c in parts]
    return "/".join(parts)


# ---- contents ----------------------------------------------------------------------------------------------------------------------------

#: A line that is about an HTTP proxy, not the product, is not touched by the word rule.
HTTP_PROXY_LINE = re.compile(
    r"(reverse|forward\w*|http\w*|web|corporate|system|outbound|socks|trusted|transparent|load.balanc\w+|ssh)[ -]proxy|X-Forwarded|HTTPS?_PROXY|proxy_pass|WebProxy|"
    r"proxy (settings|address|protocol)|behind (a|the) proxy|through (a |the )?(reverse )?prox|proxy lets|apt-get|dev-server proxy|proxy\.example",
    re.IGNORECASE)

#: The product's own files have no line about the plural `proxies` of an HTTP kind, so only the unmistakable phrases are skipped there.
HTTP_PROXY_PHRASE = re.compile(
    r"(reverse|forward\w*|http\w*|web|corporate|outbound|socks|trusted|transparent|ssh)[ -]proxy|X-Forwarded|HTTPS?_PROXY|proxy_pass|WebProxy", re.IGNORECASE)

#: Lines of the console that talk to Build's own vocabulary (its `proxies` list and `server proxy` verbs) keep it.
BUILD_VOCABULARY_LINE = re.compile(
    r"proxies|Proxies|(server|store|dbdatastore|dbdatabuild|\$\w+) proxy (add|remove|list)|--proxy\b|ProxyPublish|ProxiesFile|StoreProxies")

WORD_RULES = [
    (re.compile(r"(?<![A-Za-z])Proxies(?![a-z])"), "Gateways"),
    (re.compile(r"(?<![A-Za-z])proxies(?![a-z])"), "gateways"),
    (re.compile(r"(?<![A-Za-z])Proxy(?![a-z])"), "Gateway"),
    (re.compile(r"(?<![A-Za-z])proxy(?![a-z])"), "gateway"),
]
#: Inside the product itself a CamelCase name that ends or continues with Proxy (PairedProxyServer, LiveProxyTests) names the product too.
INNER_CAMEL = re.compile(r"(?<=[a-z0-9])Proxy(?![a-z])")
#: and these spellings (`dbproxy-cfg-` temp folders, the unit name `proxyb`, `The_proxys_default` test names) in the product's own files.
PRODUCT_SPELLINGS = [(re.compile(r"dbproxy"), "dbgateway"), (re.compile(r"(?<![A-Za-z])proxyb(?![a-z])"), "gatewayb"), (re.compile(r"(?<![A-Za-z])proxys(?![a-z])"), "gateways")]

OTHER_PRODUCTS = re.compile(r"StoreProxies|(?:server|store|dbdatastore|dbdatabuild) proxy (?:add|remove|list)|--proxy\b")

SECRET_PREFIX = re.compile(r"dbdata:proxy:")


def under(path: str, *prefixes: str) -> bool:
    return any(path == p or path.startswith(p if p.endswith("/") else p + "/") for p in prefixes)


def word_level(path: str) -> str | None:
    """None, 'product' (every line), or 'prose' (lines about an HTTP proxy are skipped, and Build's vocabulary in the console is kept)."""
    if under(path, "DbDataGateway", "DbDataKit/web/gateway"):
        return "product"
    if under(path, "DbDataKit/src") and path.endswith(".cs"):
        return "comments"
    if under(path, "DbDataConsole", "DbDataKit/web/console/src", "dist", "testenv", ".github", "CLAUDE.md", "scripts"):
        return "prose"
    if path.endswith(".md") and under(path, "docs", "DbDataKit", "DbDataRunner", "DbDataStore") or path in ("README.md", "CLAUDE.md"):
        return "prose"
    return None


WORKSPACE_FILES = {"DbDataKit/web/package.json", "DbDataKit/web/package-lock.json", "DbDataKit/web/console/package.json"}
WORKSPACE_RULES = [
    (re.compile(r"@dbdatakit/proxy\b"), "@dbdatakit/gateway"),
    (re.compile(r'^(\s*)"proxy"(,?)$', re.M), r'\1"gateway"\2'),
    (re.compile(r'^(\s*)"proxy": \{$', re.M), r'\1"gateway": {'),
    (re.compile(r'"resolved": "proxy"'), '"resolved": "gateway"'),
]


def _workspace(text: str) -> str:
    for pattern, replacement in WORKSPACE_RULES:
        text = pattern.sub(replacement, text)
    return text


def is_history(path: str) -> bool:
    if path in HISTORY_EXCEPTIONS:
        return False
    return any(path == h or path.startswith(h) for h in HISTORY_PREFIXES)


def transform(path: str, text: str, identifiers: dict[str, str]) -> str:
    """The new text of one file (the same text when nothing applies). `path` is the new path."""
    if path in KEEP_OLD_NAMES or is_history(path):
        return text
    for old, new in NAME_RULES:
        text = text.replace(old, new)
    # secret names the product keeps in its store; the shared libraries' own tests use the same text as a sample name and keep it
    if not under(path, "DbDataKit/tests", "DbDataKit/src"):
        text = SECRET_PREFIX.sub("dbdata:gateway:", text)
    # the configuration library's tests declare a `ProxyConfig` of their own: a sample, not the product's type
    if identifiers and not under(path, "DbDataGateway", "DbDataKit/tests/DbDataKit.Config.Tests"):
        for old, new in identifiers.items():
            text = re.sub(rf"\b{old}\b", new, text)
    if path in WORKSPACE_FILES:
        # the npm workspace list and lock name the workspace `proxy` and the package `@dbdatakit/proxy`; other `proxy` words in the lock are npm packages
        return _workspace(text)
    level = word_level(path)
    if level:
        out = []
        for line in text.splitlines(keepends=True):
            if level == "product" and HTTP_PROXY_PHRASE.search(line):
                out.append(line)
                continue
            if level == "comments" and not line.lstrip().startswith(("//", "*")):
                out.append(line)
                continue
            if level in ("prose", "comments") and (HTTP_PROXY_LINE.search(line) or BUILD_VOCABULARY_LINE.search(line)):
                out.append(line)
                continue
            if level == "product":
                # other products' own vocabulary inside a line about the product stays: masked while the rules run
                line = OTHER_PRODUCTS.sub(lambda m: m.group(0).replace("rox", "\0"), line)
                line = INNER_CAMEL.sub("Gateway", line)
                for pattern, replacement in PRODUCT_SPELLINGS:
                    line = pattern.sub(replacement, line)
            for pattern, replacement in WORD_RULES:
                line = pattern.sub(replacement, line)
            out.append(line.replace("\0", "rox"))
        text = "".join(out)
    return text


# ---- the run -----------------------------------------------------------------------------------------------------------------------------

def git(root: Path, *args: str, check: bool = True) -> str:
    result = subprocess.run(["git", "-C", str(root), *args], capture_output=True, text=True, check=check)
    return result.stdout


def tracked(root: Path) -> list[str]:
    return [p for p in git(root, "ls-files", "-z").split("\0") if p]


def declared_identifiers(root: Path, files: list[str]) -> dict[str, str]:
    """The `Proxy*` types the product declares (found before its folder is renamed), as old -> new."""
    found = dict(EXTRA_IDENTIFIERS)
    declaration = re.compile(r"\b(?:class|record|struct|interface|enum)\s+(?:struct\s+)?(Proxy[A-Za-z0-9]*)")
    for p in files:
        if under(p, "DbDataProxy") and p.endswith(".cs"):
            try:
                text = (root / p).read_text(encoding="utf-8")
            except (OSError, UnicodeDecodeError):
                continue
            for name in declaration.findall(text):
                found[name] = name.replace("Proxy", "Gateway", 1)
    return found


def plan(root: Path) -> tuple[list[tuple[str, str]], dict[str, str], dict[str, str]]:
    files = tracked(root)
    identifiers = declared_identifiers(root, files)
    moves = [(p, new_path(p, identifiers)) for p in files]
    moves = [(a, b) for a, b in moves if a != b]
    changes: dict[str, str] = {}
    moved = dict(moves)
    for p in files:
        target = moved.get(p, p)
        try:
            data = (root / p).read_bytes()
            text = data.decode("utf-8")
        except (OSError, UnicodeDecodeError):
            continue
        new = transform(target, text, identifiers)
        if new != text:
            changes[p] = new
    return moves, changes, identifiers


def _prune(root: Path, directory: Path) -> None:
    """Removes the folders a move left empty (git does not track them; they would only confuse a build)."""
    while str(directory) not in ("", ".") and (root / directory).is_dir() and not any((root / directory).iterdir()):
        (root / directory).rmdir()
        directory = directory.parent


def run(root: Path, dry_run: bool) -> int:
    moves, changes, _ = plan(root)
    if not moves and not changes:
        print("Nothing to do: no old path and no old name outside the files that keep them.")
        return 0
    print(f"{len(moves)} path(s) to move, {len(changes)} file(s) to change.")
    for old, new in moves:
        print(f"  mv  {old} -> {new}")
    for p in sorted(changes):
        print(f"  edit {p}")
    if dry_run:
        print("Dry run: nothing was touched.")
        return 0
    for old, new in moves:
        (root / new).parent.mkdir(parents=True, exist_ok=True)
        git(root, "mv", old, new)
        _prune(root, Path(old).parent)
    moved = dict(moves)
    for p, text in changes.items():
        (root / moved.get(p, p)).write_bytes(text.encode("utf-8"))
    print("Done. Review `git status`, then build; regenerate the goldens and schemas the product writes.")
    return 0


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--root", default=str(Path(__file__).resolve().parent.parent), help="the repository root (default: this repository)")
    parser.add_argument("--dry-run", action="store_true", help="list what would move and change, and touch nothing")
    args = parser.parse_args(argv)
    return run(Path(args.root).resolve(), args.dry_run)


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
