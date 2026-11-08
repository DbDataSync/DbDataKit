import hashlib
import io
import os
import re
import shutil
import subprocess
import tarfile
import tempfile
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SCRIPT = os.path.join(ROOT, "scripts", "fetch-fonts.sh")
FAMILIES = ["noto-sans", "noto-serif", "noto-sans-mono"]


def sha(data):
    return hashlib.sha256(data).hexdigest()


def read_pin(path):
    """(single keys, FILE entries as (family, path, sha256))."""
    pin, files = {}, []
    with open(path, encoding="utf-8") as f:
        for line in f:
            line = line.rstrip("\n")
            m = re.match(r"^FILE=([^|]+)\|([^|]+)\|([0-9a-f]{64})$", line)
            if m:
                files.append(m.groups())
                continue
            m = re.match(r"^([A-Za-z0-9_]+)=(.*)$", line)
            if m:
                pin.setdefault(m.group(1), m.group(2))
    return pin, files


class ThePin(unittest.TestCase):
    def setUp(self):
        self.pin, self.files = read_pin(os.path.join(ROOT, "fonts.pin"))

    def test_every_family_has_an_archive_a_licence_an_alias_and_hashes(self):
        self.assertEqual(FAMILIES, self.pin["FAMILIES"].split())
        for family in FAMILIES:
            key = family.replace("-", "_")
            self.assertTrue(self.pin["ARCHIVE_" + key].endswith(".tgz"), family)
            self.assertRegex(self.pin["ARCHIVE_SHA256_" + key], r"^[0-9a-f]{64}$", family)
            self.assertRegex(self.pin["LICENSE_SHA256_" + key], r"^[0-9a-f]{64}$", family)
            self.assertEqual("OFL-1.1", self.pin["LICENSE_" + key], "the licence of every font is recorded and is the OFL")
            self.assertTrue(self.pin["ALIAS_" + key].startswith("DbDataKit "), family)
            mine = [f for f in self.files if f[0] == family]
            self.assertTrue(mine, family)
            for _, path, _ in mine:
                self.assertTrue(path.endswith(".woff2"), path)
                self.assertTrue(os.path.basename(path).startswith(family + "-"), path)
            for weight in ("400", "700"):
                self.assertTrue(any("-latin-" + weight + "-normal" in f[1] for f in mine), f"{family} {weight}")
        self.assertEqual(len(self.files), len({os.path.basename(f[1]) for f in self.files}), "file names are unique")
        self.assertEqual({f[0] for f in self.files}, set(FAMILIES))

    def test_the_licence_texts_in_the_repository_are_the_pinned_ones(self):
        for family in FAMILIES:
            with open(os.path.join(ROOT, "licenses", "fonts", family + "-OFL.txt"), "rb") as f:
                text = f.read()
            self.assertEqual(self.pin["LICENSE_SHA256_" + family.replace("-", "_")], sha(text), family)
            self.assertIn(b"SIL OPEN FONT LICENSE Version 1.1", text)

    def test_the_gitignore_names_the_install_folder(self):
        with open(os.path.join(ROOT, ".gitignore"), encoding="utf-8") as f:
            self.assertIn(".fonts/", f.read().split())


@unittest.skipUnless(shutil.which("bash") and shutil.which("curl") and shutil.which("tar"), "bash, curl and tar are needed")
class FetchFonts(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.mkdtemp()
        self.addCleanup(shutil.rmtree, self.work, True)
        self.release = os.path.join(self.work, "release")
        self.contents = {}
        lines = ["VERSION=9.9.9", f"BASE=file://{self.release}", "FAMILIES=" + " ".join(FAMILIES)]
        file_lines = []
        for family in FAMILIES:
            key = family.replace("-", "_")
            archive = f"{family}/-/{family}-9.9.9.tgz"
            path = os.path.join(self.release, *archive.split("/"))
            os.makedirs(os.path.dirname(path), exist_ok=True)
            licence = f"licence of {family}".encode()
            members = {"package/LICENSE": licence, "package/README.md": b"not pinned"}
            for name in ("latin-400-normal", "latin-700-normal"):
                members[f"package/files/{family}-{name}.woff2"] = f"{family} {name}".encode()
                file_lines.append(f"FILE={family}|package/files/{family}-{name}.woff2|{sha(members[f'package/files/{family}-{name}.woff2'])}")
            with tarfile.open(path, "w:gz") as t:
                for name, data in members.items():
                    info = tarfile.TarInfo(name)
                    info.size = len(data)
                    t.addfile(info, io.BytesIO(data))
            with open(path, "rb") as f:
                archive_sha = sha(f.read())
            self.contents[family] = members
            lines += [f"ARCHIVE_{key}={archive}", f"ARCHIVE_SHA256_{key}={archive_sha}", f"LICENSE_{key}=OFL-1.1", f"LICENSE_FILE_{key}=package/LICENSE", f"LICENSE_SHA256_{key}={sha(licence)}"]
        self.pin = os.path.join(self.work, "fonts.pin")
        with open(self.pin, "w", encoding="utf-8") as f:
            f.write("\n".join(lines + file_lines) + "\n")
        self.dest = os.path.join(self.work, "dest")

    def run_script(self, *args):
        env = dict(os.environ, FONTS_PIN=self.pin, FONTS_DEST=self.dest)
        return subprocess.run(["bash", SCRIPT, *args], env=env, capture_output=True, text=True)

    def read(self, family, name):
        with open(os.path.join(self.dest, family, name), "rb") as f:
            return f.read()

    def test_the_files_and_the_licence_are_installed_once_and_then_found_current(self):
        first = self.run_script("noto-sans")
        self.assertEqual(0, first.returncode, first.stderr)
        self.assertEqual(b"noto-sans latin-400-normal", self.read("noto-sans", "noto-sans-latin-400-normal.woff2"))
        self.assertEqual(b"licence of noto-sans", self.read("noto-sans", "OFL.txt"))
        self.assertFalse(os.path.exists(os.path.join(self.dest, "noto-sans", "README.md")), "only the pinned files are installed")
        self.assertIn("is current", self.run_script("noto-sans").stdout)

    def test_no_argument_and_all_install_every_family(self):
        self.assertEqual(0, self.run_script().returncode)
        for family in FAMILIES:
            self.assertEqual(f"{family} latin-700-normal".encode(), self.read(family, f"{family}-latin-700-normal.woff2"))
        shutil.rmtree(self.dest)
        self.assertEqual(0, self.run_script("all").returncode)
        self.assertTrue(os.path.exists(os.path.join(self.dest, "noto-sans-mono", "OFL.txt")))

    def test_a_changed_archive_is_refused_and_nothing_is_installed(self):
        with open(os.path.join(self.release, "noto-sans", "-", "noto-sans-9.9.9.tgz"), "ab") as f:
            f.write(b"x")
        result = self.run_script("noto-sans")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("checksum mismatch", result.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.dest, "noto-sans")))

    def test_a_changed_font_inside_a_good_archive_is_refused(self):
        path = os.path.join(self.release, "noto-sans", "-", "noto-sans-9.9.9.tgz")
        members = dict(self.contents["noto-sans"])
        members["package/files/noto-sans-latin-400-normal.woff2"] = b"tampered"
        with tarfile.open(path, "w:gz") as t:
            for name, data in members.items():
                info = tarfile.TarInfo(name)
                info.size = len(data)
                t.addfile(info, io.BytesIO(data))
        with open(path, "rb") as f:
            archive_sha = sha(f.read())
        with open(self.pin, encoding="utf-8") as f:
            text = f.read()
        with open(self.pin, "w", encoding="utf-8") as f:
            f.write(re.sub(r"(ARCHIVE_SHA256_noto_sans=).*", r"\g<1>" + archive_sha, text))
        result = self.run_script("noto-sans")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("inside", result.stderr)
        self.assertFalse(os.path.exists(os.path.join(self.dest, "noto-sans")))

    def test_a_changed_installed_file_is_fetched_again(self):
        self.assertEqual(0, self.run_script("noto-serif").returncode)
        with open(os.path.join(self.dest, "noto-serif", "noto-serif-latin-400-normal.woff2"), "wb") as f:
            f.write(b"edited")
        self.assertIn("fetched", self.run_script("noto-serif").stdout)
        self.assertEqual(b"noto-serif latin-400-normal", self.read("noto-serif", "noto-serif-latin-400-normal.woff2"))

    def test_an_unknown_family_is_refused(self):
        self.assertNotEqual(0, self.run_script("comic-sans").returncode)


if __name__ == "__main__":
    unittest.main()
