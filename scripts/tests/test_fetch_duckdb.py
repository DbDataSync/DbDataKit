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

    def run_script(self, *args, **extra):
        env = dict(os.environ, DUCKDB_PIN=self.pin, DUCKDB_DEST=self.dest, DUCKDB_RETRY_DELAY="0", DUCKDB_RETRIES="1")
        env.pop("DUCKDB_RELEASE_BASE", None)
        env.pop("DUCKDB_RELEASE_BASE_FALLBACK", None)
        env.update(extra)
        return subprocess.run(["bash", SCRIPT, *args], env=env, capture_output=True, text=True)

    def move_release(self):
        """The pin's own address stops working (a GitHub outage): its folder moves away, a copy stays for the cache."""
        gone = os.path.join(self.work, "release-moved")
        os.rename(self.release, gone)
        return gone

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

    def test_archives_mode_keeps_hash_checked_archives_and_extracts_nothing(self):
        cache = os.path.join(self.work, "cache")
        result = self.run_script("--archives", cache, "linux-x64", "win-x64")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(["duckdb-test-linux-x64.tar.gz", "duckdb-test-win-x64.tar.gz"], sorted(os.listdir(cache)))
        self.assertFalse(os.path.exists(self.dest))
        again = self.run_script("--archives", cache, "linux-x64")
        self.assertIn("is current", again.stdout)

    def test_a_cache_folder_is_read_when_the_pins_address_is_gone(self):
        cache = os.path.join(self.work, "cache")
        self.assertEqual(0, self.run_script("--archives", cache, "linux-x64").returncode)
        self.move_release()
        result = self.run_script("linux-x64", DUCKDB_RELEASE_BASE="file://" + cache)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.libs["linux-x64"], read(os.path.join(self.dest, "linux-x64", "libduckdb.so"), "rb"))

    def test_a_cache_with_a_changed_archive_is_not_trusted(self):
        cache = os.path.join(self.work, "cache")
        self.assertEqual(0, self.run_script("--archives", cache, "linux-x64").returncode)
        path = os.path.join(cache, "duckdb-test-linux-x64.tar.gz")
        data = bytearray(read(path, "rb"))
        data[len(data) // 2] ^= 0xFF
        write(path, bytes(data))
        # the pin's address still has the good one: the changed copy is refused and the next source is used
        result = self.run_script("linux-x64", DUCKDB_RELEASE_BASE="file://" + cache)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("checksum mismatch", result.stderr)
        # with nothing else to fall back to, the run fails
        self.move_release()
        failed = self.run_script("linux-x64", DUCKDB_RELEASE_BASE="file://" + cache, DUCKDB_DEST=os.path.join(self.work, "dest2"))
        self.assertNotEqual(0, failed.returncode)
        self.assertIn("checksum mismatch", failed.stderr)

    def test_a_cache_missing_an_archive_falls_back_to_the_pins_address(self):
        empty = os.path.join(self.work, "empty")
        os.makedirs(empty)
        result = self.run_script("linux-x64", DUCKDB_RELEASE_BASE="file://" + empty)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("no duckdb-test-linux-x64.tar.gz", result.stderr)

    def test_a_failed_download_names_every_url_and_the_cache_key_and_the_fallback_is_used(self):
        archive = "duckdb-test-linux-x64.tar.gz"
        dead = "file://" + os.path.join(self.work, "dead")
        text = read(self.pin).replace(f"BASE=file://{self.release}", f"BASE={dead}")
        write(self.pin, text)
        failed = self.run_script("linux-x64", DUCKDB_CACHE_KEY="duckdb-Linux-X64-host-abc")
        self.assertNotEqual(0, failed.returncode)
        self.assertIn(f"{dead}/{archive}", failed.stderr)
        self.assertIn("duckdb-Linux-X64-host-abc", failed.stderr)
        ok = self.run_script("linux-x64", DUCKDB_RELEASE_BASE_FALLBACK="file://" + self.release)
        self.assertEqual(0, ok.returncode, ok.stderr)
        write(self.pin, text + f"BASE_FALLBACK=file://{self.release}\n")
        ok = self.run_script("linux-x64", DUCKDB_DEST=os.path.join(self.work, "dest3"))
        self.assertEqual(0, ok.returncode, ok.stderr)

    def test_an_unreachable_https_source_is_retried_then_the_next_source_is_used(self):
        # port 9 on localhost refuses at once: a transient-looking failure the retries cannot cure
        text = read(self.pin).replace(f"BASE=file://{self.release}", "BASE=http://127.0.0.1:9/none")
        write(self.pin, text)
        result = self.run_script("linux-x64", DUCKDB_RELEASE_BASE_FALLBACK="file://" + self.release)
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("attempt 3 of 3", result.stderr)

    def test_an_unknown_platform_is_refused(self):
        result = self.run_script("plan9-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("unknown rid", result.stderr)


if __name__ == "__main__":
    unittest.main()
