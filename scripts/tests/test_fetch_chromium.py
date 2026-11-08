import hashlib
import os
import re
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SCRIPT = os.path.join(ROOT, "scripts", "fetch-chromium.sh")
RIDS = ["linux-x64", "win-x64"]
LAYOUT = {"linux-x64": ("linux64/chrome-linux64.zip", "chrome-linux64/chrome"), "win-x64": ("win64/chrome-win64.zip", "chrome-win64/chrome.exe")}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def parse(path):
    pin = {}
    with open(path, encoding="utf-8") as f:
        for line in f:
            m = re.match(r"^([A-Za-z0-9_]+)=(.*)$", line.strip())
            if m:
                pin[m.group(1)] = m.group(2)
    return pin


class ThePin(unittest.TestCase):
    def test_every_platform_has_an_archive_an_executable_and_both_hashes(self):
        pin = parse(os.path.join(ROOT, "chromium.pin"))
        self.assertRegex(pin["VERSION"], r"^\d+\.\d+\.\d+\.\d+$")
        self.assertTrue(pin["BASE"].endswith("/" + pin["VERSION"]), "BASE names the pinned version")
        for rid in RIDS:
            key = rid.replace("-", "_")
            self.assertEqual(LAYOUT[rid][0], pin["ARCHIVE_" + key], rid)
            self.assertEqual(LAYOUT[rid][1], pin["BIN_" + key], rid)
            self.assertRegex(pin["ARCHIVE_SHA256_" + key], r"^[0-9a-f]{64}$", rid)
            self.assertRegex(pin["BIN_SHA256_" + key], r"^[0-9a-f]{64}$", rid)
            self.assertNotEqual(pin["ARCHIVE_SHA256_" + key], pin["BIN_SHA256_" + key], rid)

    def test_the_gitignore_names_the_install_folder(self):
        with open(os.path.join(ROOT, ".gitignore"), encoding="utf-8") as f:
            self.assertIn(".chromium/", f.read().split())


@unittest.skipUnless(shutil.which("bash") and shutil.which("curl") and shutil.which("unzip"), "bash, curl and unzip are needed")
class FetchChromium(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.work, True)
        self.release = os.path.join(self.work, "release")
        self.bins = {}
        lines = ["VERSION=9.9.9.9", f"BASE=file://{self.release}"]
        for rid in RIDS:
            key = rid.replace("-", "_")
            archive, bin_path = LAYOUT[rid]
            data = f"fake chromium for {rid}".encode()
            self.bins[rid] = data
            path = os.path.join(self.release, archive)
            os.makedirs(os.path.dirname(path), exist_ok=True)
            with zipfile.ZipFile(path, "w") as z:
                z.writestr(bin_path, data)
                z.writestr(os.path.dirname(bin_path) + "/icudtl.dat", b"sibling file")
            with open(path, "rb") as f:
                archive_sha = sha(f.read())
            lines += [f"ARCHIVE_{key}={archive}", f"ARCHIVE_SHA256_{key}={archive_sha}", f"BIN_{key}={bin_path}", f"BIN_SHA256_{key}={sha(data)}"]
        self.pin = os.path.join(self.work, "chromium.pin")
        with open(self.pin, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        self.dest = os.path.join(self.work, "dest")

    def run_script(self, *args):
        env = dict(os.environ, CHROMIUM_PIN=self.pin, CHROMIUM_DEST=self.dest)
        return subprocess.run(["bash", SCRIPT, *args], env=env, capture_output=True, text=True)

    def installed(self, rid):
        return os.path.join(self.dest, rid, *LAYOUT[rid][1].split("/"))

    def test_the_browser_is_installed_whole_once_and_then_found_current(self):
        first = self.run_script("linux-x64")
        self.assertEqual(0, first.returncode, first.stderr)
        with open(self.installed("linux-x64"), "rb") as f:
            self.assertEqual(self.bins["linux-x64"], f.read())
        self.assertTrue(os.path.exists(os.path.join(self.dest, "linux-x64", "chrome-linux64", "icudtl.dat")), "the sibling files are installed too")
        if os.name == "posix":
            self.assertTrue(os.access(self.installed("linux-x64"), os.X_OK))
        self.assertIn("is current", self.run_script("linux-x64").stdout)

    def test_all_installs_every_platform(self):
        self.assertEqual(0, self.run_script("all").returncode)
        for rid in RIDS:
            self.assertTrue(os.path.exists(self.installed(rid)), rid)

    def test_a_changed_archive_is_refused_and_nothing_is_installed(self):
        with open(os.path.join(self.release, "linux64", "chrome-linux64.zip"), "ab") as f:
            f.write(b"x")
        result = self.run_script("linux-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("checksum mismatch", result.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.dest, "linux-x64")))

    def test_a_changed_executable_inside_a_good_archive_is_refused(self):
        path = os.path.join(self.release, "linux64", "chrome-linux64.zip")
        with zipfile.ZipFile(path, "w") as z:
            z.writestr("chrome-linux64/chrome", b"tampered")
        with open(path, "rb") as f:
            archive_sha = sha(f.read())
        with open(self.pin, encoding="utf-8") as f:
            text = f.read()
        text = re.sub(r"(ARCHIVE_SHA256_linux_x64=).*", r"\g<1>" + archive_sha, text)
        with open(self.pin, "w", encoding="utf-8") as f:
            f.write(text)
        result = self.run_script("linux-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("inside", result.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.dest, "linux-x64")))

    def test_a_changed_installed_file_is_fetched_again(self):
        self.assertEqual(0, self.run_script("linux-x64").returncode)
        with open(self.installed("linux-x64"), "wb") as f:
            f.write(b"edited")
        again = self.run_script("linux-x64")
        self.assertIn("fetched", again.stdout)
        with open(self.installed("linux-x64"), "rb") as f:
            self.assertEqual(self.bins["linux-x64"], f.read())

    def test_an_unknown_platform_is_refused(self):
        self.assertNotEqual(0, self.run_script("plan9").returncode)


if __name__ == "__main__":
    unittest.main()
