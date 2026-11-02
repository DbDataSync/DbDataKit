import hashlib
import os
import re
import shutil
import subprocess
import tempfile
import unittest
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SCRIPT = os.path.join(ROOT, "scripts", "fetch-bun.sh")
RIDS = ["linux-x64", "linux-arm64", "win-x64", "osx-arm64", "osx-x64"]
BINS = {"linux-x64": "bun", "linux-arm64": "bun", "win-x64": "bun.exe", "osx-arm64": "bun", "osx-x64": "bun"}


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
    def test_every_platform_has_an_archive_a_program_and_both_hashes(self):
        pin = parse(os.path.join(ROOT, "bun.pin"))
        self.assertRegex(pin["VERSION"], r"^\d+\.\d+\.\d+$")
        self.assertIn(pin["VERSION"], pin["BASE"])
        for rid in RIDS:
            key = rid.replace("-", "_")
            self.assertTrue(pin["ARCHIVE_" + key].endswith(".zip"), rid)
            self.assertEqual(BINS[rid], pin["BIN_" + key], rid)
            self.assertRegex(pin["ARCHIVE_SHA256_" + key], r"^[0-9a-f]{64}$", rid)
            self.assertRegex(pin["BIN_SHA256_" + key], r"^[0-9a-f]{64}$", rid)


@unittest.skipUnless(shutil.which("bash") and shutil.which("curl") and shutil.which("unzip"), "bash, curl and unzip are needed")
class FetchBun(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.work, True)
        self.release = os.path.join(self.work, "release")
        os.makedirs(self.release)
        self.bins = {}
        lines = ["VERSION=9.9.9", f"BASE=file://{self.release}"]
        for rid in RIDS:
            key = rid.replace("-", "_")
            data = f"fake bun for {rid}".encode()
            self.bins[rid] = data
            archive = f"bun-{rid}.zip"
            path = os.path.join(self.release, archive)
            with zipfile.ZipFile(path, "w") as z:
                z.writestr(f"bun-{rid}/{BINS[rid]}", data)
            with open(path, "rb") as f:
                archive_sha = sha(f.read())
            lines += [f"ARCHIVE_{key}={archive}", f"ARCHIVE_SHA256_{key}={archive_sha}", f"BIN_{key}={BINS[rid]}", f"BIN_SHA256_{key}={sha(data)}"]
        self.pin = os.path.join(self.work, "bun.pin")
        with open(self.pin, "w", encoding="utf-8") as f:
            f.write("\n".join(lines) + "\n")
        self.dest = os.path.join(self.work, "dest")

    def run_script(self, *args):
        env = dict(os.environ, BUN_PIN=self.pin, BUN_DEST=self.dest)
        return subprocess.run(["bash", SCRIPT, *args], env=env, capture_output=True, text=True)

    def test_the_program_is_installed_once_and_then_found_current(self):
        first = self.run_script("linux-x64")
        self.assertEqual(0, first.returncode, first.stderr)
        with open(os.path.join(self.dest, "linux-x64", "bun"), "rb") as f:
            self.assertEqual(self.bins["linux-x64"], f.read())
        self.assertIn("is current", self.run_script("linux-x64").stdout)

    def test_all_installs_every_platform(self):
        self.assertEqual(0, self.run_script("all").returncode)
        for rid in RIDS:
            self.assertTrue(os.path.exists(os.path.join(self.dest, rid, BINS[rid])), rid)

    def test_a_changed_archive_is_refused_and_nothing_is_installed(self):
        with open(os.path.join(self.release, "bun-linux-x64.zip"), "ab") as f:
            f.write(b"x")
        result = self.run_script("linux-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("checksum mismatch", result.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.dest, "linux-x64")))

    def test_a_changed_program_inside_a_good_archive_is_refused(self):
        path = os.path.join(self.release, "bun-linux-x64.zip")
        with zipfile.ZipFile(path, "w") as z:
            z.writestr("bun-linux-x64/bun", b"tampered")
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

    def test_an_unknown_platform_is_refused(self):
        self.assertNotEqual(0, self.run_script("plan9").returncode)


if __name__ == "__main__":
    unittest.main()
