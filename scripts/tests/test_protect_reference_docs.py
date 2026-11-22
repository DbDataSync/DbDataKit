"""The Bash hook that asks before a shell command writes an owner-approved file (.claude/hooks/protect-reference-docs.py)."""
import importlib.util
import json
import os
import subprocess
import sys
import tempfile
import unittest

HOOK = os.path.join(os.path.dirname(__file__), "..", "..", ".claude", "hooks", "protect-reference-docs.py")
_spec = importlib.util.spec_from_file_location("protect_reference_docs", HOOK)
hook = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(hook)

NONE = []

# (command, the protected paths it writes; an empty list means the hook stays silent)
CASES = [
    # Reading, and commands that name a protected file without writing it.
    ("cat CLAUDE.md", NONE),
    ("grep -n x planning/ROLES.md > /tmp/out", NONE),
    ("sed -n 1,40p docs/architecture.md", NONE),
    ("git diff origin/main -- CLAUDE.md", NONE),
    ("git log -p .claude/settings.json 2>&1 | head", NONE),
    ("grep -rn x CLAUDE.md 2>/dev/null", NONE),
    ("python3 -I scripts/check_plans.py", NONE),
    ("python3 -I -m unittest discover -s scripts/tests", NONE),
    ("cat CLAUDE.md | python3 -I scripts/check_plans.py", NONE),
    ("cp CLAUDE.md /tmp/claude-copy.md", NONE),
    ("diff CLAUDE.md /tmp/x >/tmp/out 2>&1", NONE),
    ("echo 'see CLAUDE.md' > /tmp/note", NONE),
    ("cat <<'EOF' > /tmp/note\nedit CLAUDE.md and docs/architecture.md\nEOF", NONE),
    # The parts are judged one by one.
    ("git merge origin/main && grep -n x CLAUDE.md", NONE),
    ("git pull && cat planning/ROLES.md", NONE),
    ("rm -rf /tmp/x; grep x CLAUDE.md", NONE),
    ("python3 -I scripts/check_plans.py && cat docs/architecture.md", NONE),
    ("sed -i s/a/b/ notes.md && cat CLAUDE.md", NONE),
    ("echo CLAUDE.md\ntee /tmp/out < /dev/null", NONE),
    # Commits already approved come in without asking.
    ("git merge origin/main", NONE),
    ("git pull --ff-only origin main", NONE),
    ("git reset --hard origin/main", NONE),
    ("git stash && git stash pop", NONE),
    ("git cherry-pick abc123", NONE),
    ("git revert --no-edit abc123", NONE),
    # Only the whole path is a protected file.
    ("sed -i s/a/b/ DbDataBuild/CLAUDE.md", NONE),
    ("echo x >> DbDataBuild/CLAUDE.md", NONE),
    ("cp /tmp/x DbDataGateway/docs/architecture.md", NONE),
    ("echo x > foo/docs/architecture.md", NONE),
    ("rm DbDataSync/CLAUDE.md", NONE),
    ("python3 - <<'EOF'\nopen('DbDataBuild/CLAUDE.md','w').write('x')\nEOF", NONE),
    ("echo x > CLAUDE.md.bak", NONE),
    ("rm -rf /home/user/wt-x", NONE),
    # Redirections.
    ("echo x >> docs/architecture.md", ["docs/architecture.md"]),
    ("echo x > ./CLAUDE.md", ["CLAUDE.md"]),
    ("printf x >CLAUDE.md", ["CLAUDE.md"]),
    ("cat /tmp/x 1> planning/ROLES.md", ["planning/ROLES.md"]),
    ("cat /tmp/x &> CLAUDE.md", ["CLAUDE.md"]),
    ("cat <<'EOF' > CLAUDE.md\ntext\nEOF", ["CLAUDE.md"]),
    ("echo x > /home/user/wt-x/docs/architecture.md", ["docs/architecture.md"]),
    ("grep x /tmp/a && echo y >> \"planning/ROLES.md\"", ["planning/ROLES.md"]),
    ("echo $(cat /tmp/x > CLAUDE.md)", ["CLAUDE.md"]),
    ("(cd /tmp && echo x) > docs/architecture.md", ["docs/architecture.md"]),
    # tee, sed -i, perl -i, awk -i inplace.
    ("echo x | tee CLAUDE.md", ["CLAUDE.md"]),
    ("echo x | tee -a /tmp/log planning/ROLES.md", ["planning/ROLES.md"]),
    ("sed -i s/a/b/ CLAUDE.md", ["CLAUDE.md"]),
    ("sed -i.bak -e s/a/b/ docs/architecture.md", ["docs/architecture.md"]),
    ("sed --in-place s/a/b/ CLAUDE.md", ["CLAUDE.md"]),
    ("perl -pi -e s/a/b/ planning/ROLES.md", ["planning/ROLES.md"]),
    ("gawk -i inplace '{print}' CLAUDE.md", ["CLAUDE.md"]),
    # cp, mv, install, rsync, ln: the destination; mv also its source.
    ("cp /tmp/x .claude/settings.json", [".claude/settings.json"]),
    ("cp /tmp/CLAUDE.md .", ["CLAUDE.md"]),
    ("cp -t docs /tmp/architecture.md", ["docs/architecture.md"]),
    ("mv /tmp/x planning/ROLES.md", ["planning/ROLES.md"]),
    ("mv CLAUDE.md /tmp/", ["CLAUDE.md"]),
    ("install -m 644 /tmp/x .claude/hooks/protect-reference-docs.py", [".claude/hooks/protect-reference-docs.py"]),
    ("rsync -a /tmp/x CLAUDE.md", ["CLAUDE.md"]),
    ("ln -sf /tmp/x CLAUDE.md", ["CLAUDE.md"]),
    # rm, truncate, dd, and folders that hold a protected file.
    ("rm CLAUDE.md", ["CLAUDE.md"]),
    ("rm -f -- ./docs/architecture.md", ["docs/architecture.md"]),
    ("truncate -s 0 planning/ROLES.md", ["planning/ROLES.md"]),
    ("dd if=/dev/zero of=CLAUDE.md count=1", ["CLAUDE.md"]),
    ("rm -rf .claude/skills", [".claude/skills/product-docs/SKILL.md"]),
    ("git rm -r planning", ["planning/ROLES.md"]),
    ("find . -name CLAUDE.md -delete", ["CLAUDE.md"]),
    ("echo CLAUDE.md | xargs rm", ["CLAUDE.md"]),
    ("sudo rm CLAUDE.md", ["CLAUDE.md"]),
    ("FOO=1 rm CLAUDE.md", ["CLAUDE.md"]),
    # git commands that write the named paths.
    ("git checkout main -- CLAUDE.md", ["CLAUDE.md"]),
    ("git checkout origin/main -- docs/architecture.md planning/ROLES.md", ["planning/ROLES.md", "docs/architecture.md"]),
    ("git restore --source=main .claude/settings.json", [".claude/settings.json"]),
    ("git rm CLAUDE.md", ["CLAUDE.md"]),
    ("git mv docs/architecture.md docs/old.md", ["docs/architecture.md"]),
    ("git -C /tmp/wt checkout -- CLAUDE.md", ["CLAUDE.md"]),
    ("git apply <<'EOF'\n--- a/CLAUDE.md\n+++ b/CLAUDE.md\n@@ -1 +1 @@\n-a\n+b\nEOF", ["CLAUDE.md"]),
    ("patch -p1 <<'EOF'\n--- a/planning/ROLES.md\n+++ b/planning/ROLES.md\nEOF", ["planning/ROLES.md"]),
    ("git apply <<'EOF'\n--- a/DbDataBuild/CLAUDE.md\n+++ b/DbDataBuild/CLAUDE.md\nEOF", NONE),
    ("git checkout -- DbDataBuild/CLAUDE.md", NONE),
    # Interpreters: their part names a protected path.
    ("python3 - <<'EOF'\nopen('planning/ROLES.md','w').write('x')\nEOF", ["planning/ROLES.md"]),
    ("python3 -c \"open('CLAUDE.md','w')\"", ["CLAUDE.md"]),
    ("node -e \"require('fs').writeFileSync('docs/architecture.md','x')\"", ["docs/architecture.md"]),
    ("perl -e 'open F, \">\", \"CLAUDE.md\"'", ["CLAUDE.md"]),
    ("ruby -e 'File.write(\"CLAUDE.md\", 1)'", ["CLAUDE.md"]),
    ("bash -c 'echo x > CLAUDE.md'", ["CLAUDE.md"]),
    ("sh -c \"rm docs/architecture.md\"", ["docs/architecture.md"]),
    ("python3 scripts/x.py .claude/settings.json", [".claude/settings.json"]),
    ("python3 - <<'EOF'\nimport pathlib\npathlib.Path('/home/user/wt-x/CLAUDE.md').write_text('x')\nEOF", ["CLAUDE.md"]),
    ("git merge origin/main && python3 - <<'EOF' && grep x CLAUDE.md\nprint(1)\nEOF", NONE),
    # A commit message written with a heredoc inside $(...): its text is not a command, the parts after it are.
    ("git commit -m \"$(cat <<'EOF'\nHook: it's about CLAUDE.md (and docs/architecture.md\nEOF\n)\" && git push", NONE),
    ("git commit -m \"$(cat <<'EOF'\nit's (\nEOF\n)\" && rm CLAUDE.md", ["CLAUDE.md"]),
    ("x=$(python3 - <<EOF\nopen('CLAUDE.md','w')\nEOF\n)", ["CLAUDE.md"]),
    # Into a folder: the copied file or folder keeps its name.
    ("cp -r /tmp/docs .", ["docs/architecture.md"]),
    ("cp /tmp/x docs/", NONE),
]


class ProtectReferenceDocs(unittest.TestCase):
    def setUp(self):
        if os.path.exists("/home/user/wt-x"):
            self.skipTest("/home/user/wt-x exists on this machine; the table assumes it does not")

    def test_the_table(self):
        for command, expected in CASES:
            with self.subTest(command=command):
                self.assertEqual(expected, hook.targets(command))
                self.assertEqual(bool(expected), hook.decide(command) is not None)

    def test_an_absolute_path_counts_only_under_a_work_tree_root(self):
        tmp = tempfile.TemporaryDirectory()
        self.addCleanup(tmp.cleanup)
        root = tmp.name
        os.makedirs(os.path.join(root, "wt", ".git"))
        os.makedirs(os.path.join(root, "wt", "DbDataBuild"))
        os.makedirs(os.path.join(root, "plain", "docs"))
        os.makedirs(os.path.join(root, "linked"))
        with open(os.path.join(root, "linked", ".git"), "w") as f:
            f.write("gitdir: /elsewhere\n")  # a git worktree has a .git file
        self.assertEqual(["CLAUDE.md"], hook.targets(f"echo x > {root}/wt/CLAUDE.md"))
        self.assertEqual(["CLAUDE.md"], hook.targets(f"echo x > {root}/linked/CLAUDE.md"))
        self.assertEqual([], hook.targets(f"echo x > {root}/wt/DbDataBuild/CLAUDE.md"))
        self.assertEqual([], hook.targets(f"echo x > {root}/plain/docs/architecture.md"))

    def test_a_patch_file_is_read_for_the_paths_it_changes(self):
        with tempfile.NamedTemporaryFile("w", suffix=".patch", delete=False) as f:
            f.write("--- a/docs/architecture.md\n+++ b/docs/architecture.md\n")
        self.addCleanup(os.remove, f.name)
        self.assertEqual(["docs/architecture.md"], hook.targets(f"git apply {f.name}"))
        self.assertEqual(["docs/architecture.md"], hook.targets(f"patch -p1 < {f.name}"))

    def test_the_answer_asks_and_names_the_files(self):
        result = hook.decide("sed -i s/a/b/ CLAUDE.md docs/architecture.md")
        out = result["hookSpecificOutput"]
        self.assertEqual("PreToolUse", out["hookEventName"])
        self.assertEqual("ask", out["permissionDecision"])
        self.assertEqual("This command may change CLAUDE.md, docs/architecture.md, which only the owner approves. "
                         "Allow it only if you agreed to this change.", out["permissionDecisionReason"])

    def test_the_hook_reads_the_tool_call_from_standard_input(self):
        def run(payload):
            return subprocess.run([sys.executable, "-I", HOOK], input=json.dumps(payload), capture_output=True, text=True, check=True).stdout

        asked = json.loads(run({"tool_name": "Bash", "tool_input": {"command": "echo x >> CLAUDE.md"}}))
        self.assertEqual("ask", asked["hookSpecificOutput"]["permissionDecision"])
        self.assertEqual("", run({"tool_name": "Bash", "tool_input": {"command": "cat CLAUDE.md"}}))
        self.assertEqual("", run({"tool_name": "Read", "tool_input": {"file_path": "CLAUDE.md"}}))


if __name__ == "__main__":
    unittest.main()
