"""Tests for the standard distribution files: dist.py binding, install.sh and install.ps1.

The installers run for real against a local fake of the GitHub releases API that serves real zip files, so what is exercised is
release selection (tag prefix, prerelease rules), checksum verification, file replacement and configuration preservation.
install.ps1 tests need pwsh or Windows PowerShell (set PWSH to its path; CI runs both) and are skipped without it. The bash tests run on
Linux and macOS only.
"""
import hashlib
import http.server
import io
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import threading
import unittest
import zipfile

DIST = pathlib.Path(__file__).resolve().parent.parent
PWSH = os.environ.get("PWSH") or shutil.which("pwsh")
REPO = "acme/kit"

META = {
    "product": "widget",
    "displayName": "Widget",
    "description": "A test widget.",
    "homepage": "https://example.test/widget",
    "license": "MIT",
    "platforms": {"win-x64": {"exe": "widget.exe"}, "linux-x64": {"exe": "widget"}},
    "windowsScope": "user",
    "preserve": ["settings.json"],
    "requiresDotnet": "10.0",
    "requiresVcRuntime": True,
    "service": {"name": "WidgetSvc", "register": "service install"},
    "notes": "Read the manual; it's short.",
}


def make_zip(files):
    buf = io.BytesIO()
    with zipfile.ZipFile(buf, "w") as z:
        for name, content in files.items():
            z.writestr(name, content)
    return buf.getvalue()


class FakeGitHub:
    """Serves /repos/<repo>/releases and the asset downloads."""

    def __init__(self):
        self.releases = []   # api dicts, newest first
        self.clock = 0
        self.blobs = {}      # path -> bytes
        outer = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass

            def do_GET(self):
                path = self.path.split("?")[0]
                query = self.path.partition("?")[2]
                if path == f"/repos/{REPO}/releases":
                    page = 1
                    for kv in query.split("&"):
                        if kv.startswith("page="):
                            page = int(kv[5:])
                    body = json.dumps(outer.releases if page == 1 else [], separators=(",", ":")).encode()   # the real API is compact
                elif path in outer.blobs:
                    body = outer.blobs[path]
                else:
                    self.send_error(404)
                    return
                self.send_response(200)
                if path.endswith("/releases"):
                    self.send_header("Content-Type", "application/json; charset=utf-8")
                self.send_header("Content-Length", str(len(body)))
                self.end_headers()
                self.wfile.write(body)

        self.server = http.server.ThreadingHTTPServer(("127.0.0.1", 0), Handler)
        self.base = f"http://127.0.0.1:{self.server.server_address[1]}"
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def close(self):
        self.server.shutdown()
        self.server.server_close()

    def add_release(self, product, version, *, prerelease=False, draft=False, exe_payload=b"v", corrupt=False, crlf=False, rids=("win-x64", "linux-x64"), platforms=None):
        tag = f"{product}/v{version}"
        assets, sums = [], []
        for rid in rids:
            exe = (platforms or META["platforms"])[rid]["exe"]
            data = make_zip({exe: exe_payload, "settings.json": b'{"shipped": true}', "lib/a.txt": b"a"})
            name = f"{product}-v{version}-{rid}.zip"
            sums.append(f"{hashlib.sha256(data).hexdigest()}  {name}")
            if corrupt:
                sums[-1] = f"{'0' * 64}  {name}"
            self._asset(assets, tag, name, data)
        self._asset(assets, tag, "SHA256SUMS", (("\r\n" if crlf else "\n").join(sums) + ("\r\n" if crlf else "\n")).encode())
        self._asset(assets, tag, "install.sh", b"#!/bin/sh\n")   # unrelated asset
        self.clock += 1
        self.releases.insert(0, {"tag_name": tag, "name": tag, "draft": draft, "prerelease": prerelease,
                                 "published_at": f"2026-10-19T12:{self.clock:02d}:00Z", "body": 'text with "tag_name": inside',
                                 "assets": assets})

    def _asset(self, assets, tag, name, data):
        path = f"/dl/{tag}/{name}"
        self.blobs[path] = data
        assets.append({"name": name, "browser_download_url": self.base + path})


def bind(out, meta=META, version=None, sums=None):
    meta_path = out / "dist.json"
    meta_path.write_text(json.dumps(meta))
    cmd = [sys.executable, "-I", str(DIST / "dist.py"), "bind", "--meta", str(meta_path), "--out", str(out), "--repo", REPO]
    if version:
        sums_path = out / "SHA256SUMS"
        sums_path.write_text(sums)
        cmd += ["--version", version, "--sums", str(sums_path)]
    subprocess.run(cmd, check=True, capture_output=True)


class DistTestCase(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = pathlib.Path(tempfile.mkdtemp(prefix="dist-tests-"))
        bind(cls.tmp)
        cls.gh = FakeGitHub()
        cls.gh.add_release("widget", "2026.1.1.0100", crlf=True)   # sums written by PowerShell on Windows
        cls.gh.add_release("other", "2026.9.9.0900")                               # another product in the same repo
        cls.gh.add_release("widget", "2026.2.2.0200", exe_payload=b"stable-new")
        cls.gh.add_release("widget", "2026.3.3.0300-beta", prerelease=True, exe_payload=b"beta")
        cls.gh.add_release("widget", "2026.4.4.0400", draft=True)                  # never offered
        cls.gh.add_release("widget", "2026.5.5.0500", corrupt=True, prerelease=True, exe_payload=b"corrupt")

    @classmethod
    def tearDownClass(cls):
        cls.gh.close()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def fresh(self):
        d = pathlib.Path(tempfile.mkdtemp(prefix="dist-case-", dir=self.tmp))
        return d


REPO_ROOT = DIST.parent


class ShippedGlobalEnvTests(unittest.TestCase):
    """The folders a global Scoop install points the products at are the ones the products default to for a service, so the two cannot drift (plan 33 SC31):
    every manifest sets the one machine variable DBDATAKIT_DATA, and each service's folder is <that>\\<its name> by the one reader (SuiteEnvironment)."""

    SHARED = [{"name": "DBDATAKIT_DATA", "programData": "DbDataKit"}]
    # product folder -> the service name its folder has under the root
    SERVICES = {"DbDataKit": "server", "DbDataRunner": "runner", "DbDataStore": "store", "DbDataWorkspace": "workspace", "DbDataSync": "sync", "DbDataBuild": "build"}

    def meta(self, path):
        return json.loads((REPO_ROOT / path).read_text(encoding="utf-8"))

    def test_every_manifest_that_sets_a_variable_sets_the_one_shared_root(self):
        for folder in self.SERVICES:
            self.assertEqual(self.meta(f"{folder}/dist.json")["globalEnv"], self.SHARED, folder)

    def test_the_root_and_the_services_folder_are_what_the_source_reads_and_registers(self):
        engines = (REPO_ROOT / "Shared/src/DbDataKit.State.Engines/SuiteEnvironment.cs").read_text(encoding="utf-8")
        self.assertIn('DataRootVariable = "DBDATAKIT_DATA"', engines)
        # the machine folder is <ProgramData>\DbDataKit\<service>: the same text the manifests' programData names
        self.assertIn('Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.Create), "DbDataKit", name)', engines)
        self.assertIn('/var/lib/dbdatakit/{name}', engines)
        listed = engines.split("Services { get; }")[1].split("]")[0]
        for service in list(self.SERVICES.values()) + ["gateway", "build", "identity"]:
            self.assertIn(f'"{service}"', listed)

    def test_each_service_registers_the_folder_the_shared_reader_gives_it(self):
        for folder, service in (("DbDataKit", "server"), ("DbDataRunner", "runner"), ("DbDataStore", "store"), ("DbDataWorkspace", "workspace"), ("DbDataBuild", "build")):
            program = folder[6:]
            if folder == "DbDataKit":
                path, settings, home = f"{folder}/src/DbDataKit.Server/ConsoleService.cs", "ConsoleSettings", f"{folder}/src/DbDataKit.Server/ConsoleSettings.cs"
            elif folder == "DbDataBuild":
                path, settings, home = f"{folder}/src/DbDataBuild.Cli/Server/BuildService.cs", "BuildServerSettings", f"{folder}/src/DbDataBuild.Cli/Server/BuildServer.cs"
            else:
                path, settings, home = f"{folder}/src/{folder}.Server/{program}Service.cs", f"{program}Settings", f"{folder}/src/{folder}.Server/{program}Settings.cs"
            text = (REPO_ROOT / path).read_text(encoding="utf-8")
            self.assertIn(f"SuiteEnvironment.ResolveServiceFolder({settings}.ServiceName)", text, path)
            self.assertNotIn("CommonApplicationData", text, path)
            self.assertIn(f'ServiceName = "{service}"', (REPO_ROOT / home).read_text(encoding="utf-8"), home)

    def test_builds_service_block_is_what_the_source_registers(self):
        meta = self.meta("DbDataBuild/dist.json")
        service = (REPO_ROOT / "DbDataBuild/src/DbDataBuild.Cli/Server/BuildService.cs").read_text(encoding="utf-8")
        self.assertIn(f'RegistrationName = "{meta["service"]["name"]}"', service)
        self.assertEqual(meta["service"]["register"], "service install")
        self.assertEqual(meta["service"]["unregister"], "service uninstall")
        self.assertEqual(meta["windowsScope"], "machine")      # a service belongs under Program Files

    def test_the_sync_manifest_names_the_folder_sync_defaults_to(self):
        cli = (REPO_ROOT / "DbDataSync/src/DbDataSync.Cli/CliOptions.cs").read_text(encoding="utf-8")
        self.assertIn('SuiteEnvironment.MachineFolder("sync")', cli)
        self.assertIn('SuiteEnvironment.ResolveServiceFolder("sync")', cli)

    def test_the_gateway_default_is_the_machine_folder_of_the_shared_reader(self):
        host = (REPO_ROOT / "DbDataGateway/src/DbDataGateway.Server/Hosting/GatewayHost.cs").read_text(encoding="utf-8")
        self.assertIn('SuiteEnvironment.ResolveServiceFolder("gateway")', host)
        notes = self.meta("DbDataGateway/dist.json")["notes"]
        self.assertIn("%ProgramData%\\DbDataKit\\gateway\\config", notes)

    def test_no_manifest_names_a_variable_of_an_earlier_release(self):
        old = ("DBDATAKIT_CONSOLE_DIR", "DBDATARUNNER_SERVER_DIR", "DBDATASTORE_SERVER_DIR", "DBDATAWORKSPACE_SERVER_DIR", "DBDATABUILD_SERVER_DIR", "DbDataSync__App__RepoRoot")
        for path in REPO_ROOT.glob("*/dist.json"):
            text = path.read_text(encoding="utf-8")
            for name in old:
                self.assertNotIn(name, text, path.name)


class BindTests(DistTestCase):
    def test_scoop_manifest(self):
        out = self.fresh()
        name = "widget-v2026.2.2.0200-win-x64.zip"
        bind(out, version="2026.2.2.0200", sums=f"{'ab' * 32}  {name}\n")
        m = json.loads((out / "widget.json").read_text())
        self.assertEqual(m["version"], "2026.2.2.0200")
        self.assertEqual(m["architecture"]["64bit"]["url"], f"https://github.com/{REPO}/releases/download/widget/v2026.2.2.0200/{name}")
        self.assertEqual(m["architecture"]["64bit"]["hash"], "ab" * 32)
        self.assertEqual(m["bin"], "widget.exe")
        self.assertEqual(m["persist"], ["settings.json"])
        self.assertEqual(m["suggest"], {"Visual C++ Redistributable": ["extras/vcredist2022"]})
        self.assertNotIn("post_install", m)      # no globalEnv in this product
        self.assertNotIn("pre_uninstall", m)

    def test_a_beta_and_a_snapshot_have_a_manifest_that_pins_their_own_zip(self):
        for suffix in ("beta", "snapshot.gabc1234"):
            out = self.fresh()
            version = f"2026.2.2.0200-{suffix}"
            name = f"widget-v{version}-win-x64.zip"
            bind(out, version=version, sums=f"{'cd' * 32}  {name}\n")
            m = json.loads((out / "widget.json").read_text())
            self.assertEqual(m["version"], version)
            self.assertEqual(m["architecture"]["64bit"]["url"], f"https://github.com/{REPO}/releases/download/widget/v{version}/{name}")
            self.assertEqual(m["architecture"]["64bit"]["hash"], "cd" * 32)

    def test_a_global_install_sets_the_machine_variables_and_an_uninstall_removes_only_those(self):
        out = self.fresh()
        meta = dict(META, globalEnv=[{"name": "WIDGET_DIR", "programData": "Acme\\widget"}, {"name": "WIDGET__ROOT", "programData": "Widget"}])
        name = "widget-v2026.2.2.0200-win-x64.zip"
        bind(out, meta=meta, version="2026.2.2.0200", sums=f"{'ab' * 32}  {name}\n")
        m = json.loads((out / "widget.json").read_text())
        post, pre = m["post_install"], m["pre_uninstall"]
        self.assertEqual(post[0], "if ($global) {")        # a per-user install changes nothing
        self.assertEqual(post[-1], "}")
        self.assertIn("SetEnvironmentVariable('WIDGET_DIR', (Join-Path $env:ProgramData 'Acme\\widget'), 'Machine')", post[1])
        self.assertIn("SetEnvironmentVariable('WIDGET__ROOT', (Join-Path $env:ProgramData 'Widget'), 'Machine')", post[2])
        self.assertEqual(pre[0], "if ($global) {")
        self.assertIn("GetEnvironmentVariable('WIDGET_DIR', 'Machine') -eq (Join-Path $env:ProgramData 'Acme\\widget')", pre[1])
        self.assertIn("SetEnvironmentVariable('WIDGET_DIR', $null, 'Machine')", pre[1])
        self.assertIn("scoop install -g", m["notes"])
        self.assertIn("WIDGET_DIR=%ProgramData%\\Acme\\widget", m["notes"])
        self.assertIn("Read the manual", m["notes"])         # the product's own notes stay

    def test_an_uninstall_never_removes_the_shared_data_root(self):
        out = self.fresh()
        meta = dict(META, globalEnv=[{"name": "DBDATAKIT_DATA", "programData": "DbDataKit"}, {"name": "WIDGET_DIR", "programData": "Acme\\widget"}])
        name = "widget-v2026.2.2.0200-win-x64.zip"
        bind(out, meta=meta, version="2026.2.2.0200", sums=f"{'ab' * 32}  {name}\n")
        m = json.loads((out / "widget.json").read_text())
        self.assertIn("SetEnvironmentVariable('DBDATAKIT_DATA', (Join-Path $env:ProgramData 'DbDataKit'), 'Machine')", "\n".join(m["post_install"]))
        undo = "\n".join(m["pre_uninstall"])
        self.assertIn("WIDGET_DIR", undo)
        self.assertNotIn("DBDATAKIT_DATA", undo)          # another product still installed reads it
        self.assertIn("DBDATAKIT_DATA is left in place", m["notes"])

    def test_a_product_that_sets_only_the_shared_root_has_no_uninstall_script(self):
        out = self.fresh()
        meta = dict(META, globalEnv=[{"name": "DBDATAKIT_DATA", "programData": "DbDataKit"}])
        name = "widget-v2026.2.2.0200-win-x64.zip"
        bind(out, meta=meta, version="2026.2.2.0200", sums=f"{'ab' * 32}  {name}\n")
        m = json.loads((out / "widget.json").read_text())
        self.assertIn("post_install", m)
        self.assertNotIn("pre_uninstall", m)
        self.assertIn("DBDATAKIT_DATA=%ProgramData%\\DbDataKit", m["notes"])

    def test_a_bad_global_env_entry_is_refused(self):
        for bad in ({"name": "not a name", "programData": "Widget"}, {"name": "OK", "programData": "C:\\Widget"}, {"name": "OK", "programData": "\\Widget"}, {"name": "OK"}):
            out = self.fresh()
            name = "widget-v2026.2.2.0200-win-x64.zip"
            with self.assertRaises(subprocess.CalledProcessError):
                bind(out, meta=dict(META, globalEnv=[bad]), version="2026.2.2.0200", sums=f"{'ab' * 32}  {name}\n")

    def test_scoop_manifest_needs_the_checksum(self):
        out = self.fresh()
        with self.assertRaises(subprocess.CalledProcessError):
            bind(out, version="2026.2.2.0200", sums="x  other.zip\n")

    def test_bound_installers_carry_the_product(self):
        out = self.fresh()
        bind(out)
        sh = (out / "install.sh").read_text()
        self.assertIn("PRODUCT='widget'", sh)
        self.assertIn("PLATFORMS='linux-x64=widget'", sh)
        self.assertIn("it's short", sh.replace("'\\''", "'"))   # notes quoting survives
        self.assertIn("Product = 'widget'", (out / "install.ps1").read_text().replace('"', "'"))
        self.assertIn("RequiresVcRuntime = $true", (out / "install.ps1").read_text())

    def test_the_announce_flag_is_bound(self):
        out = self.fresh()
        bind(out, meta=dict(META, announces=True))
        self.assertIn("ANNOUNCES=1", (out / "install.sh").read_text())
        self.assertIn("Announces = $true", (out / "install.ps1").read_text())
        out2 = self.fresh()
        bind(out2)
        self.assertIn("ANNOUNCES=0", (out2 / "install.sh").read_text())
        self.assertIn("Announces = $false", (out2 / "install.ps1").read_text())

    @unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
    def test_unbound_templates_refuse_to_run(self):
        r = subprocess.run(["bash", str(DIST / "install.sh")], capture_output=True, text=True)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("not bound", r.stderr)


RECORDER = b"#!/bin/sh\necho \"$@\" > \"$EXPORT_LOG\"\nexit \"${EXPORT_EXIT:-0}\"\n"


class ExportComponentOptionTests(unittest.TestCase):
    """The installer option that adds the PDF export component (plan 32 DBR16, owner decision 2026-11-12): the installers call the product's own `export install` for the release they installed;
    nothing downloads or verifies the component in a script of ours. A fake program records what it was asked."""

    @classmethod
    def setUpClass(cls):
        cls.tmp = pathlib.Path(tempfile.mkdtemp(prefix="dist-export-"))
        cls.gh = FakeGitHub()
        cls.gh.add_release("widget", "2026.2.2.0200", exe_payload=RECORDER)

    @classmethod
    def tearDownClass(cls):
        cls.gh.close()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def bound(self, **extra):
        out = pathlib.Path(tempfile.mkdtemp(prefix="bound-", dir=self.tmp))
        bind(out, meta=dict(META, **extra))
        return out

    def install(self, out, *args, exit_code="0"):
        home = pathlib.Path(tempfile.mkdtemp(prefix="home-", dir=self.tmp))
        log = home / "export-args"
        env = {**os.environ, "HOME": str(home), "XDG_DATA_HOME": str(home / "data"), "EXPORT_LOG": str(log), "EXPORT_EXIT": exit_code}
        r = subprocess.run(["bash", str(out / "install.sh"), "--api-base", self.gh.base, *args], capture_output=True, text=True, env=env)
        return home, log, r

    def test_the_flag_is_bound_into_both_installers(self):
        on, off = self.bound(exportComponent=True), self.bound()
        self.assertIn("EXPORT_COMPONENT=1", (on / "install.sh").read_text())
        self.assertIn("ExportComponent = $true", (on / "install.ps1").read_text())
        self.assertIn("EXPORT_COMPONENT=0", (off / "install.sh").read_text())
        self.assertIn("ExportComponent = $false", (off / "install.ps1").read_text())

    def test_the_powershell_installer_has_the_option_and_calls_the_product(self):
        text = (DIST / "install.ps1").read_text()
        self.assertIn("[switch]$WithPdfExport", text)
        self.assertIn("[string]$ExportSource", text)
        self.assertIn("@('export', 'install', '--version', $releaseVersion)", text)
        self.assertIn("'--source', $ExportSource", text)

    @unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
    def test_with_pdf_export_runs_the_products_own_install_for_this_release_and_without_it_nothing_runs(self):
        out = self.bound(exportComponent=True)
        home, log, r = self.install(out, "--with-pdf-export")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(log.read_text().strip(), "export install --version 2026.2.2.0200")
        self.assertIn("Adding PDF export", r.stdout)
        home, log, r = self.install(out)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertFalse(log.exists())

    @unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
    def test_an_export_source_is_passed_on_and_is_refused_without_the_option(self):
        out = self.bound(exportComponent=True)
        home, log, r = self.install(out, "--with-pdf-export", "--export-source", "/srv/releases/files")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(log.read_text().strip(), "export install --version 2026.2.2.0200 --source /srv/releases/files")
        home, log, r = self.install(out, "--export-source", "/srv/releases/files")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("--export-source goes with --with-pdf-export", r.stderr)
        self.assertFalse((home / "data" / "widget").exists())      # refused before anything was installed

    @unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
    def test_a_failed_export_install_fails_the_script_in_words_after_the_product_is_installed(self):
        home, log, r = self.install(self.bound(exportComponent=True), "--with-pdf-export", exit_code="3")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("is installed, but its PDF export was not", r.stderr)
        self.assertIn("export install --version 2026.2.2.0200", r.stderr)
        self.assertTrue((home / "data" / "widget" / "widget").exists())

    @unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
    def test_a_product_without_the_component_ignores_the_option_and_says_so(self):
        home, log, r = self.install(self.bound(), "--with-pdf-export")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("has no PDF export component", r.stderr)
        self.assertFalse(log.exists())

    @unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
    def test_a_dry_run_says_what_would_follow(self):
        home, log, r = self.install(self.bound(exportComponent=True), "--dry-run", "--with-pdf-export", "--export-source", "/srv/f")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("widget export install --version 2026.2.2.0200 --source /srv/f", r.stdout)
        self.assertFalse(log.exists())


@unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
class BashInstallerTests(DistTestCase):
    def run_sh(self, home, *args):
        env = {**os.environ, "HOME": str(home), "XDG_DATA_HOME": str(home / "data")}
        return subprocess.run(["bash", str(self.tmp / "install.sh"), "--api-base", self.gh.base, *args],
                              capture_output=True, text=True, env=env)

    def test_the_announce_address_is_named_for_a_product_that_announces_and_ignored_for_one_that_does_not(self):
        announcing = self.fresh()
        bind(announcing, meta=dict(META, announces=True))
        env = {**os.environ, "HOME": str(announcing), "XDG_DATA_HOME": str(announcing / "data")}
        r = subprocess.run(["bash", str(announcing / "install.sh"), "--api-base", self.gh.base, "--announce-url", "http://console:8481"], capture_output=True, text=True, env=env)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("DBDATAKIT_ANNOUNCE_URL=http://console:8481", r.stdout)
        r = self.run_sh(self.fresh(), "--announce-url", "http://console:8481")      # the widget of the shared bind does not announce
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("does not announce", r.stderr)
        self.assertNotIn("DBDATAKIT_ANNOUNCE_URL", r.stdout)

    def announcing_install(self, *extra):
        home = self.fresh()
        out = self.fresh()
        bind(out, meta=dict(META, announces=True))
        env = {**os.environ, "HOME": str(home), "XDG_DATA_HOME": str(home / "data")}
        r = subprocess.run(["bash", str(out / "install.sh"), "--api-base", self.gh.base, *extra], capture_output=True, text=True, env=env)
        return home, r

    def test_the_announce_address_becomes_a_systemd_drop_in_for_the_products_unit(self):
        # a fake systemd directory stands for /etc/systemd/system (the real one needs root and --system); nothing asks systemd to reload
        systemd = self.fresh() / "etc-systemd-system"
        home, r = self.announcing_install("--announce-url", "http://console:8481", "--systemd-dir", str(systemd))
        self.assertEqual(r.returncode, 0, r.stderr)
        dropin = systemd / "widget.service.d" / "announce.conf"
        lines = dropin.read_text().splitlines()
        self.assertEqual(lines[1:], ["[Service]", 'Environment="DBDATAKIT_ANNOUNCE_URL=http://console:8481"'])
        self.assertTrue(lines[0].startswith("# Written by install.sh --announce-url"))
        self.assertEqual(dropin.stat().st_mode & 0o777, 0o644)
        self.assertIn(str(dropin), r.stdout)
        self.assertNotIn("systemctl", r.stderr)
        # installing again with another address replaces the file (one line, not two)
        home, r = self.announcing_install("--announce-url", "https://console.example:8481", "--systemd-dir", str(systemd))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual(dropin.read_text().count("DBDATAKIT_ANNOUNCE_URL"), 1)
        self.assertIn("https://console.example:8481", dropin.read_text())

    def test_the_bundle_token_file_goes_into_the_drop_in_and_is_refused_without_an_announce_address_or_with_syntax(self):
        # plan 23a FQ18: the token file's path (never the token) is what a server of the single-machine bundle is given
        systemd = self.fresh() / "etc-systemd-system"
        home, r = self.announcing_install("--announce-url", "http://127.0.0.1:8481", "--bundle-token-file", "/var/lib/dbdatakit/server/bundle.token", "--systemd-dir", str(systemd))
        self.assertEqual(r.returncode, 0, r.stderr)
        lines = (systemd / "widget.service.d" / "announce.conf").read_text().splitlines()
        self.assertEqual(lines[1:], ["[Service]", 'Environment="DBDATAKIT_ANNOUNCE_URL=http://127.0.0.1:8481"', 'Environment="DBDATAKIT_BUNDLE_TOKEN_FILE=/var/lib/dbdatakit/server/bundle.token"'])
        self.assertIn("pairs with the service by itself", r.stdout)
        for args, words in ((("--bundle-token-file", "/x/bundle.token"), "goes with --announce-url"),
                            (("--announce-url", "http://c:1", "--bundle-token-file", "relative.token"), "absolute path"),
                            (("--announce-url", "http://c:1", "--bundle-token-file", "/x/a b"), "must not contain")):
            home, r = self.announcing_install(*args)
            self.assertNotEqual(r.returncode, 0)
            self.assertIn(words, r.stderr)

    def test_without_a_system_install_or_a_directory_nothing_is_written_and_the_line_is_printed(self):
        systemd = self.fresh() / "etc-systemd-system"
        home, r = self.announcing_install("--announce-url", "http://console:8481")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Environment=DBDATAKIT_ANNOUNCE_URL=http://console:8481", r.stdout)
        self.assertIn("--system", r.stdout)
        self.assertFalse(systemd.exists())

    def test_a_product_that_does_not_announce_gets_no_drop_in(self):
        systemd = self.fresh() / "etc-systemd-system"
        r = self.run_sh(self.fresh(), "--announce-url", "http://console:8481", "--systemd-dir", str(systemd))
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("does not announce", r.stderr)
        self.assertFalse(systemd.exists())

    def test_an_announce_address_that_is_not_plain_http_or_has_unit_syntax_is_refused_before_anything_is_installed(self):
        for bad in ("console:8481", "ftp://console", "http://", "http://console 8481", 'http://con"sole', "http://console/%n", "http://console/$x", "http://console\\x"):
            systemd = self.fresh() / "etc-systemd-system"
            home, r = self.announcing_install("--announce-url", bad, "--systemd-dir", str(systemd))
            self.assertNotEqual(r.returncode, 0, bad)
            self.assertIn("--announce-url", r.stderr, bad)
            self.assertFalse(systemd.exists(), bad)
            self.assertFalse((home / "data" / "widget").exists(), bad)

    def test_latest_is_the_newest_stable_release_of_this_product(self):
        r = self.run_sh(self.fresh(), "--dry-run")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Widget 2026.2.2.0200 (widget/v2026.2.2.0200)", r.stdout)
        self.assertIn("widget-v2026.2.2.0200-linux-x64.zip", r.stdout)

    def test_prerelease_flag_allows_a_prerelease_but_never_a_draft(self):
        r = self.run_sh(self.fresh(), "--dry-run", "--prerelease")
        self.assertIn("2026.5.5.0500", r.stdout)   # newest non-draft
        self.assertIn("[prerelease]", r.stdout)

    def test_specific_version(self):
        r = self.run_sh(self.fresh(), "--dry-run", "--version", "2026.1.1.0100")
        self.assertIn("Widget 2026.1.1.0100", r.stdout)
        r = self.run_sh(self.fresh(), "--dry-run", "--version", "widget/v2026.1.1.0100")
        self.assertIn("Widget 2026.1.1.0100", r.stdout)

    def test_unknown_version_fails(self):
        r = self.run_sh(self.fresh(), "--dry-run", "--version", "1.0")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("No Widget release 1.0", r.stderr)

    def test_install_then_upgrade_keeps_configuration(self):
        home = self.fresh()
        r = self.run_sh(home)
        self.assertEqual(r.returncode, 0, r.stderr)
        target = home / "data" / "widget"
        self.assertEqual((target / "widget").read_bytes(), b"stable-new")
        self.assertTrue(os.access(target / "widget", os.X_OK))
        self.assertTrue((target / "lib" / "a.txt").exists())
        self.assertEqual(os.readlink(home / ".local" / "bin" / "widget"), str(target / "widget"))
        self.assertIn("Read the manual", r.stdout)

        (target / "settings.json").write_text('{"mine": true}')
        r = self.run_sh(home, "--version", "2026.1.1.0100")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual((target / "widget").read_bytes(), b"v")
        self.assertEqual((target / "settings.json").read_text(), '{"mine": true}')
        self.assertIn("Kept your existing settings.json", r.stdout)

    def test_checksum_mismatch_installs_nothing(self):
        home = self.fresh()
        r = self.run_sh(home, "--version", "2026.5.5.0500")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("Checksum mismatch", r.stderr)
        self.assertFalse((home / "data" / "widget").exists())


class GatewayContainerManagementTests(unittest.TestCase):
    """The Gateway's management API is the administration API: its container image binds it to loopback, and no compose file publishes it beyond the host's loopback (plan 33 D15)."""

    def test_the_image_binds_management_to_loopback_and_exposes_only_flight(self):
        text = (REPO_ROOT / "DbDataGateway/Dockerfile").read_text(encoding="utf-8")
        lines = [line for line in text.splitlines() if not line.lstrip().startswith("#")]
        code = "\n".join(lines)
        self.assertIn("DBDATAKIT_Gateway__Management__Address=127.0.0.1", code)
        self.assertNotIn("Management__Enabled=false", code)         # on by default is the owner's decision (D15)
        self.assertEqual([line for line in lines if line.startswith("EXPOSE")], ["EXPOSE 8815"])

    def test_no_compose_file_publishes_the_management_port_beyond_loopback(self):
        import re
        files = sorted((REPO_ROOT / "dist/compose").glob("*.yml"))
        self.assertTrue(files)
        for path in files:
            for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), 1):
                entry = re.match(r'\s*-\s*"?([^"#\s]+)"?', line)
                if line.lstrip().startswith("-") and entry and re.search(r"(^|:)8816(:|$)", entry.group(1)):
                    self.assertTrue(entry.group(1).startswith("127.0.0.1:"), f"{path.name}:{number} publishes 8816 on every address: {line.strip()}")

    def test_a_compose_file_that_widens_management_does_not_publish_it(self):
        for path in sorted((REPO_ROOT / "dist/compose").glob("*.yml")):
            text = path.read_text(encoding="utf-8")
            if "Management__Address: 0.0.0.0" in text:
                self.assertNotIn('- "8816:8816"', text, path.name)


@unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
class ShippedGatewayTests(unittest.TestCase):
    """The Gateway ships a win-x64 zip (self-contained, MSI and Scoop) and a linux-x64 zip (framework-dependent, install.sh; plan 33 SC01, SC02): its real
    dist.json is bound and installed from a fake release that has both zips, the way a two-platform dist.json is."""

    PRODUCT = "dbdatagateway"
    VERSION = "2026.11.11.1200"

    @classmethod
    def setUpClass(cls):
        cls.meta = json.loads((REPO_ROOT / "DbDataGateway" / "dist.json").read_text(encoding="utf-8"))
        cls.tmp = pathlib.Path(tempfile.mkdtemp(prefix="dist-gateway-"))
        cls.gh = FakeGitHub()
        cls.gh.add_release(cls.PRODUCT, cls.VERSION, exe_payload=b"#!/bin/sh\n", platforms=cls.meta["platforms"])
        cls.bound = cls.tmp / "bound"
        cls.bound.mkdir()
        sums = "".join(f"{'ab' * 32}  {cls.PRODUCT}-v{cls.VERSION}-{rid}.zip\n" for rid in cls.meta["platforms"])
        bind(cls.bound, meta=cls.meta, version=cls.VERSION, sums=sums)

    @classmethod
    def tearDownClass(cls):
        cls.gh.close()
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def test_it_lists_both_platforms_and_the_linux_zip_needs_the_runtime(self):
        self.assertEqual(sorted(self.meta["platforms"]), ["linux-x64", "win-x64"])
        self.assertEqual(self.meta["platforms"]["linux-x64"]["exe"], "dbdatagateway")
        self.assertNotIn("requiresDotnet", self.meta)   # the Windows zip is self-contained: no .NET check in its MSI or install.ps1
        self.assertEqual(self.meta["platforms"]["linux-x64"]["requiresDotnet"], "10.0")

    def test_scoop_and_the_powershell_installer_stay_windows(self):
        manifest = json.loads((self.bound / f"{self.PRODUCT}.json").read_text())
        url = manifest["architecture"]["64bit"]["url"]
        self.assertTrue(url.endswith(f"{self.PRODUCT}-v{self.VERSION}-win-x64.zip"), url)
        self.assertEqual(manifest["bin"], "dbdatagateway.exe")
        self.assertNotIn("linux", json.dumps(manifest))
        ps1 = (self.bound / "install.ps1").read_text().replace('"', "'")
        self.assertIn("Exe = 'dbdatagateway.exe'", ps1)
        self.assertIn("RequiresDotnet = ''", ps1)

    def test_the_shell_installer_takes_only_the_linux_zip(self):
        sh = (self.bound / "install.sh").read_text()
        self.assertIn("PLATFORMS='linux-x64=dbdatagateway'", sh)
        self.assertIn("REQUIRES_DOTNET='10.0'", sh)

    def test_install_sh_installs_the_linux_zip_and_not_the_windows_one(self):
        home = pathlib.Path(tempfile.mkdtemp(prefix="home-", dir=self.tmp))
        out = pathlib.Path(tempfile.mkdtemp(prefix="bound-", dir=self.tmp))
        bind(out, meta=self.meta)
        env = {**os.environ, "HOME": str(home), "XDG_DATA_HOME": str(home / "data")}
        r = subprocess.run(["bash", str(out / "install.sh"), "--api-base", self.gh.base, "--dry-run"], capture_output=True, text=True, env=env)
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn(f"{self.PRODUCT}-v{self.VERSION}-linux-x64.zip", r.stdout)
        self.assertNotIn("win-x64", r.stdout)
        r = subprocess.run(["bash", str(out / "install.sh"), "--api-base", self.gh.base], capture_output=True, text=True, env=env)
        self.assertEqual(r.returncode, 0, r.stderr)
        target = home / "data" / self.PRODUCT
        self.assertTrue(os.access(target / "dbdatagateway", os.X_OK))
        self.assertEqual(os.readlink(home / ".local" / "bin" / "dbdatagateway"), str(target / "dbdatagateway"))
        self.assertFalse((target / "dbdatagateway.exe").exists())
        self.assertIn("/var/lib/dbdatakit/gateway", r.stdout)             # the notes: where the data lives on Linux, and the unit
        self.assertIn("service unit", r.stdout)


@unittest.skipUnless(PWSH, "pwsh not available")
class PowerShellInstallerTests(DistTestCase):
    def run_ps(self, *args):
        # A PSModulePath left by a parent PowerShell 7 stops Windows PowerShell 5.1 from finding its own modules.
        env = {k: v for k, v in os.environ.items() if k.upper() != "PSMODULEPATH"}   # Windows upper-cases the names
        return subprocess.run([PWSH, "-NoProfile", "-File", str(self.tmp / "install.ps1"), "-ApiBase", self.gh.base, *args],
                              capture_output=True, text=True, env=env)

    def test_latest_is_the_newest_stable_release_of_this_product(self):
        r = self.run_ps("-DryRun")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertIn("Widget 2026.2.2.0200 (widget/v2026.2.2.0200)", r.stdout)
        self.assertIn("widget-v2026.2.2.0200-win-x64.zip", r.stdout)

    def test_prerelease_and_version_selection(self):
        r = self.run_ps("-DryRun", "-IncludePrerelease")
        self.assertIn("2026.5.5.0500", r.stdout)
        r = self.run_ps("-DryRun", "-Version", "2026.1.1.0100")
        self.assertIn("Widget 2026.1.1.0100", r.stdout)

    def test_install_then_upgrade_keeps_configuration(self):
        target = self.fresh() / "inst"
        r = self.run_ps("-InstallDir", str(target), "-NoPath")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual((target / "widget.exe").read_bytes(), b"stable-new")
        self.assertTrue((target / "lib" / "a.txt").exists())
        (target / "settings.json").write_text('{"mine": true}')
        r = self.run_ps("-InstallDir", str(target), "-NoPath", "-Version", "2026.1.1.0100")
        self.assertEqual(r.returncode, 0, r.stderr)
        self.assertEqual((target / "widget.exe").read_bytes(), b"v")
        self.assertEqual((target / "settings.json").read_text(), '{"mine": true}')

    def test_checksum_mismatch_installs_nothing(self):
        target = self.fresh() / "inst"
        r = self.run_ps("-InstallDir", str(target), "-NoPath", "-Version", "2026.5.5.0500")
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("Checksum mismatch", r.stdout + r.stderr)
        self.assertFalse(target.exists())


@unittest.skipUnless(PWSH, "pwsh not available")
class ServiceTests(DistTestCase):
    """install.ps1's Windows service handling, with Get/Stop/Start-Service replaced by functions that write a log, and IsWindows forced on."""

    def run_install(self, status, *, install_dir=None, preseed=None):
        work = self.fresh()
        target = install_dir or work / "inst"
        if preseed is not None:
            target.mkdir(parents=True)
            (target / "widget.exe").write_bytes(preseed)
        log = work / "service.log"
        wrapper = work / "wrapper.ps1"
        wrapper.write_text(f"""$ErrorActionPreference = 'Stop'
Set-Variable -Name IsWindows -Value $true -Force -Scope Global
if (-not $env:SystemRoot) {{ $env:SystemRoot = '{work}' }}
$log = '{log}'
$exe = '{target / "widget.exe"}'
function Content {{ if (Test-Path $exe) {{ (Get-Content $exe -Raw).Trim() }} else {{ 'none' }} }}
function Get-Service {{
  [CmdletBinding()] param([string]$Name)
  if ('{status}' -eq 'none') {{ return }}
  $o = [pscustomobject]@{{ Name = $Name; Status = '{status}' }}
  $o | Add-Member -MemberType ScriptMethod -Name WaitForStatus -Value {{ param($s, $t) }}
  $o
}}
function Stop-Service {{ [CmdletBinding()] param([Parameter(Position = 0)][string]$Name) "stop $Name $(Content)" | Add-Content $log }}
function Start-Service {{ [CmdletBinding()] param([Parameter(Position = 0)][string]$Name) "start $Name $(Content)" | Add-Content $log }}
& '{self.tmp / "install.ps1"}' -ApiBase '{self.gh.base}' -InstallDir '{target}' -NoPath -Version 2026.2.2.0200
""", encoding="utf-8")
        env = {k: v for k, v in os.environ.items() if k.upper() != "PSMODULEPATH"}
        r = subprocess.run([PWSH, "-NoProfile", "-File", str(wrapper)], capture_output=True, text=True, env=env)
        lines = log.read_text().splitlines() if log.exists() else []
        return r, lines, target

    def test_a_running_service_is_stopped_before_the_files_change_and_started_after(self):
        r, lines, target = self.run_install("Running", preseed=b"old")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(lines, ["stop WidgetSvc old", "start WidgetSvc stable-new"])
        self.assertEqual((target / "widget.exe").read_bytes(), b"stable-new")

    def test_a_stopped_service_is_left_stopped(self):
        r, lines, _ = self.run_install("Stopped", preseed=b"old")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(lines, [])

    def test_no_service_means_nothing_to_stop_or_start(self):
        r, lines, target = self.run_install("none")
        self.assertEqual(r.returncode, 0, r.stdout + r.stderr)
        self.assertEqual(lines, [])
        self.assertEqual((target / "widget.exe").read_bytes(), b"stable-new")

    def test_a_failed_copy_starts_the_service_again(self):
        blocker = self.fresh() / "blocker"
        blocker.write_text("a file where a directory is needed")
        r, lines, _ = self.run_install("Running", install_dir=blocker / "inst")
        self.assertNotEqual(r.returncode, 0)
        self.assertEqual([l.split()[0] for l in lines], ["stop", "start"])
        self.assertIn("starting the service again", r.stdout + r.stderr)


if __name__ == "__main__":
    unittest.main()
