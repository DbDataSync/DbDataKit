"""The export component (plan 32 DBR16): staged from a repository whose fetch scripts have run, verified against the pins, a changed file refused by name."""
import hashlib
import json
import os
import pathlib
import shutil
import sys
import tempfile
import unittest
import zipfile

DIST = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(DIST))
import export_component as ec  # noqa: E402


def sha(data):
    return hashlib.sha256(data).hexdigest()


class Fixture:
    """A repository root with both pins, a 'browser', two font families and a built print page, all tiny."""

    def __init__(self, tmp):
        self.root = pathlib.Path(tmp, "repo")
        self.print_dir = pathlib.Path(tmp, "print-build")
        browser = b"#!/bin/sh\necho browser\n"
        (self.root / ".chromium" / "linux-x64" / "chrome-linux64").mkdir(parents=True)
        (self.root / ".chromium" / "linux-x64" / "chrome-linux64" / "chrome").write_bytes(browser)
        (self.root / "chromium.pin").write_text(
            "# fixture\nVERSION=1.2.3.4\nBASE=https://example.test/1.2.3.4\n"
            "ARCHIVE_linux_x64=linux64/chrome-linux64.zip\nARCHIVE_SHA256_linux_x64=" + "0" * 64 + "\n"
            "BIN_linux_x64=chrome-linux64/chrome\nBIN_SHA256_linux_x64=" + sha(browser) + "\n", encoding="utf-8")
        lines = ["# fixture", "VERSION=9.9.9", "BASE=https://example.test", "FAMILIES=noto-sans noto-serif"]
        for family, data in (("noto-sans", b"sans-regular"), ("noto-serif", b"serif-regular")):
            folder = self.root / ".fonts" / family
            folder.mkdir(parents=True)
            (folder / f"{family}-latin-400-normal.woff2").write_bytes(data)
            (folder / "OFL.txt").write_bytes(b"licence of " + family.encode())
            key = family.replace("-", "_")
            lines += [f"LICENSE_SHA256_{key}={sha(b'licence of ' + family.encode())}", f"FILE={family}|package/files/{family}-latin-400-normal.woff2|{sha(data)}"]
        (self.root / "fonts.pin").write_text("\n".join(lines) + "\n", encoding="utf-8")
        self.print_dir.mkdir()
        (self.print_dir / "print.js").write_text("window.__reportReady = true", encoding="utf-8")
        (self.print_dir / "print.css").write_text("body{margin:0}", encoding="utf-8")


class TheExportComponent(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.tmp, True)
        self.fx = Fixture(self.tmp)
        self.out = os.path.join(self.tmp, "export")

    def stage(self, **kw):
        return ec.stage(str(self.fx.root), "linux-x64", self.out, str(self.fx.print_dir), **kw)

    def test_a_staged_component_is_whole_and_says_what_it_holds(self):
        manifest = self.stage()
        self.assertEqual({"component": "export", "rid": "linux-x64", "chromium": "1.2.3.4", "fonts": "9.9.9"}, {k: manifest[k] for k in ("component", "rid", "chromium", "fonts")})
        self.assertEqual([], ec.verify(self.out, "linux-x64"))
        for path in ("chromium.pin", "fonts.pin", "print/print.js", "print/print.css", ".fonts/noto-sans/OFL.txt", ".chromium/linux-x64/chrome-linux64/chrome", ec.MANIFEST):
            self.assertTrue(os.path.exists(os.path.join(self.out, path)), path)

    def test_a_changed_browser_is_refused_by_name(self):
        self.stage()
        with open(os.path.join(self.out, ".chromium", "linux-x64", "chrome-linux64", "chrome"), "ab") as f:
            f.write(b"x")
        problems = ec.verify(self.out, "linux-x64")
        self.assertEqual(1, len(problems))
        self.assertIn("chrome-linux64", problems[0])
        self.assertIn("chromium.pin", problems[0])

    def test_a_changed_font_a_missing_licence_and_a_changed_print_file_are_each_refused(self):
        self.stage()
        with open(os.path.join(self.out, ".fonts", "noto-sans", "noto-sans-latin-400-normal.woff2"), "ab") as f:
            f.write(b"x")
        os.remove(os.path.join(self.out, ".fonts", "noto-serif", "OFL.txt"))
        with open(os.path.join(self.out, "print", "print.js"), "a") as f:
            f.write(";")
        text = "\n".join(ec.verify(self.out, "linux-x64"))
        self.assertIn("noto-sans-latin-400-normal.woff2 does not match fonts.pin", text)
        self.assertIn("licence of noto-serif is missing", text)
        self.assertIn("print.js does not match export-component.json", text)

    def test_a_missing_browser_names_the_script_that_fetches_it(self):
        self.stage()
        shutil.rmtree(os.path.join(self.out, ".chromium"))
        self.assertIn("scripts/fetch-chromium.sh linux-x64", "\n".join(ec.verify(self.out, "linux-x64")))

    def test_staging_without_a_print_build_or_a_fetched_browser_says_what_to_run(self):
        shutil.rmtree(self.fx.print_dir)
        with self.assertRaises(SystemExit) as raised:
            self.stage()
        self.assertIn("npm run build:print", str(raised.exception))
        shutil.rmtree(self.out, ignore_errors=True)
        self.fx.print_dir.mkdir()
        (self.fx.print_dir / "print.js").write_text("x")
        (self.fx.print_dir / "print.css").write_text("x")
        shutil.rmtree(self.fx.root / ".chromium")
        with self.assertRaises(SystemExit) as raised:
            self.stage()
        self.assertIn("fetch-chromium.sh linux-x64", str(raised.exception))

    def test_the_zip_holds_the_staged_files_and_verifies_when_unpacked(self):
        zip_path = os.path.join(self.tmp, "export.zip")
        self.stage(zip_path=zip_path)
        unpacked = os.path.join(self.tmp, "unpacked")
        with zipfile.ZipFile(zip_path) as z:
            z.extractall(unpacked)
        self.assertEqual([], ec.verify(unpacked, "linux-x64"))

    def test_the_repositorys_own_pins_read(self):
        root = DIST.parent
        chromium = ec.read_pin(str(root / "chromium.pin"))
        fonts = ec.read_pin(str(root / "fonts.pin"))
        for key in ("BIN_linux_x64", "BIN_SHA256_linux_x64", "BIN_win_x64", "VERSION"):
            self.assertIn(key, chromium)
        for family in ec.one(fonts, "FAMILIES", "fonts.pin").split():
            self.assertIn("LICENSE_SHA256_" + family.replace("-", "_"), fonts)
            self.assertTrue(any(e.startswith(family + "|") for e in fonts["FILE"]), family)

    def test_the_cli_exit_codes(self):
        self.stage()
        self.assertEqual(0, ec.main(["verify", self.out, "--rid", "linux-x64"]))
        os.remove(os.path.join(self.out, "print", "print.css"))
        self.assertEqual(1, ec.main(["verify", self.out, "--rid", "linux-x64"]))


class TheImageLayer(unittest.TestCase):
    """The Dockerfile cannot be built where the repository's tests run (a package mirror and about 600 MB are needed): its shape is checked against the settings it sets and the folder `stage` makes."""

    def setUp(self):
        self.text = (DIST / "export" / "Dockerfile").read_text(encoding="utf-8")
        self.options = (DIST.parent / "DbDataGateway" / "src" / "DbDataGateway.Server" / "Configuration" / "GatewayOptions.cs").read_text(encoding="utf-8")

    def test_every_setting_it_sets_is_a_property_of_the_gateways_export_section(self):
        import re
        names = re.findall(r"DBDATAKIT_Gateway__Views__Export__([A-Za-z]+)(?:__([A-Za-z]+))?=", self.text)
        self.assertGreaterEqual(len(names), 6)
        for first, second in names:
            self.assertRegex(self.options, rf"public \S+ {first} \{{ get; set; \}}", first)
            if second:
                self.assertRegex(self.options, rf"public \S+ {second} \{{ get; set; \}}", second)

    def test_the_settings_point_into_the_component_folder_it_copies(self):
        self.assertIn("COPY .export-component /opt/dbdatakit/export", self.text)
        for path in ("chromium.pin", "print", ".fonts", "fonts.pin"):
            self.assertIn("/opt/dbdatakit/export/" + path, self.text)
        self.assertIn("Mode=pinned", self.text)

    def test_a_browser_that_cannot_start_fails_the_build_and_host_fonts_are_only_those_promised(self):
        self.assertIn('"$browser" --version', self.text)
        self.assertIn("not found", self.text)
        self.assertIn('ARG HOST_FONT_PACKAGES=""', self.text)


if __name__ == "__main__":
    unittest.main()
