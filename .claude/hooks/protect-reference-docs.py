#!/usr/bin/env python3
"""PreToolUse hook for Bash: asks the person before a shell command writes one of the owner-approved files.

The permission rules in .claude/settings.json ("ask" on Edit(...)) cover the file tools only. A shell command can write a file with sed -i, a
redirection, python and the like, so this hook reads the command and answers "ask" when it names a protected file and does something that can
write. A command that only reads a protected file (cat, grep, sed -n, git diff) runs as before. It cannot see a path that a command builds in
pieces; GitHub's code owners review is the guarantee at merge.
"""
import json
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

# Ways a shell command can change a file. A redirection to a file counts; "2>&1" and "2>/dev/null" do not.
WRITES = re.compile(
    r"(?:(?<![0-9&])>>?(?!&)"
    r"|\btee\b|\bsed\b[^|;&]*\s-i|\bperl\b[^|;&]*\s-[a-z]*i|\bpython3?\b|\bnode\b|\bcp\b|\bmv\b|\brm\b|\btruncate\b|\bdd\b|\bpatch\b"
    r"|\bgit\s+(?:checkout|restore|apply|mv|rm|reset|stash|merge|pull|cherry-pick|revert)\b)"
)


def decide(command: str):
    named = [p for p in PROTECTED if p in command]
    if not named or not WRITES.search(command):
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
