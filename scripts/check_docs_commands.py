#!/usr/bin/env python3
"""No product document, script or workflow uses a command name that plan 13 retired, or one that does not exist (slice X03).

Three checks over the same files:
  1. the retired forms, listed in RETIRED with what replaced them;
  2. every `dbdatasync|dbdataproxy|dbdatakit|dbdatastore|dbdatabuild <words>` that is typed must be a command that exists. The commands are read from the
     sources, not listed by hand: the nouns and verbs of each product's command line (the shared ones in DbDataKit/src, the product's own under its src) and,
     for Build, its generated command reference (DbDataBuild/docs/commands.md, which a test keeps current);
  3. no DbDataSync document says its server serves a web app at its own address (SYNC_WEB_APP): since U11 the screens are the dbdatakit console's.

History (plans, progress logs, research, phase documents, release notes) and test fixtures are not checked, because they say what was true then. A line can
name an old or made-up form on purpose with the marker `retired-name-ok` on itself or the line above.
Run from anywhere: python3 scripts/check_docs_commands.py [root]
"""
import os
import re
import sys

SKIP_DIRS = {"node_modules", "bin", "obj", ".git", "__pycache__", "tests", "architecture", "progress", "research", "plans", "review"}
SKIP_FILES = {"RELEASE-NOTES-PENDING.md", "CHANGELOG.md", "review.md"}
OK_MARK = "retired-name-ok"   # a line that must name the old form on purpose (a transitional step) says so, on itself or the line above
EXTENSIONS = (".md", ".yml", ".yaml", ".ps1", ".sh", ".service", ".wxs", ".json", ".py", ".txt")

# (pattern, what to say instead). A pattern matches a command as typed or written in a unit file, a script or a workflow.
RETIRED = [
    (r"\bdbdatasync(?:\.exe)?\s+serve\b", "dbdatasync server run"),
    (r"\bdbdatasync(?:\.exe)?\s+setup\b", "dbdatasync server setup"),
    (r"\bdbdatasync(?:\.exe)?\s+config\s+(cert|secret|library|driver)\b", "dbdatasync <cert|secret|library|driver> ... (no longer under config)"),
    (r"\bdbdataproxy(?:\.exe)?\s+hash-password\b", "dbdataproxy secret hash"),
    (r"\bdbdataproxy(?:\.exe)?\s+protect-secret\b", "dbdataproxy secret protect"),
    (r"\bdbdataproxy(?:\.exe)?\s+(install|uninstall)\b", "dbdataproxy service install|uninstall"),
    (r"\bdbdataproxy(?:\.exe)?\s+query(?!\s+run)\b", "dbdataproxy query run"),
    (r"\bdbdataproxy(?:\.exe)?\s+--healthcheck\b", "dbdataproxy health check"),
    (r"\bDbDataProxy(?:\.Worker)?\.exe\b", "dbdataproxy.exe / dbdataproxy-worker.exe (lower case)"),
]

# What a Sync document may no longer say (slice U11, X03): the server serves no web app, its screens are the dbdatakit console's. Each pattern is a way the old claim was written,
# with what is true now. Only DbDataSync's own documents are read, and a line can name the old claim on purpose with the marker.
SYNC_WEB_APP = [
    (r"(?i)\bopen\b[^.\n]{0,24}https?://(?:localhost|127\.0\.0\.1)(?::\d+)?:(?:5080|8080)\b", "the server's address serves no web app: open the server in the console (`/servers/<id>`)"),
    (r"(?i)\b(?:dbdatasync|the server|the api)\b[^.\n]{0,40}\bserves?\s+(?!\W*no\b)[^.\n]{0,30}\b(?:web\s+(?:ui|app|console)|spa|its console|the console)\b", "the server serves no web app; its screens are the dbdatakit console's"),
    (r"/app/wwwroot|\bwwwroot/docs\b", "the image and the packages carry no wwwroot; the docs are the console's (`/docs/sync`)"),
    (r"\bsrc/DbDataSync\.Web\b", "the screens are `DbDataKit/web/sync`"),
]

# The program each product's command line is called by, and the folder whose sources define it (the console is `dbdatakit`).
PRODUCTS = {
    "dbdatasync": "DbDataSync",
    "dbdataproxy": "DbDataProxy",
    "dbdatakit": "DbDataConsole",
    "dbdatastore": "DbDataStore",
    "dbdatabuild": "DbDataBuild",
}
NOUN_DEF = r'new(?:\s+Noun)?\(\s*"(?P<noun>[a-z][a-z-]*)"\s*,\s*"[^"]*"\s*,\s*\['      # a noun: its name, one line of help, then its verbs
# a verb: `new Verb("run", ...` or a product's helper that makes one, `Forward(cli, "set", "one line of help", EffectClass.X, ...`
VERB_DEF = r'(?:new\s+Verb\(\s*"(?P<verb>[a-z][a-z-]*)"|\w+\((?:\w+\s*,\s*)*"(?P<verb2>[a-z][a-z-]*)"\s*,\s*"(?:[^"\\]|\\.)*"\s*,\s*EffectClass\.)'
VERB_ONLY = re.compile(VERB_DEF)
FACTORY = r'\b(?P<cls>[A-Z]\w+)\.(?P<method>[A-Z]\w*)\('                                    # a shared noun made by a call: ServerNoun.Create(...)
TOKENS = re.compile(f"{NOUN_DEF}|{VERB_DEF}|{FACTORY}")
HEAD = re.compile(r"^\s*(?:[$>]\s*|sudo\s+|(?:\.|[\w.:\\/-]*[\\/]))?$")   # what may stand before a program name for the line to be that command
LEGACY = re.compile(r"Legacy\s*=[^{]*\{([^}]*)\}")      # Sync's commands that stay outside the router


def read(path):
    try:
        with open(path, encoding="utf-8") as f:
            return f.read()
    except (UnicodeDecodeError, OSError):
        return ""


def files(root):
    for dirpath, dirnames, names in os.walk(root):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in names:
            if name in SKIP_FILES or not name.endswith(EXTENSIONS):
                continue
            yield os.path.join(dirpath, name)


def cs_files(folder):
    for dirpath, dirnames, names in os.walk(folder):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIRS]
        for name in names:
            if name.endswith(".cs"):
                yield os.path.join(dirpath, name)


def shared_nouns(root):
    """'Class.Method' -> (noun, verbs) for every shared factory in DbDataKit/src that makes a noun (the pairing verbs add to `server`)."""
    index = {}
    for path in cs_files(os.path.join(root, "DbDataKit", "src")):
        text = read(path)
        for cls in re.finditer(r"static\s+class\s+(\w+)", text):
            for chunk in re.split(r"(?=public\s+static\s)", text[cls.end():]):
                head = re.match(r"public\s+static\s+(?:Noun|Verb\[\])\s+(\w+)\(", chunk)
                if not head:
                    continue
                noun = re.search(NOUN_DEF, chunk)
                if noun is None and cls.group(1) != "PairingVerbs":
                    continue
                verbs = [m.group("verb") or m.group("verb2") for m in VERB_ONLY.finditer(chunk)]
                index[f"{cls.group(1)}.{head.group(1)}"] = (noun.group("noun") if noun else "server", verbs)
    return index


def commands_of(root, product):
    """One product's commands as tuples of words: (noun, verb), (name,) for a command outside the router, Build's own from its command reference."""
    folder = os.path.join(root, PRODUCTS[product])
    shared = shared_nouns(root)
    found = set()
    for path in cs_files(os.path.join(folder, "src")):
        text = read(path)
        current = None
        for match in TOKENS.finditer(text):
            if match.group("noun"):
                current = match.group("noun")
            elif match.group("verb") or match.group("verb2"):
                if current:
                    found.add((current, match.group("verb") or match.group("verb2")))
            elif (key := f"{match.group('cls')}.{match.group('method')}") in shared:
                current, verbs = shared[key]
                found.update((current, v) for v in verbs)
        for legacy in LEGACY.findall(text):
            found.update((w,) for w in re.findall(r'"([a-z][a-z-]*)"', legacy))
    for line in read(os.path.join(folder, "docs", "commands.md")).splitlines():
        if m := re.match(r"\|\s*\[`([a-z][a-z -]*)`\]", line):
            found.add(tuple(m.group(1).split()))
    return found


def is_command(words, commands):
    """The words typed after the program name a command (words after a whole command are its arguments) or the start of one (a noun alone)."""
    return any(tuple(words[:len(c)]) == c or (len(words) < len(c) and c[:len(words)] == tuple(words)) for c in commands)


def code_of(path, lines):
    """(line number, text) of what a reader would type: all of a script or workflow, only the code spans and fences of a Markdown file."""
    if not path.endswith(".md"):
        yield from enumerate(lines, 1)
        return
    fenced = False
    for number, line in enumerate(lines, 1):
        if line.lstrip().startswith("```"):
            fenced = not fenced
        elif fenced:
            yield number, line
        else:
            for span in re.findall(r"`([^`]+)`", line):
                yield number, span


def marked(lines, number):
    return OK_MARK in lines[number - 1] or (number > 1 and OK_MARK in lines[number - 2])


def check_retired(root):
    problems = []
    compiled = [(re.compile(pattern), instead) for pattern, instead in RETIRED]
    for path in files(root):
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        lines = read(path).splitlines()
        for number, line in enumerate(lines, 1):
            if marked(lines, number):
                continue
            for pattern, instead in compiled:
                if pattern.search(line):
                    problems.append(f"{rel}:{number}: a retired command name; write `{instead}`")
    return problems


def check_web_app(root):
    """No document of DbDataSync says its server serves a web app at its own address (the screens moved to the console in U11)."""
    problems = []
    compiled = [(re.compile(pattern), instead) for pattern, instead in SYNC_WEB_APP]
    sync = os.path.join(root, "DbDataSync")
    if not os.path.isdir(sync):
        return problems
    for path in files(sync):
        if not path.endswith(".md"):
            continue
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        lines = read(path).splitlines()
        for number, line in enumerate(lines, 1):
            if marked(lines, number):
                continue
            for pattern, instead in compiled:
                if pattern.search(line):
                    problems.append(f"{rel}:{number}: says Sync serves a web app; {instead}")
    return problems


def check_exist(root):
    problems = []
    tables = {}
    for product, folder in PRODUCTS.items():
        if os.path.isdir(os.path.join(root, folder, "src")):
            tables[product] = commands_of(root, product)
            if not tables[product]:
                problems.append(f"{folder}: no command was found in its sources, so its documents cannot be checked (the reader is in scripts/check_docs_commands.py)")
    if not tables:
        return problems
    typed = re.compile(r"(?:^|[\s$\"'(=])(" + "|".join(tables) + r")(?:\.exe)?[ \t]+((?:[a-z][a-z-]*(?:[ \t]+|$)){1,3})")
    for path in files(root):
        if path.endswith(".json"):   # package manifests, schemas and generated tool lists: prose and data, not what anyone types
            continue
        rel = os.path.relpath(path, root).replace(os.sep, "/")
        lines = read(path).splitlines()
        for number, text in code_of(path, lines):
            if marked(lines, number):
                continue
            for match in typed.finditer(text):
                program, words = match.group(1), match.group(2).split()
                known_noun = any(c[0] == words[0] for c in tables[program])
                # a noun that exists with a verb that does not is wrong wherever it stands; an unknown first word only when the text starts with the program (else it is a sentence)
                if not is_command(words, tables[program]) and (known_noun or HEAD.match(text[:match.start(1)])):
                    nouns = sorted({c[0] for c in tables[program]})
                    verbs = sorted(c[1] for c in tables[program] if len(c) > 1 and c[0] == words[0])
                    hint = f"{words[0]} has: {', '.join(verbs)}" if verbs else f"it has: {', '.join(nouns)}"
                    shown = " ".join(words[:2]) if verbs else words[0]
                    problems.append(f"{rel}:{number}: `{program} {shown}` is not a command of {program} ({hint})")
    return problems


# Products whose README is the whole command reference (they are small and new): every noun of their command line must appear in it.
README_COMPLETE = ("dbdatakit", "dbdatastore")


def check_readmes(root):
    problems = []
    for program in README_COMPLETE:
        folder = PRODUCTS[program]
        readme = os.path.join(root, folder, "README.md")
        if not os.path.isfile(readme) or not os.path.isdir(os.path.join(root, folder, "src")):
            continue
        text = read(readme)
        for noun in sorted({c[0] for c in commands_of(root, program)}):
            if not re.search(rf"\b{program}(?:\.exe)?\s+{re.escape(noun)}\b", text):
                problems.append(f"{folder}/README.md: the command line has `{program} {noun}` and the README does not show it")
    return problems


def check(root):
    return check_retired(root) + check_web_app(root) + check_exist(root) + check_readmes(root)


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems = check(root)
    for problem in problems:
        print(problem)
    print(f"{len(problems)} problem(s)" if problems else "no document uses a retired or unknown command name")
    return 1 if problems else 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
