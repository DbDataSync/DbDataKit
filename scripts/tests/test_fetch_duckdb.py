import hashlib
import os
import re
import shutil
import subprocess
import tarfile
import tempfile
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SCRIPT = os.path.join(ROOT, "scripts", "fetch-duckdb.sh")
RIDS = ["linux-x64", "linux-arm64", "win-x64", "osx-arm64", "osx-x64"]
LIBS = {"linux-x64": "libduckdb.so", "linux-arm64": "libduckdb.so", "win-x64": "duckdb.dll", "osx-arm64": "libduckdb.dylib", "osx-x64": "libduckdb.dylib"}


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read(path, mode="r"):
    with open(path, mode, **({} if "b" in mode else {"encoding": "utf-8"})) as f:
        return f.read()


def write(path, data):
    with open(path, "wb" if isinstance(data, bytes) else "w", **({} if isinstance(data, bytes) else {"encoding": "utf-8"})) as f:
        f.write(data)


def parse(path):
    pin = {}
    for line in read(path).splitlines():
        m = re.match(r"^([A-Za-z0-9_]+)=(.*)$", line.strip())
        if m:
            pin[m.group(1)] = m.group(2)
    return pin


class ThePin(unittest.TestCase):
    def test_every_platform_has_an_archive_a_library_and_both_hashes(self):
        pin = parse(os.path.join(ROOT, "duckdb.pin"))
        self.assertRegex(pin["VERSION"], r"^\d+\.\d+\.\d+$")
        self.assertIn(pin["VERSION"], pin["BASE"])
        for rid in RIDS:
            key = rid.replace("-", "_")
            self.assertTrue(pin["ARCHIVE_" + key].endswith(".tar.gz"), rid)
            self.assertEqual(LIBS[rid], pin["LIB_" + key], rid)
            self.assertRegex(pin["ARCHIVE_SHA256_" + key], r"^[0-9a-f]{64}$", rid)
            self.assertRegex(pin["LIB_SHA256_" + key], r"^[0-9a-f]{64}$", rid)


@unittest.skipUnless(shutil.which("bash") and shutil.which("curl") and shutil.which("tar"), "bash, curl and tar are needed")
class FetchDuckDb(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.mkdtemp()
        self.release = os.path.join(self.work, "release")
        os.makedirs(self.release)
        self.libs = {}
        lines = ["VERSION=9.9.9", f"BASE=file://{self.release}"]
        for rid in RIDS:
            lib = LIBS[rid]
            content = f"library for {rid}\n".encode() * 100
            staging = os.path.join(self.work, "stage-" + rid)
            os.makedirs(staging)
            with open(os.path.join(staging, lib), "wb") as f:
                f.write(content)
            with open(os.path.join(staging, "duckdb.h"), "wb") as f:
                f.write(b"header\n")
            archive = f"duckdb-test-{rid}.tar.gz"
            with tarfile.open(os.path.join(self.release, archive), "w:gz") as t:
                t.add(os.path.join(staging, lib), arcname=lib)
                t.add(os.path.join(staging, "duckdb.h"), arcname="duckdb.h")
            key = rid.replace("-", "_")
            lines += [f"ARCHIVE_{key}={archive}", f"ARCHIVE_SHA256_{key}={sha(read(os.path.join(self.release, archive), 'rb'))}", f"LIB_{key}={lib}", f"LIB_SHA256_{key}={sha(content)}"]
            self.libs[rid] = content
        self.pin = os.path.join(self.work, "test.pin")
        write(self.pin, "\n".join(lines) + "\n")
        self.dest = os.path.join(self.work, "dest")

    def tearDown(self):
        shutil.rmtree(self.work, ignore_errors=True)

    def run_script(self, *args):
        env = dict(os.environ, DUCKDB_PIN=self.pin, DUCKDB_DEST=self.dest)
        env.pop("DUCKDB_RELEASE_BASE", None)
        return subprocess.run(["bash", SCRIPT, *args], env=env, capture_output=True, text=True)

    def test_every_platform_is_fetched_and_verified(self):
        result = self.run_script("all")
        self.assertEqual(0, result.returncode, result.stderr)
        for rid in RIDS:
            self.assertEqual(self.libs[rid], read(os.path.join(self.dest, rid, LIBS[rid]), "rb"), rid)

    def test_a_second_run_finds_it_current(self):
        self.assertEqual(0, self.run_script("linux-x64").returncode)
        result = self.run_script("linux-x64")
        self.assertEqual(0, result.returncode)
        self.assertIn("is current", result.stdout)

    def test_a_changed_byte_in_the_archive_fails(self):
        path = os.path.join(self.release, "duckdb-test-linux-x64.tar.gz")
        data = bytearray(read(path, "rb"))
        data[len(data) // 2] ^= 0xFF
        write(path, bytes(data))
        result = self.run_script("linux-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("checksum mismatch", result.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.dest, "linux-x64", "libduckdb.so")))

    def test_a_library_that_is_not_the_pinned_one_fails_even_in_a_valid_archive(self):
        key = "linux_x64"
        text = re.sub(rf"^LIB_SHA256_{key}=.*$", f"LIB_SHA256_{key}={'0' * 64}", read(self.pin), flags=re.M)
        write(self.pin, text)
        result = self.run_script("linux-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("inside", result.stderr)

    def test_a_tampered_installed_library_is_fetched_again(self):
        self.assertEqual(0, self.run_script("linux-x64").returncode)
        installed = os.path.join(self.dest, "linux-x64", "libduckdb.so")
        write(installed, b"changed")
        self.assertEqual(0, self.run_script("linux-x64").returncode)
        self.assertEqual(self.libs["linux-x64"], read(installed, "rb"))

    def test_an_unknown_platform_is_refused(self):
        result = self.run_script("plan9-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("unknown rid", result.stderr)


if __name__ == "__main__":
    unittest.main()
