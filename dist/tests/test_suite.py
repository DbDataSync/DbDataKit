"""Tests for the suite manifest, `dist.py bind --suite` (dbdatakit-full.json, plan 33 section 6a, slice SC11).

The members here are fixtures (a root folder with one dist.json each), so the tests do not depend on which products have a dist.json yet;
`ShippedSuiteTests` checks the real dist/suite.json against the real files.
"""
import json
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest

DIST = pathlib.Path(__file__).resolve().parent.parent
REPO_ROOT = DIST.parent
PWSH = shutil.which("pwsh")     # the parse check runs only where PowerShell is installed (CI has it)
REPO = "acme/kit"
VERSION = "2026.2.2.0200"


def check_balanced(text):
    """A cheap syntax check for machines without PowerShell: braces, brackets and parentheses pair up outside strings, and every quote closes."""
    stack, pairs, i = [], {")": "(", "]": "[", "}": "{"}, 0
    while i < len(text):
        c = text[i]
        if text.startswith("<#", i):                                # a block comment
            i = text.index("#>", i) + 2
            continue
        if c == "#":                                                # a line comment (a # inside a string is skipped with the string)
            i = text.find("\n", i)
            if i < 0:
                break
            continue
        if c in "'\"":
            j = i + 1
            while j < len(text):
                if text[j] == c and text[j + 1:j + 2] == c:      # a doubled quote inside a quoted string
                    j += 2
                    continue
                if text[j] == c:
                    break
                if c == '"' and text[j] == "`":
                    j += 1
                j += 1
            else:
                raise AssertionError(f"a {c} quote opens and never closes near: {text[i:i + 60]!r}")
            i = j
        elif c in "([{":
            stack.append(c)
        elif c in ")]}":
            if not stack or stack.pop() != pairs[c]:
                raise AssertionError(f"unbalanced {c} near: {text[max(0, i - 60):i + 1]!r}")
        i += 1
    if stack:
        raise AssertionError(f"unclosed {stack}")


def member(product, exe=None, service=None, vc=False, win=True, env=None):
    meta = {
        "product": product,
        "displayName": product,
        "description": f"The {product}.",
        "homepage": f"https://example.test/{product}",
        "license": "MIT",
        "platforms": {"linux-x64": {"exe": product}},
    }
    if win:
        meta["platforms"]["win-x64"] = {"exe": exe or product + ".exe"}
    if service:
        meta["service"] = service
    if vc:
        meta["requiresVcRuntime"] = True
    if env:
        meta["globalEnv"] = env
    return meta


MEMBERS = [
    ("dbdatakit", "console", member("dbdatakit", service={"name": "KitConsole", "register": "service install"}, env=[{"name": "DBDATAKIT_DATA", "programData": "DbDataKit"}, {"name": "KIT_DIR", "programData": "Kit\\console"}])),
    ("dbdatagateway", "gateway", member("dbdatagateway", service={"name": "gw", "register": "service install --name {name}"}, vc=True, env=[{"name": "DBDATAKIT_DATA", "programData": "DbDataKit"}])),
    ("dbdatabuild", "build", member("dbdatabuild", env=[{"name": "DBDATAKIT_DATA", "programData": "DbDataKit"}, {"name": "BUILD_DIR", "programData": "Kit\\build"}])),          # no service: it is paired but not registered
]


def hashes(version=VERSION, products=None):
    return {f"{m['product']}-v{version}-win-x64.zip": f"{i:02x}" * 32 for i, (_, _, m) in enumerate(MEMBERS, 1) if not products or m["product"] in products}


def sums_text(table):
    return "".join(f"{h}  {n}\n" for n, h in table.items())


def lay_out(root, members=MEMBERS, skip=()):
    entries = []
    for product, folder, meta in members:
        rel = f"{product}/dist.json"
        if product not in skip:
            (root / product).mkdir(exist_ok=True)
            (root / rel).write_text(json.dumps(meta))
        entries.append({"product": product, "meta": rel, "folder": folder})
    suite = root / "suite.json"
    suite.write_text(json.dumps({"members": entries}))
    return suite


def run_bind(suite, root, out, version, sums_files, repo=REPO):
    cmd = [sys.executable, "-I", str(DIST / "dist.py"), "bind", "--suite", str(suite), "--root", str(root), "--out", str(out), "--repo", repo]
    if version:
        cmd += ["--version", version, "--sums", *map(str, sums_files)]
    return subprocess.run(cmd, capture_output=True, text=True)


class SuiteManifestTests(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp(prefix="suite-tests-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(self.tmp, ignore_errors=True))
        self.suite = lay_out(self.tmp)

    def bind(self, version=VERSION, table=None, split=False):
        table = hashes(version) if table is None else table
        files = []
        if split:   # one SHA256SUMS per member, as each product's release has
            for i, (name, h) in enumerate(table.items()):
                f = self.tmp / f"sums{i}"
                f.write_text(sums_text({name: h}))
                files.append(f)
        else:
            f = self.tmp / "SHA256SUMS"
            f.write_text(sums_text(table))
            files.append(f)
        out = self.tmp / "out"
        r = run_bind(self.suite, self.tmp, out, version, files)
        return r, out / "dbdatakit-full.json"

    def manifest(self, **kw):
        r, path = self.bind(**kw)
        self.assertEqual(r.returncode, 0, r.stderr)
        return json.loads(path.read_text())

    def test_every_member_zip_is_listed_with_its_hash_in_order(self):
        m = self.manifest()
        b = m["architecture"]["64bit"]
        self.assertEqual(m["version"], VERSION)
        self.assertEqual(b["url"], [f"https://github.com/{REPO}/releases/download/{p}/v{VERSION}/{p}-v{VERSION}-win-x64.zip" for p, _, _ in MEMBERS])
        self.assertEqual(b["hash"], [f"{i:02x}" * 32 for i in (1, 2, 3)])

    def test_the_lists_have_equal_length_and_order(self):
        m = self.manifest()
        n = len(MEMBERS)
        self.assertEqual(len(m["architecture"]["64bit"]["url"]), n)
        self.assertEqual(len(m["architecture"]["64bit"]["hash"]), n)
        self.assertEqual(m["extract_to"], ["console", "gateway", "build"])
        self.assertEqual(m["bin"], [["console\\dbdatakit.exe", "dbdatakit"], ["gateway\\dbdatagateway.exe", "dbdatagateway"], ["build\\dbdatabuild.exe", "dbdatabuild"]])
        for url, folder, (exe, alias) in zip(m["architecture"]["64bit"]["url"], m["extract_to"], m["bin"]):
            self.assertTrue(exe.startswith(folder + "\\"))
            self.assertIn(f"/{alias}/v", url)

    def test_per_member_sums_files_give_the_same_manifest(self):
        self.assertEqual(self.manifest(split=True), self.manifest(split=False))

    def test_it_has_the_two_scripts_and_no_installer_keys(self):
        m = self.manifest()
        for key in ("installer", "uninstaller", "persist"):
            self.assertNotIn(key, m)
        self.assertEqual(sorted(m), ["architecture", "bin", "description", "extract_to", "homepage", "license", "notes", "post_install", "pre_uninstall", "suggest", "version"])
        self.assertTrue(all(isinstance(line, str) for line in m["post_install"] + m["pre_uninstall"]))

    def test_the_bound_member_list_is_in_start_order_with_each_services_commands(self):
        post = "\n".join(self.manifest()["post_install"])
        self.assertLess(post.index("Product = 'dbdatakit'"), post.index("Product = 'dbdatagateway'"))
        self.assertLess(post.index("Product = 'dbdatagateway'"), post.index("Product = 'dbdatabuild'"))
        self.assertIn("Service = 'KitConsole'; Register = 'service install'; Unregister = ''", post)
        self.assertIn("Register = 'service install --name gw'", post)       # {name} is substituted
        self.assertIn("Service = ''; Register = ''", post)                   # a member with no service is bound without one and left alone
        self.assertIn("Roles = 'viewer,operator,admin'", post)
        self.assertIn("Address = 'https://localhost:8816'", post)

    def test_the_install_checks_before_it_changes_anything(self):
        post = "\n".join(self.manifest()["post_install"])
        refuse_user = post.index("if (-not $global)")
        refuse_route = post.index("is registered from")
        env = post.index("SetEnvironmentVariable('DBDATAKIT_DATA'")
        first_change = post.index("Registered $m")
        self.assertLess(refuse_user, refuse_route)
        self.assertLess(refuse_route, env)
        self.assertLess(refuse_route, first_change)
        self.assertIn("IsInRole", post)
        self.assertIn("Use one install route per machine", post)

    def test_the_install_registers_starts_the_console_then_pairs_and_starts_the_others(self):
        post = "\n".join(self.manifest()["post_install"])
        self.assertLess(post.index("Registered $m"), post.index("Started $consoleMember"))
        self.assertLess(post.index("Started $consoleMember"), post.index("Paired $m"))
        for word in ("'server', 'pair', '--max-role'", "'instance', 'add'", "'instance', 'list', '--offline'", "'health', 'check'", "Start-Service"):
            self.assertIn(word, post)
        self.assertIn("is listed by the console already", post)        # idempotent
        self.assertIn("is registered already", post)
        self.assertIn("$failed", post)                                 # one member's failure names it and leaves the rest

    def test_no_pairing_code_reaches_a_file_the_log_or_a_message(self):
        post = "\n".join(self.manifest()["post_install"])
        for word in ("Out-File", "Set-Content", "Add-Content", "Tee-Object", "Export-", ">>", " > "):
            self.assertNotIn(word, post)
        for line in post.splitlines():
            if "$code" in line:                                        # the only uses: read it, pass it to `instance add`, scrub it from a message, drop it
                self.assertTrue(any(k in line for k in ("Groups[1].Value", "'--code', $code", "Scrub $added.Output $code", "$code = $null")), line)
            if line.lstrip().startswith(("Say ", "Write-Host", "Write-Warning")):
                self.assertNotIn("$code", line)
                self.assertNotIn("$made.Output", line)

    def test_an_uninstall_unpairs_and_unregisters_in_reverse_and_an_update_only_stops(self):
        undo = "\n".join(self.manifest()["pre_uninstall"])
        real = undo.index("if ($cmd -eq 'uninstall')")
        self.assertLess(real, undo.index("'instance', 'remove'"))
        self.assertLess(undo.index("'instance', 'remove'"), undo.index("'server', 'unpair'"))
        self.assertLess(undo.index("'server', 'unpair'"), undo.index("Stopped $m.Service"))
        self.assertLess(undo.index("Stopped $m.Service"), undo.index("$m.Unregister"))
        self.assertIn("[array]::Reverse", undo)
        update = undo[undo.index("} else {", real):].split("SetEnvironmentVariable")[0]
        for word in ("Unregister", "unpair", "instance"):
            self.assertNotIn(word, update)
        self.assertNotIn("Remove-Item", undo)           # data under %ProgramData% stays

    def test_the_services_run_from_scoops_current_folder(self):
        post = "\n".join(self.manifest()["post_install"])
        self.assertIn("Join-Path $appDir 'current'", post)

    def test_the_scripts_are_balanced_powershell(self):
        m = self.manifest()
        for script in (m["post_install"], m["pre_uninstall"]):
            check_balanced("\n".join(script))
        if PWSH:
            for name in ("post_install", "pre_uninstall"):
                f = self.tmp / f"{name}.ps1"
                f.write_text("\n".join(m[name]) + "\n", encoding="utf-8")
                r = subprocess.run([PWSH, "-NoProfile", "-Command", f"$e=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('{f}',[ref]$null,[ref]$e);if($e){{$e|Out-String;exit 1}}"], capture_output=True, text=True)
                self.assertEqual(r.returncode, 0, r.stdout + r.stderr)

    def test_the_variables_are_every_members_global_env_in_order(self):
        m = self.manifest()
        script = "\n".join(m["post_install"])
        self.assertLess(script.index("KIT_DIR"), script.index("BUILD_DIR"))
        self.assertIn("Kit\\console", script)
        self.assertIn("Kit\\build", script)
        undo = "\n".join(m["pre_uninstall"])
        self.assertIn("KIT_DIR", undo)
        self.assertIn("BUILD_DIR", undo)
        self.assertIn("Open a new terminal", m["notes"])

    def test_the_shared_data_root_is_set_once_and_never_removed_on_uninstall(self):
        m = self.manifest()
        post = "\n".join(m["post_install"])
        undo = "\n".join(m["pre_uninstall"])
        # three members name it; the suite's script sets it once
        self.assertEqual(post.count("SetEnvironmentVariable('DBDATAKIT_DATA'"), 1)
        self.assertEqual(m["notes"].count("DBDATAKIT_DATA=%ProgramData%\\DbDataKit"), 1)
        self.assertNotIn("SetEnvironmentVariable('DBDATAKIT_DATA'", undo)
        self.assertIn("KIT_DIR", undo)
        self.assertIn("DBDATAKIT_DATA is left in place", m["notes"])

    def test_members_that_disagree_on_a_variable_are_refused_naming_it(self):
        members = [(p, f, dict(meta, globalEnv=[{"name": "DBDATAKIT_DATA", "programData": "Elsewhere"}]) if p == "dbdatabuild" else meta) for p, f, meta in MEMBERS]
        self.suite = lay_out(self.tmp, members=members)
        r, _ = self.bind()
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("DBDATAKIT_DATA", r.stderr)

    def test_a_suite_with_no_global_env_has_scripts_but_no_variable_lines(self):
        self.suite = lay_out(self.tmp, members=[(p, f, member(p, service={"name": "S" + p, "register": "service install"})) for p, f, _ in MEMBERS])
        r, path = self.bind()
        self.assertEqual(r.returncode, 0, r.stderr)
        m = json.loads(path.read_text())
        self.assertNotIn("SetEnvironmentVariable", "\n".join(m["post_install"] + m["pre_uninstall"]))
        self.assertIn("$MachineVars = @()", m["post_install"])

    def test_the_notes_name_each_service_and_each_pairing_by_hand(self):
        notes = self.manifest()["notes"]
        self.assertIn("dbdatakit service install", notes)
        self.assertIn("dbdatagateway service install --name gw", notes)
        self.assertNotIn("dbdatabuild service install", notes)      # dbdatabuild lists no service
        self.assertIn("dbdatakit server run", notes)
        self.assertIn("dbdatagateway server pair --max-role", notes)
        self.assertIn("dbdatabuild server pair --max-role", notes)
        self.assertNotIn("dbdatakit server pair", notes)             # the console is not paired with itself
        # each member's management address is the product's default (the Gateway's is https://localhost:8816, on by default); a member this file knows no default for stays generic
        self.assertIn("dbdatakit instance add https://localhost:8816 --code <code>", notes)
        self.assertIn("dbdatakit instance add https://127.0.0.1:8470 --code <code>", notes)
        self.assertNotIn("<https address of that server>", notes)
        self.assertLess(notes.index("dbdatagateway server pair"), notes.index("instance add https://localhost:8816"))
        self.assertLess(notes.index("dbdatabuild server pair"), notes.index("instance add https://127.0.0.1:8470"))
        self.assertIn("one install route per machine", notes)
        self.assertLess(notes.index("dbdatakit service install"), notes.index("dbdatagateway service install"))
        self.assertLess(notes.index("dbdatagateway service install"), notes.index("dbdatagateway server pair"))

    def test_the_visual_cpp_runtime_is_suggested_when_any_member_needs_it(self):
        self.assertEqual(self.manifest()["suggest"], {"Visual C++ Redistributable": ["extras/vcredist2022"]})
        members = [(p, f, {k: v for k, v in m.items() if k != "requiresVcRuntime"}) for p, f, m in MEMBERS]
        self.suite = lay_out(self.tmp, members)
        self.assertNotIn("suggest", self.manifest())

    def test_a_member_with_no_win_x64_zip_in_the_sums_is_refused_naming_it(self):
        table = hashes()
        del table[f"dbdatagateway-v{VERSION}-win-x64.zip"]
        r, path = self.bind(table=table)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("dbdatagateway", r.stderr)
        self.assertNotIn("dbdatabuild (", r.stderr)
        self.assertFalse(path.exists())

    def test_a_member_with_no_win_x64_platform_is_refused_naming_it(self):
        members = MEMBERS[:2] + [("dbdatabuild", "build", member("dbdatabuild", win=False))]
        self.suite = lay_out(self.tmp, members)
        r, _ = self.bind()
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("dbdatabuild", r.stderr)
        self.assertIn("win-x64", r.stderr)

    def test_a_missing_member_dist_json_is_refused_naming_it(self):
        (self.tmp / "dbdatagateway" / "dist.json").unlink()
        self.suite = lay_out(self.tmp, skip=("dbdatagateway",))
        r, path = self.bind()
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("dbdatagateway", r.stderr)
        self.assertIn("dbdatagateway/dist.json", r.stderr)
        self.assertFalse(path.exists())

    def test_stable_beta_and_snapshot_versions_pin_their_own_zips(self):
        for version in (VERSION, "2026.2.2.0200-beta", "2026.2.2.0200-snapshot.gabc1234"):
            m = self.manifest(version=version)
            self.assertEqual(m["version"], version)
            for url, (p, _, _) in zip(m["architecture"]["64bit"]["url"], MEMBERS):
                self.assertTrue(url.endswith(f"/{p}/v{version}/{p}-v{version}-win-x64.zip"), url)

    def test_the_manifest_is_valid_json_with_what_scoop_reads_from_it(self):
        m = self.manifest()
        b = m["architecture"]["64bit"]
        for key in ("version", "description", "homepage", "license", "notes"):
            self.assertIsInstance(m[key], str)
        self.assertEqual(len({len(b["url"]), len(b["hash"]), len(m["extract_to"])}), 1)
        self.assertEqual(len(set(m["extract_to"])), len(m["extract_to"]))
        self.assertTrue(all(len(h) == 64 and set(h) <= set("0123456789abcdef") for h in b["hash"]))
        self.assertTrue(all(len(pair) == 2 and all(isinstance(x, str) for x in pair) for pair in m["bin"]))
        self.assertEqual(len({alias for _, alias in m["bin"]}), len(m["bin"]))

    def test_a_suite_with_no_version_or_sums_is_refused(self):
        r = run_bind(self.suite, self.tmp, self.tmp / "out", None, [])
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("--version", r.stderr)


class SuiteInstallTestPlumbingTests(unittest.TestCase):
    """What the Windows install test (dist/tests/suite-scoop-e2e.ps1, job suite-install-windows) relies on, checked here because the job itself needs Windows."""

    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp(prefix="suite-plumbing-"))
        self.addCleanup(lambda: shutil.rmtree(self.tmp, ignore_errors=True))
        self.suite = lay_out(self.tmp)

    def test_a_base_url_serves_the_zips_from_another_address(self):
        files = [self.tmp / "SHA256SUMS"]
        files[0].write_text(sums_text(hashes()))
        out = self.tmp / "local"
        cmd = [sys.executable, "-I", str(DIST / "dist.py"), "bind", "--suite", str(self.suite), "--root", str(self.tmp), "--out", str(out), "--version", VERSION, "--sums", str(files[0]), "--base-url", "http://127.0.0.1:8099/"]
        r = subprocess.run(cmd, capture_output=True, text=True)
        self.assertEqual(r.returncode, 0, r.stderr)
        m = json.loads((out / "dbdatakit-full.json").read_text())
        self.assertEqual(m["architecture"]["64bit"]["url"], [f"http://127.0.0.1:8099/{p}/v{VERSION}/{p}-v{VERSION}-win-x64.zip" for p, _, _ in MEMBERS])

    def test_the_script_is_balanced_and_uses_what_the_manifest_has(self):
        text = (DIST / "tests" / "suite-scoop-e2e.ps1").read_text(encoding="utf-8")
        check_balanced(text)
        for word in ("--base-url", "scoop install -g", "scoop update", "scoop uninstall -g", "'Pairing code:'", "is registered already", "is listed by the console already", "health check", "FIRST-RUN.txt", "instance list"):
            self.assertIn(word, text)
        # the lines the repeat step looks for are the ones the install script says
        script = (DIST / "suite-scripts.ps1").read_text(encoding="utf-8")
        self.assertIn("is registered already", script)
        self.assertIn("is listed by the console already", script)

    def test_the_job_runs_after_the_dry_run_checks_and_only_in_a_dry_run(self):
        text = (REPO_ROOT / ".github" / "workflows" / "suite-release.yml").read_text(encoding="utf-8")
        job = text[text.index("  suite-install-windows:"):]
        self.assertIn("needs: [guard, verify]", job)
        self.assertIn("inputs.dry_run", job)
        self.assertIn("windows-latest", job)
        self.assertIn("suite-scoop-e2e.ps1", job)

    def test_the_script_parses_in_powershell(self):
        if not PWSH:
            self.skipTest("pwsh not available")
        for name in (DIST / "tests" / "suite-scoop-e2e.ps1", DIST / "suite-scripts.ps1"):
            r = subprocess.run([PWSH, "-NoProfile", "-Command", f"$e=$null;[void][System.Management.Automation.Language.Parser]::ParseFile('{name}',[ref]$null,[ref]$e);if($e){{$e|Out-String;exit 1}}"], capture_output=True, text=True)
            self.assertEqual(r.returncode, 0, r.stdout + r.stderr)


class ShippedSuiteTests(unittest.TestCase):
    PRODUCTS = ["dbdatakit", "dbdatagateway", "dbdatarunner", "dbdatasync", "dbdatabuild", "dbdatastore", "dbdataworkspace"]

    def setUp(self):
        self.entries = json.loads((DIST / "suite.json").read_text())["members"]

    def test_the_members_are_the_products_in_start_order(self):
        self.assertEqual([e["product"] for e in self.entries], self.PRODUCTS)
        folders = [e["folder"] for e in self.entries]
        self.assertEqual(len(set(folders)), len(folders))
        self.assertEqual(folders[0], "console")

    def test_every_member_that_has_a_dist_json_binds_and_the_others_are_refused_by_name(self):
        tmp = pathlib.Path(tempfile.mkdtemp(prefix="suite-shipped-"))
        self.addCleanup(lambda: __import__("shutil").rmtree(tmp, ignore_errors=True))
        lines, missing = [], []
        for e in self.entries:
            path = REPO_ROOT / e["meta"]
            if not path.is_file():
                missing.append(e)
                continue
            meta = json.loads(path.read_text())
            self.assertEqual(meta["product"], e["product"])
            lines.append(f"{'ab' * 32}  {e['product']}-v{VERSION}-win-x64.zip\n")
        sums = tmp / "SHA256SUMS"
        sums.write_text("".join(lines))
        r = run_bind(DIST / "suite.json", REPO_ROOT, tmp / "out", VERSION, [sums])
        if missing:      # until every member's dist.json exists, a real bind fails loudly
            self.assertNotEqual(r.returncode, 0)
            self.assertIn(missing[0]["product"], r.stderr)
        else:
            self.assertEqual(r.returncode, 0, r.stderr)
            m = json.loads((tmp / "out" / "dbdatakit-full.json").read_text())
            self.assertEqual(len(m["bin"]), len(self.PRODUCTS))


if __name__ == "__main__":
    unittest.main()
