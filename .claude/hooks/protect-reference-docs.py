#!/usr/bin/env python3
"""PreToolUse hook for Bash: asks the person before a shell command writes one of the owner-approved files.

The permission rules in .claude/settings.json ("ask" on Edit(...)) cover the file tools only. A shell command can write a file with sed -i, a
redirection, python and the like, so this hook reads the command and answers "ask" only when a part of it writes a protected file:

- the command is split into parts at ;, &&, ||, |, &, parentheses and newlines (quotes respected; a heredoc body belongs to the part that
  opened it; the text of $(...) and backticks is read as a command of its own);
- a protected file is recognised only as a whole path: the path itself (CLAUDE.md, ./CLAUDE.md), or an absolute path whose remainder after a
  git work tree root (a folder with a .git entry, or a folder that does not exist yet) is the path; DbDataBuild/CLAUDE.md is another file;
- a part writes a file when the file is a redirection target (> and >>, never 2>&1), an argument of tee, rm, truncate, shred or an editor,
  the file sed -i, perl -i or awk -i inplace edits, the destination of cp, ln, install or rsync, either side of mv, dd's of=, a path given to
  git checkout, git restore, git rm or git mv, or a file a patch (patch, git apply, git am) names; rm, mv, git rm and git mv of a folder that
  holds a protected file count too;
- an interpreter (python, node, perl without -i, ruby, a shell) counts as writing when its part (its arguments, its -c or -e text, its
  heredoc body) names a protected path.

Merges, pulls, resets, stashes, cherry-picks and reverts do not ask: they bring in commits that were approved when they were written.
A command that only reads a protected file (cat, grep, sed -n, git diff) runs as before.

What it cannot see: a path a command builds in pieces (variables, os.path.join), a file a script writes without naming it on the command
line, and paths relative to a folder the command moved into: after `cd docs`, a bare `architecture.md` is not recognised. The Edit rules in
.claude/settings.json still cover the file tools, and GitHub's code owners review is the guarantee at merge.
"""
import json
import os
import re
import sys

PROTECTED = (
    "CLAUDE.md",
    "planning/ROLES.md",
    "docs/architecture.md",
    ".claude/skills/product-docs/SKILL.md",
    ".claude/settings.json",
    ".claude/hooks/protect-reference-docs.py",
)


def _folders(path):
    folder = os.path.dirname(path)
    while folder:
        yield folder
        folder = os.path.dirname(folder)


# Folders that hold a protected file: removing or moving one of them removes the file. The work tree root itself is not one, so removing a
# whole worktree does not ask.
PROTECTED_DIRS = tuple(sorted({folder for p in PROTECTED for folder in _folders(p)}))

INTERPRETER = re.compile(r"(?:python[0-9.]*|node|nodejs|perl|ruby|bash|sh|zsh|dash|ksh)$")
SHELL_EDITORS = {"ed", "ex", "vi", "vim", "nvim", "nano", "emacs"}
DELETERS = {"rm", "unlink", "truncate", "shred", "rmdir"}
COPIERS = {"cp", "ln", "install", "rsync"}
# Commands that run the command after them; their own options are skipped.
PREFIXES = {"sudo", "env", "command", "builtin", "nohup", "time", "exec", "nice", "ionice", "timeout", "stdbuf", "xargs", "!", "{", "}",
            "then", "do", "else", "elif", "if", "while", "until"}
PREFIX_OPTIONS_WITH_VALUE = {"-I", "-n", "-P", "-d", "-L", "-s", "-E", "-u", "-c", "-C", "-k", "-o", "-i", "-e"}
ASSIGNMENT = re.compile(r"[A-Za-z_][A-Za-z0-9_]*=")
# sed -i, sed -ni.bak, sed --in-place; perl -i, perl -pi -e. Only option letters that take no value may come before the i.
IN_PLACE = {"sed": re.compile(r"-[nrEsuz]*i.*|--in-place.*"), "perl": re.compile(r"-[nplaswtTUWXcd]*i.*")}
HEREDOC = re.compile(r"<<(-?)[ \t]*(?:'([^']*)'|\"([^\"]*)\"|([^\s;&|()<>]+))")
TEXT_TOKEN = re.compile(r"[^\s'\"`;&|()<>=,:{}\[\]\\]+")


def _is_work_tree_root(folder: str) -> bool:
    # A folder that does not exist yet cannot be ruled out (a worktree about to be made, a path on another machine).
    return os.path.lexists(os.path.join(folder, ".git")) or not os.path.exists(folder)


def _match(token: str, candidates):
    t = token.strip()
    if not t:
        return None
    if t.startswith("~"):
        t = os.path.expanduser(t)
    if os.path.isabs(t):
        n = os.path.normpath(t)
        for p in candidates:
            if n.endswith("/" + p):
                root = n[: -len(p) - 1] or "/"
                if root != "/" and _is_work_tree_root(root):
                    return p
        return None
    n = os.path.normpath(t)
    return n if n in candidates else None


def protected(token: str):
    """The protected path a word names as a whole path, or None."""
    return _match(token, PROTECTED)


def names_in_text(text: str, diff: bool = False):
    """The protected paths a piece of program text, a patch or a heredoc names as whole paths."""
    found = []
    for token in TEXT_TOKEN.findall(text):
        if diff and token[:2] in ("a/", "b/"):
            token = token[2:]
        if token.startswith("/") and token[1:] in PROTECTED:
            token = token[1:]  # root + "/CLAUDE.md"
        p = protected(token)
        if p:
            found.append(p)
    return found


class Part:
    def __init__(self):
        self.words, self.writes, self.inputs, self.texts, self.subs = [], [], [], [], []

    def empty(self):
        return not (self.words or self.writes or self.inputs or self.texts or self.subs)


def _skip_quoted(s: str, i: int) -> int:
    """From an opening quote at i, the index after its closing quote."""
    q = s[i]
    i += 1
    while i < len(s) and s[i] != q:
        i += 2 if (q == '"' and s[i] == "\\") else 1
    return i + 1


def _skip_heredoc_bodies(s: str, i: int, delimiters) -> int:
    """From the start of a line, the index after the bodies of these heredocs."""
    for delimiter, strip in delimiters:
        while i < len(s):
            j = s.find("\n", i)
            j = len(s) if j < 0 else j
            line, i = s[i:j], j + 1
            if (line.lstrip("\t") if strip else line) == delimiter:
                break
    return i


def _closing(s: str, i: int, open_c: str, close_c: str) -> int:
    """From an opening bracket at i, the index of the bracket that closes it. Heredoc bodies inside are skipped: their text can hold
    quotes and brackets that do not pair (a commit message written with $(cat <<'EOF' ...))."""
    depth, heredocs = 0, []
    while i < len(s):
        c = s[i]
        if c == "\n" and heredocs:
            i, heredocs = _skip_heredoc_bodies(s, i + 1, heredocs), []
            continue
        if s.startswith("<<", i) and not s.startswith("<<<", i):
            m = HEREDOC.match(s, i)
            if m:
                heredocs.append((next(g for g in m.group(2, 3, 4) if g is not None), m.group(1) == "-"))
                i = m.end()
                continue
        if c in "'\"":
            i = _skip_quoted(s, i)
            continue
        if c == "\\":
            i += 2
            continue
        if c == open_c:
            depth += 1
        elif c == close_c:
            depth -= 1
            if depth == 0:
                return i
        i += 1
    return len(s) - 1


def split_parts(command: str):
    """The command's parts, each with its words, redirection targets, heredoc bodies and command substitutions."""
    parts = [Part()]
    heredocs = []  # (part, delimiter, strip tabs)
    s, n, i = command, len(command), 0
    buf, inword, redir = [], False, None

    def end_word():
        nonlocal buf, inword, redir
        if not inword:
            return
        word, buf, inword = "".join(buf), [], False
        p, op, redir = parts[-1], redir, None
        if op is None:
            p.words.append(word)
        elif op in (">", ">>", ">|", "&>", "&>>", "<>"):
            p.writes.append(word)
        elif op == ">&":
            if not re.fullmatch(r"[0-9]*-?", word):
                p.writes.append(word)
        elif op == "<":
            p.inputs.append(word)
        elif op == "<<<":
            p.texts.append(word)
        elif op in ("<<", "<<-"):
            heredocs.append((p, word, op == "<<-"))

    def new_part():
        nonlocal redir
        end_word()
        redir = None
        if not parts[-1].empty():
            parts.append(Part())

    def substitution(start, end):
        parts[-1].subs.append(s[start:end])

    while i < n:
        c = s[i]
        nxt = s[i + 1] if i + 1 < n else ""
        if c == "'":
            j = s.find("'", i + 1)
            j = n if j < 0 else j
            buf.append(s[i + 1:j])
            inword, i = True, j + 1
        elif c == '"':
            inword, i = True, i + 1
            while i < n and s[i] != '"':
                if s[i] == "\\" and i + 1 < n:
                    buf.append(s[i + 1])
                    i += 2
                elif s[i] == "$" and i + 1 < n and s[i + 1] == "(":
                    j = _closing(s, i + 1, "(", ")")
                    substitution(i + 2, j)
                    buf.append(s[i:j + 1])
                    i = j + 1
                elif s[i] == "`":
                    j = s.find("`", i + 1)
                    j = n if j < 0 else j
                    substitution(i + 1, j)
                    i = j + 1
                else:
                    buf.append(s[i])
                    i += 1
            i += 1
        elif c == "\\":
            if nxt and nxt != "\n":
                buf.append(nxt)
                inword = True
            i += 2
        elif c == "$" and nxt == "(":
            j = _closing(s, i + 1, "(", ")")
            substitution(i + 2, j)
            buf.append(s[i:j + 1])
            inword, i = True, j + 1
        elif c == "$" and nxt == "{":
            j = _closing(s, i + 1, "{", "}")
            buf.append(s[i:j + 1])
            inword, i = True, j + 1
        elif c == "`":
            j = s.find("`", i + 1)
            j = n if j < 0 else j
            substitution(i + 1, j)
            buf.append(s[i:j + 1])
            inword, i = True, j + 1
        elif c == "#" and not inword:
            j = s.find("\n", i)
            i = n if j < 0 else j
        elif c in " \t":
            end_word()
            i += 1
        elif c == "\n":
            new_part()
            i += 1
            for part, delimiter, strip in heredocs:
                end = _skip_heredoc_bodies(s, i, [(delimiter, strip)])
                part.texts.append(s[i:end])
                i = end
            heredocs = []
        elif c == "&" and nxt == ">":
            end_word()
            redir = "&>>" if s[i:i + 3] == "&>>" else "&>"
            i += len(redir)
        elif c in ";&|()":
            new_part()
            i += 2 if s[i:i + 2] in ("&&", "||", "|&", ";;") else 1
        elif c in "<>":
            if inword and "".join(buf).isdigit():
                buf, inword = [], False  # a file descriptor: 2>, 1>>
            else:
                end_word()
            for op in ("<<<", "<<-", ">>", "<<", ">|", ">&", "<&", "<>", ">", "<"):
                if s.startswith(op, i):
                    break
            redir = None if op == "<&" else op
            if op == "<&":
                # <&3 duplicates a descriptor: read and drop the word.
                j = i + 2
                while j < n and s[j] not in " \t\n;&|()":
                    j += 1
                i = j
                continue
            i += len(op)
        else:
            buf.append(c)
            inword, i = True, i + 1
    end_word()
    for part, _, _ in heredocs:  # a heredoc whose body was never reached
        part.texts.append("")
    return [p for p in parts if not p.empty()]


def command_of(words):
    """The command a part runs (its base name), its arguments, and whether xargs feeds it."""
    i, via_xargs = 0, False
    while i < len(words):
        w = words[i]
        if ASSIGNMENT.match(w):
            i += 1
            continue
        name = os.path.basename(w)
        if name not in PREFIXES:
            return name, words[i + 1:], via_xargs
        via_xargs = via_xargs or name == "xargs"
        i += 1
        while i < len(words) and (words[i].startswith("-") or (name == "env" and ASSIGNMENT.match(words[i]))
                                  or (name == "timeout" and words[i][:1].isdigit())):
            if words[i] in PREFIX_OPTIONS_WITH_VALUE:
                i += 1
            i += 1
    return "", [], via_xargs


def positional(args):
    out, options_over = [], False
    for a in args:
        if a == "--" and not options_over:
            options_over = True
        elif options_over or not a.startswith("-") or a == "-":
            out.append(a)
    return out


def _with_folders(paths):
    """The protected paths among these paths, and those inside the folders among them."""
    found = []
    for path in paths:
        p = protected(path)
        folder = None if p else _match(path.rstrip("/"), PROTECTED_DIRS)
        if p:
            found.append(p)
        elif folder:
            found += [x for x in PROTECTED if x.startswith(folder + "/")]
    return found


def _deleted(args):
    """Protected paths removed by deleting or moving these paths, folders that hold one included."""
    return _with_folders(positional(args))


def _destinations(args):
    """Protected paths written by cp, ln, install, rsync or mv with these arguments."""
    target_dir = None
    rest = []
    it = iter(range(len(args)))
    for k in it:
        a = args[k]
        if a == "-t" and k + 1 < len(args):
            target_dir = args[k + 1]
            next(it, None)
        elif a.startswith("--target-directory="):
            target_dir = a.split("=", 1)[1]
        else:
            rest.append(a)
    paths = positional(rest)
    if target_dir is None:
        if len(paths) < 2:
            return []
        dest, sources = paths[-1], paths[:-1]
        candidates = [dest]
    else:
        dest, sources, candidates = target_dir, paths, []
    found = [p for p in map(protected, candidates) if p]
    if target_dir is not None or dest.endswith("/") or dest in (".", "..") or os.path.isdir(dest):
        # Into a folder: the file (or folder, with -r) keeps its name there.
        found += _with_folders(os.path.join(dest, os.path.basename(src.rstrip("/"))) for src in sources)
    return found


def _patch_names(part, args):
    found = []
    for a in args + part.inputs:
        p = protected(a)
        if p:
            found.append(p)
        elif os.path.isfile(a):
            try:
                with open(a, encoding="utf-8", errors="replace") as f:
                    found += names_in_text(f.read(1 << 20), diff=True)
            except OSError:
                pass
    for t in part.texts:
        found += names_in_text(t, diff=True)
    return found


def _part_targets(part, all_words):
    found = [p for p in map(protected, part.writes) if p]
    name, args, via_xargs = command_of(part.words)
    if not name:
        return found
    if name == "tee" or name in SHELL_EDITORS:
        found += [p for p in map(protected, positional(args)) if p]
    elif name in DELETERS:
        found += _deleted(args) if name in ("rm", "rmdir") else [p for p in map(protected, positional(args)) if p]
    elif name == "mv":
        found += _deleted(args) + _destinations(args)
    elif name in COPIERS:
        found += _destinations(args)
    elif name == "dd":
        found += [p for p in (protected(a[3:]) for a in args if a.startswith("of=")) if p]
    elif name in IN_PLACE and any(IN_PLACE[name].fullmatch(a) for a in args):
        found += [p for p in map(protected, args) if p]
    elif name in ("awk", "gawk"):
        if "inplace" in args or "-iinplace" in args:
            found += [p for p in map(protected, args) if p]
        else:
            program = positional(args)[:1]
            found += [p for t in program for p in names_in_text(t)]
    elif name == "patch":
        found += _patch_names(part, args)
    elif name == "git":
        found += _git_targets(part, args)
    elif name == "find":
        if any(a in ("-delete", "-exec", "-execdir", "-ok", "-okdir", "-fprint", "-fprintf", "-fls") for a in args):
            bases = {os.path.basename(p): p for p in PROTECTED}
            found += [protected(a) or bases[a] for a in args if protected(a) or a in bases]
    elif INTERPRETER.fullmatch(name):
        for t in args + part.texts:
            found += names_in_text(t)
    if via_xargs and (name in DELETERS | COPIERS | SHELL_EDITORS | {"tee", "mv", "sed", "perl"} or INTERPRETER.fullmatch(name)):
        found += [p for w in all_words for p in names_in_text(w)]
    return found


def _git_targets(part, args):
    i = 0
    while i < len(args) and args[i].startswith("-"):
        i += 2 if args[i] in ("-C", "-c") else 1
    if i >= len(args):
        return []
    sub, rest = args[i], args[i + 1:]
    if sub in ("checkout", "restore"):
        return [p for p in map(protected, rest) if p]
    if sub in ("rm", "mv"):
        return _deleted(rest) + (_destinations(rest) if sub == "mv" else [])
    if sub in ("apply", "am"):
        return _patch_names(part, rest)
    return []


def targets(command: str, depth: int = 0):
    """The protected paths a command writes, in the order of PROTECTED."""
    try:
        parts = split_parts(command)
    except Exception:  # an unreadable command: fall back to any whole path it names
        return sorted(set(names_in_text(command)), key=PROTECTED.index)
    found = []
    all_words = [w for p in parts for w in p.words + p.texts]
    for part in parts:
        if depth < 8:
            for sub in part.subs:
                found += targets(sub, depth + 1)
        found += _part_targets(part, all_words)
    return sorted(set(found), key=PROTECTED.index)


def decide(command: str):
    named = targets(command)
    if not named:
        return None
    return {
        "hookSpecificOutput": {
            "hookEventName": "PreToolUse",
            "permissionDecision": "ask",
            "permissionDecisionReason": "This command may change " + ", ".join(named)
            + ", which only the owner approves. Allow it only if you agreed to this change.",
        }
    }


def main():
    try:
        payload = json.load(sys.stdin)
    except ValueError:
        return 0
    if payload.get("tool_name") != "Bash":
        return 0
    result = decide(str((payload.get("tool_input") or {}).get("command", "")))
    if result:
        print(json.dumps(result))
    return 0


if __name__ == "__main__":
    sys.exit(main())
