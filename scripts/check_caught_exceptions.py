#!/usr/bin/env python3
"""A `catch` that throws its own exception passes the caught one as the inner exception, so the original's message, code and stack are not lost.

This check reads every tracked C# file and fails, naming the file and the line, for each `throw new ...` inside a `catch` block whose arguments do
not pass the caught exception:

    catch (SqlException ex)
    {
        throw new StateMigrationException("Migration failed.");       // found: ex is lost
        throw new StateMigrationException("Migration failed.", ex);   // passes
    }

The caught exception counts as passed when its variable is one of the arguments of the `new` (anywhere in the argument list, so a named argument
`innerException: ex` or `ex.InnerException ?? ex` passes), or when it is passed as `ex.InnerException` (the wrapper of an `AggregateException` or a
`TargetInvocationException` carries nothing of its own). Its message alone (`ex.Message`, `$"...{ex}"`) does not count: the type, the stack and
the inner exceptions are lost. A `catch` without a variable (`catch { ... }`, `catch (IOException) { ... }`) that throws a new exception is found
too: it cannot pass what it did not name.

A throw belongs to the innermost `catch` that holds it. `throw;` and a `throw` of something other than `new` are not checked. Strings, characters
and comments are skipped, so a `catch` in a message or a comment is not one.

A `catch` that replaces an error on purpose, without the original, is listed in `REVIEWED` with the reason: the file, and a piece of the throw's
own text that names it, so the entry still holds when lines move. An entry that matches nothing any more fails the check.

The catches found when the check was added are in `scripts/caught_exceptions_baseline.txt`, one `path: count` per line: a file may hold up to that
many. The baseline only shrinks: a file with more fails the check and lists each of its throws, and a file with fewer fails it too, so the line is
lowered or deleted in the commit that fixes them.

Design: docs/error-handling.md#error-handling
Run from anywhere: python3 -I scripts/check_caught_exceptions.py [root]
"""
import os
import re
import subprocess
import sys

# Reviewed replacements: (path relative to the root with '/', a piece of the throw statement's text) -> why the original is left out.
REVIEWED = {
}

BASELINE = "scripts/caught_exceptions_baseline.txt"

IDENT = r"[A-Za-z_@][A-Za-z0-9_]*"


def tracked_cs_files(root):
    """Relative paths ('/') of the C# files, from git when `root` is a repository, else by walking it."""
    if os.path.exists(os.path.join(root, ".git")):
        try:
            out = subprocess.run(["git", "-C", root, "ls-files", "-z", "--", "*.cs"], capture_output=True, check=True).stdout.decode("utf-8", "replace")
            return sorted(p for p in out.split("\0") if p.endswith(".cs"))
        except (OSError, subprocess.CalledProcessError) as error:
            print(f"git ls-files failed ({error}); walking the folder instead", file=sys.stderr)
    files = []
    for folder, dirs, names in os.walk(root):
        dirs[:] = [d for d in dirs if d not in (".git", "bin", "obj", "node_modules")]
        for name in names:
            if name.endswith(".cs"):
                files.append(os.path.relpath(os.path.join(folder, name), root).replace(os.sep, "/"))
    return sorted(files)


def blank(text):
    """The text with every comment, string and character literal replaced by spaces (line breaks kept), so offsets and line numbers stay the same."""
    out = list(text)
    n = len(text)
    i = 0

    def wipe(start, end):
        for k in range(start, min(end, n)):
            if out[k] != "\n":
                out[k] = " "

    def skip_string(i):
        """`i` is at the start of a string literal (its prefix `$`, `@` or quotes); returns the offset after it."""
        j = i
        dollars = 0
        verbatim = False
        while j < n and text[j] in "$@":
            if text[j] == "$":
                dollars += 1
            else:
                verbatim = True
            j += 1
        quotes = 0
        while j + quotes < n and text[j + quotes] == '"':
            quotes += 1
        if quotes >= 3:
            # A raw string literal ends at the same number of quotes; with `$`, a hole is `{` repeated `dollars` times and may hold any code,
            # which raw strings in this repository do not use with quotes inside.
            end = text.find('"' * quotes, j + quotes)
            return n if end < 0 else end + quotes
        j += 1  # the opening quote
        if quotes == 2 and not dollars:
            return j + 1  # ""
        while j < n:
            c = text[j]
            if verbatim and c == '"':
                if j + 1 < n and text[j + 1] == '"':
                    j += 2
                    continue
                return j + 1
            if not verbatim and c == "\\":
                j += 2
                continue
            if not verbatim and c == '"':
                return j + 1
            if not verbatim and c == "\n":
                return j  # an unterminated string ends at the line
            if dollars and c == "{":
                if j + 1 < n and text[j + 1] == "{":
                    j += 2
                    continue
                j = skip_hole(j + 1)
                continue
            j += 1
        return n

    def skip_hole(j):
        """`j` is just inside an interpolation hole; returns the offset after its closing brace."""
        depth = 1
        while j < n:
            c = text[j]
            if c == '"' or (c in "$@" and j + 1 < n and text[j + 1] in '"$@'):
                j = skip_string(j)
                continue
            if c == "'":
                j = skip_char(j)
                continue
            if c == "{":
                depth += 1
            elif c == "}":
                depth -= 1
                if depth == 0:
                    return j + 1
            j += 1
        return n

    def skip_char(j):
        k = j + 1
        while k < n and k < j + 12:
            if text[k] == "\\":
                k += 2
                continue
            if text[k] == "'":
                return k + 1
            if text[k] == "\n":
                break
            k += 1
        return j + 1  # not a character literal (a stray quote): move on

    while i < n:
        c = text[i]
        if c == "/" and i + 1 < n and text[i + 1] == "/":
            end = text.find("\n", i)
            end = n if end < 0 else end
            wipe(i, end)
            i = end
        elif c == "/" and i + 1 < n and text[i + 1] == "*":
            end = text.find("*/", i + 2)
            end = n if end < 0 else end + 2
            wipe(i, end)
            i = end
        elif c == '"' or (c in "$@" and i + 1 < n and text[i + 1] in '"$@' and (i == 0 or not (text[i - 1].isalnum() or text[i - 1] == "_"))):
            end = skip_string(i)
            wipe(i, end)
            i = end
        elif c == "'":
            end = skip_char(i)
            wipe(i, end)
            i = end
        else:
            i += 1
    return "".join(out)


def matching(code, start, open_c, close_c):
    """`start` is at `open_c`; returns the offset of the matching `close_c`, or -1."""
    depth = 0
    for k in range(start, len(code)):
        if code[k] == open_c:
            depth += 1
        elif code[k] == close_c:
            depth -= 1
            if depth == 0:
                return k
    return -1


CATCH = re.compile(r"\bcatch\b")
CLAUSE = re.compile(rf"\(\s*(?P<type>[A-Za-z_@][A-Za-z0-9_.<>,?\s]*?)(?:\s+(?P<var>{IDENT}))?\s*\)")
THROW_NEW = re.compile(r"\bthrow\s+new\b")


def catches(code):
    """Each catch block: (the caught variable or None, the offset of its `{`, the offset of its `}`)."""
    found = []
    for m in CATCH.finditer(code):
        k = m.end()
        while k < len(code) and code[k].isspace():
            k += 1
        var = None
        if k < len(code) and code[k] == "(":
            end = matching(code, k, "(", ")")
            if end < 0:
                continue
            clause = CLAUSE.fullmatch(code[k:end + 1])
            if clause and clause.group("var"):
                var = clause.group("var")
            k = end + 1
            while k < len(code) and code[k].isspace():
                k += 1
        if code.startswith("when", k) and not (code[k + 4:k + 5].isalnum() or code[k + 4:k + 5] == "_"):
            k += 4
            while k < len(code) and code[k].isspace():
                k += 1
            if k >= len(code) or code[k] != "(":
                continue
            k = matching(code, k, "(", ")")
            if k < 0:
                continue
            k += 1
            while k < len(code) and code[k].isspace():
                k += 1
        if k >= len(code) or code[k] != "{":
            continue
        end = matching(code, k, "{", "}")
        if end > 0:
            found.append((var, k, end))
    return found


def passes(arguments, var):
    """Whether the argument text passes the caught exception `var` itself (or its InnerException), not only a member of it."""
    for m in re.finditer(rf"(?<![A-Za-z0-9_.@]){re.escape(var)}(?![A-Za-z0-9_])", arguments):
        rest = arguments[m.end():].lstrip()
        if not rest.startswith(".") and not rest.startswith("?."):
            return True
        member = re.match(r"\??\.\s*(\w+)", rest)
        if member and member.group(1) in ("InnerException", "InnerExceptions"):
            return True
    return False


def check_text(path, text, reviewed=None):
    """The problems of one C# file's text: `path:line: ...`."""
    reviewed = REVIEWED if reviewed is None else reviewed
    code = blank(text)
    blocks = catches(code)
    problems = []
    for m in THROW_NEW.finditer(code):
        inner = None
        for var, start, end in blocks:
            if start < m.start() < end and (inner is None or start > inner[1]):
                inner = (var, start, end)
        if inner is None:
            continue
        var = inner[0]
        k = m.end()
        type_match = re.compile(r"\s*([A-Za-z_@][A-Za-z0-9_.<>,\s]*?)\s*(?=[({;])").match(code, k)
        thrown = type_match.group(1).strip() if type_match else "?"
        k = type_match.end() if type_match else k
        arguments = ""
        if k < len(code) and code[k] == "(":
            end = matching(code, k, "(", ")")
            arguments = code[k:end + 1] if end > 0 else code[k:]
            k = end + 1 if end > 0 else len(code)
        while k < len(code) and code[k].isspace():
            k += 1
        if k < len(code) and code[k] == "{":  # an object initializer: `new X(...) { InnerException = ... }` is not possible, but `Data` may hold it
            end = matching(code, k, "{", "}")
            arguments += code[k:end + 1] if end > 0 else ""
        if var is not None and passes(arguments, var):
            continue
        statement_end = text.find(";", m.start())
        statement = text[m.start():statement_end if statement_end > 0 else len(text)]
        if any(p == path and piece in statement for (p, piece) in reviewed):
            continue
        line = text.count("\n", 0, m.start()) + 1
        if var is None:
            problems.append(f"{path}:{line}: throw new {thrown} inside a catch that names no variable: name the caught exception and pass it as the inner exception")
        else:
            problems.append(f"{path}:{line}: throw new {thrown} inside a catch does not pass the caught exception '{var}' as its inner exception")
    return problems


def load_baseline(root):
    """{path: allowed count}; lines starting with # and blank lines are comments."""
    result = {}
    path = os.path.join(root, BASELINE)
    if not os.path.exists(path):
        return result
    with open(path, encoding="utf-8") as f:
        for number, line in enumerate(f, 1):
            line = line.strip()
            if not line or line.startswith("#"):
                continue
            name, _, count = line.rpartition(":")
            if not name or not count.strip().isdigit():
                raise ValueError(f"{BASELINE}:{number}: expected `path: count`, got `{line}`")
            result[name.strip()] = int(count)
    return result


def findings(root, reviewed=None):
    """{path: [problem, ...]} for every C# file with a catch that does not pass the caught exception, and the problems of `REVIEWED` itself."""
    reviewed = REVIEWED if reviewed is None else reviewed
    found = {}
    own = []
    for path in tracked_cs_files(root):
        full = os.path.join(root, *path.split("/"))
        try:
            with open(full, encoding="utf-8-sig", errors="replace") as f:
                text = f.read()
        except OSError as error:
            own.append(f"{path}: cannot be read ({error})")
            continue
        problems = check_text(path, text, reviewed)
        if problems:
            found[path] = problems
    for (path, piece) in reviewed:
        full = os.path.join(root, *path.split("/"))
        try:
            with open(full, encoding="utf-8-sig", errors="replace") as f:
                text = f.read()
        except OSError:
            text = ""
        if piece not in text:
            own.append(f"{path}: the reviewed entry '{piece}' in scripts/check_caught_exceptions.py (REVIEWED) matches nothing any more: remove it")
    return found, own


def check(root, reviewed=None):
    """Returns (problems, warnings): problems fail the check; warnings are what the baseline still tolerates."""
    baseline = load_baseline(root)
    found, problems = findings(root, reviewed)
    warnings = []
    for path, lines in sorted(found.items()):
        allowed = baseline.get(path, 0)
        if len(lines) > allowed:
            if allowed:
                problems.append(f"{path}: {len(lines)} catches that lose the caught exception, the baseline allows {allowed}:")
            problems.extend(lines)
        elif len(lines) < allowed:
            problems.append(f"{path}: {len(lines)} catches that lose the caught exception left, the baseline says {allowed}; lower it in {BASELINE}")
        else:
            warnings.append(f"{path}: {len(lines)} (baseline)")
    for path in sorted(set(baseline) - set(found)):
        problems.append(f"{path}: in {BASELINE} but every catch in it passes the caught exception now (or it is not tracked); delete the line")
    return problems, warnings


def main(argv):
    root = os.path.abspath(argv[1]) if len(argv) > 1 else os.path.abspath(os.path.join(os.path.dirname(__file__), ".."))
    problems, warnings = check(root)
    if warnings:
        print(f"{len(warnings)} file(s) still hold catches that lose the caught exception, up to their count in {BASELINE}")
    for p in problems:
        print(p)
    if problems:
        print(f"{len(problems)} problem(s): a catch that throws its own exception passes the caught one as the inner exception (docs/error-handling.md)")
        return 1
    print("check_caught_exceptions: no catch outside the baseline throws a new exception without the caught one.")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
