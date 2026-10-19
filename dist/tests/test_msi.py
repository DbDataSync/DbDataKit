"""Tests for dist/msi.py: the version mapping, the generated WiX-schema source and, where wixl is installed, the MSI it builds."""
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import unittest
import xml.etree.ElementTree as ET

DIST = pathlib.Path(__file__).resolve().parent.parent
sys.path.insert(0, str(DIST))
import msi  # noqa: E402

WIXL = shutil.which("wixl")
MSIINFO = shutil.which("msiinfo")
NS = {"w": "http://schemas.microsoft.com/wix/2006/wi"}

META = {
    "product": "widget",
    "displayName": "Widget",
    "description": "A widget.",
    "homepage": "https://example.test/widget",
    "license": "MIT",
    "platforms": {"win-x64": {"exe": "widget.exe"}},
    "service": {"name": "WidgetSvc", "register": "install --name {name}", "unregister": "uninstall --name {name}"},
    "msi": {"upgradeCode": "11111111-2222-3333-4444-555555555555", "addToPath": True},
}


def make_payload(root, extra=None):
    root = pathlib.Path(root)
    (root / "lib").mkdir(parents=True)
    (root / "widget.exe").write_bytes(b"exe")
    (root / "lib" / "a.dll").write_bytes(b"dll")
    (root / "README.md").write_text("hello")
    for name, content in (extra or {}).items():
        (root / name).write_text(content)
    return root


class VersionTests(unittest.TestCase):
    def test_calver_maps_to_three_fields(self):
        self.assertEqual(msi.msi_version("2026.10.19.509"), "26.10.27669")
        self.assertEqual(msi.msi_version("2026.1.1.0"), "26.1.1440")
        self.assertEqual(msi.msi_version("2026.12.31.2359"), "26.12.46079")   # still under the 65535 ceiling
        self.assertEqual(msi.msi_version("2026.10.19.509-beta"), "26.10.27669")

    def test_versions_sort_in_release_order(self):
        order = ["2026.10.19.509", "2026.10.19.1200", "2026.10.20.0", "2026.11.1.5", "2026.12.31.2359", "2027.1.1.0"]
        keys = [tuple(int(x) for x in msi.msi_version(v).split(".")) for v in order]
        self.assertEqual(keys, sorted(keys))
        self.assertEqual(len(set(keys)), len(keys))

    def test_rejects_what_is_not_a_calver_version(self):
        for bad in ("1.2.3", "2026.13.1.0", "2026.1.32.0", "2026.1.1.2460", "latest"):
            with self.assertRaises(SystemExit):
                msi.msi_version(bad)


class SourceTests(unittest.TestCase):
    def setUp(self):
        self.tmp = pathlib.Path(tempfile.mkdtemp(prefix="msi-tests-"))
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)

    def source(self, meta=META, version="2026.10.19.509", payload=None):
        payload = payload or make_payload(self.tmp / "payload")
        return msi.wxs(meta, version, payload), payload

    def root(self, text):
        return ET.fromstring(text)

    def test_one_component_per_file_with_guids_that_do_not_change_between_versions(self):
        a, _ = self.source()
        b, _ = self.source(version="2026.10.20.100", payload=self.tmp / "payload")
        guids = lambda text: {c.get("Id"): c.get("Guid") for c in self.root(text).iter("{%s}Component" % NS["w"])}
        self.assertEqual(len(guids(a)), 3)
        self.assertEqual(guids(a), guids(b))
        self.assertEqual(len(set(guids(a).values())), 3)

    def test_product_identity_and_upgrade(self):
        text, _ = self.source()
        product = self.root(text).find("w:Product", NS)
        self.assertEqual(product.get("UpgradeCode"), "{11111111-2222-3333-4444-555555555555}")
        self.assertEqual(product.get("Version"), "26.10.27669")
        self.assertEqual(product.get("Name"), "Widget")
        upgrade = product.find("w:Upgrade", NS)
        self.assertEqual(upgrade.get("Id"), "{11111111-2222-3333-4444-555555555555}")
        older, newer = upgrade.findall("w:UpgradeVersion", NS)
        self.assertEqual((older.get("Property"), older.get("Maximum"), older.get("IncludeMaximum")), ("WIX_UPGRADE_DETECTED", "26.10.27669", "no"))
        self.assertEqual((newer.get("Property"), newer.get("Minimum"), newer.get("OnlyDetect")), ("WIX_DOWNGRADE_DETECTED", "26.10.27669", "yes"))
        self.assertIn("NOT WIX_DOWNGRADE_DETECTED", product.find("w:Condition", NS).text)   # a downgrade is refused
        self.assertIsNone(product.find("w:MajorUpgrade", NS))   # wixl cannot schedule it after InstallFinalize
        self.assertEqual(product.find("w:Package", NS).get("InstallScope"), "perMachine")

    def test_service_actions_and_their_conditions(self):
        text, _ = self.source()
        product = self.root(text).find("w:Product", NS)
        actions = {a.get("Id"): a for a in product.findall("w:CustomAction", NS)}
        self.assertEqual(actions["RegisterService"].get("ExeCommand"), "install --name WidgetSvc")
        self.assertEqual(actions["UnregisterService"].get("ExeCommand"), "uninstall --name WidgetSvc")
        self.assertEqual(actions["RegisterService"].get("Return"), "check")
        self.assertEqual(actions["UnregisterService"].get("Return"), "ignore")
        sequence = {c.get("Action"): (c.text or "") for c in product.find("w:InstallExecuteSequence", NS)}
        self.assertIn("NOT Installed", sequence["RegisterService"])
        self.assertIn("NOT WIX_UPGRADE_DETECTED", sequence["RegisterService"])   # an upgrade keeps the registration it has
        self.assertIn('INSTALLSERVICE="1"', sequence["RegisterService"])
        self.assertIn("NOT UPGRADINGPRODUCTCODE", sequence["UnregisterService"])   # and removing the old version does not unregister it
        self.assertIn("WIX_UPGRADE_DETECTED", sequence["StartService"])
        self.assertIn("Stop-Service -Name 'WidgetSvc'", actions["StopService"].get("ExeCommand"))
        # stopped for an upgrade's file copy and for a plain removal, but not when the OLD product is removed after the upgrade
        self.assertIn("WIX_UPGRADE_DETECTED", sequence["StopService"])
        self.assertIn("NOT UPGRADINGPRODUCTCODE", sequence["StopService"])
        self.assertNotIn("ServiceControl", text)   # it would stop the service the upgrade just started, when the old product goes
        self.assertIn("RemoveExistingProducts", sequence)
        self.assertTrue(any(p.get("Id") == "INSTALLSERVICE" for p in product.findall("w:Property", NS)))

    def test_path_actions_escape_square_brackets_and_fail_the_install_when_the_edit_fails(self):
        text, _ = self.source()
        product = self.root(text).find("w:Product", NS)
        actions = {a.get("Id"): a for a in product.findall("w:CustomAction", NS)}
        add = actions["AddToPath"]
        self.assertEqual(add.get("Return"), "check")
        command = add.get("ExeCommand")
        self.assertIn("[INSTALLFOLDER]", command)                              # the one real property reference
        self.assertIn("[\\[]Microsoft.Win32.RegistryValueOptions[\\]]", command)   # literal brackets are escaped
        self.assertNotIn("[Microsoft", command)
        self.assertIn("$ErrorActionPreference = 'Stop'", command)
        self.assertEqual(actions["RemoveFromPath"].get("Return"), "ignore")

    def test_no_service_and_no_path_means_no_custom_actions_beyond_the_helper(self):
        meta = {k: v for k, v in META.items() if k != "service"}
        meta["msi"] = {"upgradeCode": META["msi"]["upgradeCode"]}
        text, _ = self.source(meta)
        ids = [a.get("Id") for a in self.root(text).find("w:Product", NS).findall("w:CustomAction", NS)]
        self.assertEqual(ids, ["SetPowerShell"])
        self.assertNotIn("ServiceControl", text)

    def test_a_product_that_needs_dotnet_checks_for_the_runtime_before_changing_anything(self):
        text, _ = self.source(dict(META, requiresDotnet="10.0"))
        product = self.root(text).find("w:Product", NS)
        check = next(a for a in product.findall("w:CustomAction", NS) if a.get("Id") == "CheckDotnetRuntime")
        self.assertEqual(check.get("Execute"), "immediate")           # before InstallInitialize: nothing has been changed when it fails
        self.assertEqual(check.get("Return"), "check")
        command = check.get("ExeCommand")
        self.assertIn("needs the .NET 10 runtime", command)           # Windows Installer shows the failed command, so the sentence is in it
        self.assertIn("-Filter '10.*'", command)
        self.assertIn("$found", command)                              # the $ survived (it is doubled in the source)
        row = next(c for c in product.find("w:InstallExecuteSequence", NS) if c.get("Action") == "CheckDotnetRuntime")
        self.assertEqual(row.get("Sequence"), "1460")
        self.assertIn("NOT SKIPDOTNETCHECK", row.text)
        self.assertLess(1460, 1500)
        plain, _ = self.source(payload=self.tmp / "payload")
        self.assertNotIn("CheckDotnetRuntime", plain)

    def test_no_command_puts_a_bracket_inside_braces(self):
        # Windows Installer treats {...} that contains a [...] as a conditional group and drops the braces, which breaks the PowerShell around it.
        import re
        for requires in (None, "10.0"):
            meta = dict(META, requiresDotnet=requires) if requires else META
            text, _ = self.source(meta, payload=self.tmp / "payload") if (self.tmp / "payload").exists() else self.source(meta)
            for action in self.root(text).iter("{%s}CustomAction" % NS["w"]):
                command = action.get("ExeCommand") or action.get("Value") or ""
                for group in re.findall(r"\{[^{}]*\}", command):
                    self.assertNotIn("[", group, f"{action.get('Id')}: {group}")

    def test_names_that_need_escaping_stay_well_formed(self):
        payload = make_payload(self.tmp / "odd", {"a&b.txt": "x"})
        text, _ = self.source(payload=payload)
        self.root(text)
        self.assertIn("a&amp;b.txt", text)

    def test_refuses_a_package_without_the_executable_and_a_product_without_an_upgrade_code(self):
        empty = self.tmp / "empty"
        empty.mkdir()
        with self.assertRaises(SystemExit):
            msi.wxs(META, "2026.10.19.509", empty)
        meta = dict(META, msi={})
        with self.assertRaises(SystemExit):
            msi.wxs(meta, "2026.10.19.509", make_payload(self.tmp / "p2"))

    def test_every_products_dist_json_describes_a_valid_installer(self):
        for product in ("DbDataBuild", "DbDataSync", "DbDataProxy"):
            meta = json.loads((DIST.parent / product / "dist.json").read_text())
            payload = make_payload(self.tmp / product, {meta["platforms"]["win-x64"]["exe"]: "x"})
            self.root(msi.wxs(meta, "2026.10.19.509", payload))
        codes = {json.loads((DIST.parent / p / "dist.json").read_text())["msi"]["upgradeCode"] for p in ("DbDataBuild", "DbDataSync", "DbDataProxy")}
        self.assertEqual(len(codes), 3)


@unittest.skipUnless(WIXL and MSIINFO, "wixl and msiinfo (msitools) not installed")
class BuildTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        cls.tmp = pathlib.Path(tempfile.mkdtemp(prefix="msi-build-"))
        payload = make_payload(cls.tmp / "payload", {"cost$.txt": "x"})
        meta = cls.tmp / "dist.json"
        meta.write_text(json.dumps(META))
        cls.out = cls.tmp / "widget.msi"
        subprocess.run([sys.executable, "-I", str(DIST / "msi.py"), "build", "--meta", str(meta), "--version", "2026.10.19.509",
                        "--payload", str(payload), "--out", str(cls.out)], check=True, capture_output=True)

    @classmethod
    def tearDownClass(cls):
        shutil.rmtree(cls.tmp, ignore_errors=True)

    def table(self, name):
        return subprocess.run([MSIINFO, "export", str(self.out), name], check=True, capture_output=True, text=True).stdout

    def test_it_is_a_64_bit_installer_with_the_upgrade_wiring(self):
        info = subprocess.run([MSIINFO, "suminfo", str(self.out)], check=True, capture_output=True, text=True).stdout
        self.assertIn("x64;1033", info)
        upgrade = self.table("Upgrade")
        self.assertIn("{11111111-2222-3333-4444-555555555555}", upgrade)
        self.assertIn("WIX_UPGRADE_DETECTED", upgrade)
        self.assertIn("WIX_DOWNGRADE_DETECTED", upgrade)
        self.assertIn("NOT WIX_DOWNGRADE_DETECTED", self.table("LaunchCondition"))
        self.assertIn("WIX_UPGRADE_DETECTED;WIX_DOWNGRADE_DETECTED", self.table("Property"))   # passed to the elevated install

    def test_custom_actions_are_deferred_and_unimpersonated(self):
        rows = {line.split("\t")[0]: line.split("\t") for line in self.table("CustomAction").splitlines()[3:] if "\t" in line}
        self.assertEqual(rows["RegisterService"][1], "3090")    # exe from an installed file, in the script, no impersonation, checked
        self.assertEqual(rows["UnregisterService"][1], "3154")   # the same, failure ignored
        self.assertEqual(rows["AddToPath"][1], "3122")           # exe named by a property
        self.assertEqual(rows["StopService"][1], "3186")   # the same, failure ignored
        self.assertEqual(rows["SetPowerShell"][1], "51")         # sets the property

    def test_the_sequence_stops_the_service_before_files_go_and_registers_after_they_arrive(self):
        seq = {}
        for line in self.table("InstallExecuteSequence").splitlines()[3:]:
            parts = line.split("\t")
            if len(parts) == 3 and parts[2].isdigit():
                seq[parts[0]] = int(parts[2])
        self.assertLess(seq["StopService"], seq["RemoveFiles"])
        self.assertLess(seq["UnregisterService"], seq["RemoveFiles"])
        self.assertLess(seq["InstallFiles"], seq["RegisterService"])
        self.assertLess(seq["RegisterService"], seq["StartService"])
        # The old version is removed AFTER the new one is installed: removed first, Windows Installer skips files it still sees (identical ones)
        # and the removal then deletes them. The helper property is set before any deferred action is scheduled.
        self.assertGreater(seq["RemoveExistingProducts"], seq["InstallFinalize"])
        self.assertLess(seq["SetPowerShell"], seq["StopService"])
        self.assertLess(seq["InstallInitialize"], seq["StopService"])
        self.assertLess(seq["StopService"], seq["UnregisterService"])
        self.assertLess(seq["StopService"], seq["InstallFiles"])
        self.assertLess(seq["RemoveFromPath"], seq["RemoveFiles"])
        self.assertLess(seq["StartService"], seq["AddToPath"])

    def test_the_runtime_check_is_an_immediate_exe_action_before_the_script_starts(self):
        meta = self.tmp / "dotnet.json"
        meta.write_text(json.dumps(dict(META, requiresDotnet="10.0")))
        out = self.tmp / "dotnet.msi"
        subprocess.run([sys.executable, "-I", str(DIST / "msi.py"), "build", "--meta", str(meta), "--version", "2026.10.19.509",
                        "--payload", str(self.tmp / "payload"), "--out", str(out)], check=True, capture_output=True)
        actions = subprocess.run([MSIINFO, "export", str(out), "CustomAction"], check=True, capture_output=True, text=True).stdout
        rows = {line.split("\t")[0]: line.split("\t") for line in actions.splitlines()[3:] if "\t" in line}
        self.assertEqual(rows["CheckDotnetRuntime"][1], "50")      # exe named by a property, run in the immediate sequence, failure fatal
        self.assertIn("$found", rows["CheckDotnetRuntime"][3])

    def test_dollar_signs_survive_in_commands_and_file_names(self):
        # wixl's preprocessor drops a lone $; the generator doubles it. A PowerShell command without its $ is a different, failing, command.
        commands = {line.split("\t")[0]: line.split("\t")[3] for line in self.table("CustomAction").splitlines()[3:] if line.count("\t") >= 3}
        self.assertIn("$ErrorActionPreference = 'Stop'", commands["AddToPath"])
        self.assertIn("$k = Get-Item", commands["AddToPath"])
        self.assertNotIn("$$", commands["AddToPath"])
        names = self.table("File")
        self.assertIn("cost$.txt", names)

    def test_no_table_the_windows_installer_would_have_to_guess_about(self):
        self.assertNotIn("Environment", subprocess.run([MSIINFO, "tables", str(self.out)], capture_output=True, text=True).stdout.split())


if __name__ == "__main__":
    unittest.main()
