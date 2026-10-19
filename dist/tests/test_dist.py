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

    def add_release(self, product, version, *, prerelease=False, draft=False, exe_payload=b"v", corrupt=False, crlf=False, rids=("win-x64", "linux-x64")):
        tag = f"{product}/v{version}"
        assets, sums = [], []
        for rid in rids:
            exe = META["platforms"][rid]["exe"]
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

    @unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
    def test_unbound_templates_refuse_to_run(self):
        r = subprocess.run(["bash", str(DIST / "install.sh")], capture_output=True, text=True)
        self.assertNotEqual(r.returncode, 0)
        self.assertIn("not bound", r.stderr)


@unittest.skipIf(os.name == "nt", "install.sh is for Linux and macOS")
class BashInstallerTests(DistTestCase):
    def run_sh(self, home, *args):
        env = {**os.environ, "HOME": str(home), "XDG_DATA_HOME": str(home / "data")}
        return subprocess.run(["bash", str(self.tmp / "install.sh"), "--api-base", self.gh.base, *args],
                              capture_output=True, text=True, env=env)

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
