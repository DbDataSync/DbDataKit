"""Tests for the suite manifest, `dist.py bind --suite` (dbdatakit-full.json, plan 33 section 6a, slice SC11).

The members here are fixtures (a root folder with one dist.json each), so the tests do not depend on which products have a dist.json yet;
`ShippedSuiteTests` checks the real dist/suite.json against the real files.
"""
import json
import pathlib
import subprocess
import sys
import tempfile
import unittest

DIST = pathlib.Path(__file__).resolve().parent.parent
REPO_ROOT = DIST.parent
REPO = "acme/kit"
VERSION = "2026.2.2.0200"


def member(product, exe=None, service=None, vc=False, win=True):
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
    return meta


MEMBERS = [
    ("dbdatakit", "console", member("dbdatakit", service={"name": "KitConsole", "register": "service install"})),
    ("dbdatagateway", "gateway", member("dbdatagateway", service={"name": "gw", "register": "service install --name {name}"}, vc=True)),
    ("dbdatabuild", "build", member("dbdatabuild")),          # no service: it is paired but not registered
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

    def test_it_only_packages(self):
        m = self.manifest()
        for key in ("post_install", "pre_uninstall", "installer", "uninstaller", "persist"):
            self.assertNotIn(key, m)
        self.assertEqual(sorted(m), ["architecture", "bin", "description", "extract_to", "homepage", "license", "notes", "suggest", "version"])

    def test_the_notes_name_each_service_and_each_pairing_by_hand(self):
        notes = self.manifest()["notes"]
        self.assertIn("dbdatakit service install", notes)
        self.assertIn("dbdatagateway service install --name gw", notes)
        self.assertNotIn("dbdatabuild service install", notes)      # dbdatabuild lists no service
        self.assertIn("dbdatakit server run", notes)
        self.assertIn("dbdatagateway server pair --max-role", notes)
        self.assertIn("dbdatabuild server pair --max-role", notes)
        self.assertNotIn("dbdatakit server pair", notes)             # the console is not paired with itself
        self.assertIn("dbdatakit instance add <https address of that server> --code <code>", notes)
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
