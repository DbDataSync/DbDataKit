import hashlib
import os
import shutil
import subprocess
import tarfile
import tempfile
import unittest
import zipfile

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SCRIPT = os.path.join(ROOT, "DbDataBuild", "scripts", "fetch-native.sh")
REAL_PIN = os.path.join(ROOT, "DbDataBuild", "scripts", "polyglot.pin")
LINUX = "polyglot-sql-ffi-linux-x86_64.tar.gz"
WINDOWS = "polyglot-sql-ffi-windows-x86_64.zip"


def sha(path):
    with open(path, "rb") as f:
        return hashlib.sha256(f.read()).hexdigest()


def read(path, mode="r"):
    with open(path, mode, **({} if "b" in mode else {"encoding": "utf-8"})) as f:
        return f.read()


def write(path, data):
    with open(path, "wb" if isinstance(data, bytes) else "w", **({} if isinstance(data, bytes) else {"encoding": "utf-8"})) as f:
        f.write(data)


class ThePin(unittest.TestCase):
    def test_the_pin_records_a_version_a_commit_and_both_hashes(self):
        text = read(REAL_PIN)
        for key in ("VERSION", "COMMIT", "SHA256_linux_x86_64", "SHA256_windows_x86_64"):
            self.assertRegex(text, rf"(?m)^{key}=\S+$")


class FetchNative(unittest.TestCase):
    """The script against a fake release and a fake pin: no network."""

    def setUp(self):
        self.work = tempfile.mkdtemp()
        self.release = os.path.join(self.work, "release")
        os.makedirs(self.release)
        self.libs = {"linux-x64": b"linux library\n" * 50, "win-x64": b"windows library\n" * 50}
        stage = os.path.join(self.work, "stage")
        os.makedirs(os.path.join(stage, "polyglot-sql-ffi-linux-x86_64"))
        write(os.path.join(stage, "polyglot-sql-ffi-linux-x86_64", "libpolyglot_sql_ffi.so"), self.libs["linux-x64"])
        with tarfile.open(os.path.join(self.release, LINUX), "w:gz") as t:
            t.add(os.path.join(stage, "polyglot-sql-ffi-linux-x86_64"), arcname="polyglot-sql-ffi-linux-x86_64")
        with zipfile.ZipFile(os.path.join(self.release, WINDOWS), "w") as z:
            z.writestr("polyglot-sql-ffi-windows-x86_64/polyglot_sql_ffi.dll", self.libs["win-x64"])
        self.pin = os.path.join(self.work, "test.pin")
        self.write_pin()
        self.dest = os.path.join(self.work, "native")

    def write_pin(self, linux_hash=None):
        write(self.pin, "VERSION=9.9.9\nCOMMIT=abc123\n"
                        f"SHA256_linux_x86_64={linux_hash or sha(os.path.join(self.release, LINUX))}\n"
                        f"SHA256_windows_x86_64={sha(os.path.join(self.release, WINDOWS))}\n")

    def tearDown(self):
        shutil.rmtree(self.work, ignore_errors=True)

    def run_script(self, *args, **extra):
        env = dict(os.environ, POLYGLOT_PIN=self.pin, POLYGLOT_DEST=self.dest, POLYGLOT_RETRY_DELAY="0", POLYGLOT_RETRIES="1",
                   # upstream is unreachable here: a copy must come from the base, and a miss must fail
                   HTTPS_PROXY="http://127.0.0.1:9", https_proxy="http://127.0.0.1:9", NO_PROXY="", no_proxy="")
        env.pop("POLYGLOT_RELEASE_BASE", None)
        env.update(extra)
        return subprocess.run(["bash", SCRIPT, *args], env=env, capture_output=True, text=True)

    def base(self, folder=None):
        return "file://" + (folder or self.release)

    def test_both_platforms_are_installed_from_a_file_base(self):
        result = self.run_script("all", POLYGLOT_RELEASE_BASE=self.base())
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.libs["linux-x64"], read(os.path.join(self.dest, "linux-x64", "libpolyglot_sql_ffi.so"), "rb"))
        self.assertEqual(self.libs["win-x64"], read(os.path.join(self.dest, "win-x64", "polyglot_sql_ffi.dll"), "rb"))
        self.assertEqual("abc123\n", read(os.path.join(self.dest, "linux-x64", ".polyglot-commit")))

    def test_a_second_run_finds_it_current_and_reads_nothing(self):
        self.assertEqual(0, self.run_script("linux-x64", POLYGLOT_RELEASE_BASE=self.base()).returncode)
        again = self.run_script("linux-x64", POLYGLOT_RELEASE_BASE=self.base(os.path.join(self.work, "nowhere")))
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertIn("is current", again.stdout)

    def test_archives_mode_keeps_hash_checked_archives_and_extracts_nothing(self):
        cache = os.path.join(self.work, "cache")
        result = self.run_script("--archives", cache, "all", POLYGLOT_RELEASE_BASE=self.base())
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(sorted([LINUX, WINDOWS]), sorted(os.listdir(cache)))
        self.assertFalse(os.path.exists(self.dest))
        # a hit: every archive is current, so even an empty base is not asked
        again = self.run_script("--archives", cache, "all", POLYGLOT_RELEASE_BASE=self.base(os.path.join(self.work, "nowhere")))
        self.assertEqual(0, again.returncode, again.stderr)
        self.assertEqual(2, again.stdout.count("is current"))

    def test_a_cache_folder_installs_when_the_release_is_gone(self):
        cache = os.path.join(self.work, "cache")
        self.assertEqual(0, self.run_script("--archives", cache, "linux-x64", POLYGLOT_RELEASE_BASE=self.base()).returncode)
        shutil.rmtree(self.release)
        result = self.run_script("linux-x64", POLYGLOT_RELEASE_BASE=self.base(cache))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertEqual(self.libs["linux-x64"], read(os.path.join(self.dest, "linux-x64", "libpolyglot_sql_ffi.so"), "rb"))

    def test_the_hash_is_the_one_in_the_pin_not_one_from_the_release(self):
        # a release (or a cache) with a changed archive, even one that ships its own matching checksum file, is refused
        write(os.path.join(self.release, LINUX + ".sha256"), "0\n")
        write(os.path.join(self.release, "checksums.sha256"), "0\n")
        self.write_pin(linux_hash="0" * 64)
        result = self.run_script("linux-x64", POLYGLOT_RELEASE_BASE=self.base())
        self.assertNotEqual(0, result.returncode)
        self.assertIn("checksum mismatch", result.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.dest, "linux-x64")))

    def test_a_cache_with_a_changed_archive_is_not_trusted(self):
        cache = os.path.join(self.work, "cache")
        self.assertEqual(0, self.run_script("--archives", cache, "linux-x64", POLYGLOT_RELEASE_BASE=self.base()).returncode)
        path = os.path.join(cache, LINUX)
        data = bytearray(read(path, "rb"))
        data[len(data) // 2] ^= 0xFF
        write(path, bytes(data))
        failed = self.run_script("linux-x64", POLYGLOT_RELEASE_BASE=self.base(cache))
        self.assertNotEqual(0, failed.returncode)
        self.assertIn(f"checksum mismatch for {LINUX} in file://", failed.stderr)

    def test_a_failed_download_names_every_url_and_the_cache_key(self):
        empty = os.path.join(self.work, "empty")
        os.makedirs(empty)
        failed = self.run_script("linux-x64", POLYGLOT_RELEASE_BASE=self.base(empty), POLYGLOT_CACHE_KEY="native-Linux-X64-host-abc")
        self.assertNotEqual(0, failed.returncode)
        self.assertIn(f"no {LINUX} in file://", failed.stderr)
        self.assertIn(f"file://{empty}/{LINUX}", failed.stderr)
        self.assertIn("https://github.com/tobilg/polyglot/releases/download/v9.9.9/" + LINUX, failed.stderr)
        self.assertIn("could not download", failed.stderr)
        self.assertIn("attempt 3 of 3", failed.stderr)
        self.assertIn("native-Linux-X64-host-abc", failed.stderr)

    def test_a_windows_style_file_base_is_understood(self):
        # Git Bash hands the script file:///D:/a/x; the helper must drop the slash before the drive letter
        text = read(SCRIPT)
        self.assertIn('[[ $p =~ ^/[A-Za-z]: ]]', text)
        self.assertIn('p="${p#/}"', text)

    def test_an_unknown_platform_is_refused(self):
        result = self.run_script("plan9-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("unknown rid", result.stderr)


if __name__ == "__main__":
    unittest.main()
