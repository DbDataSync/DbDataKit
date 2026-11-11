import importlib.util
import json
import os
import re
import sys
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SCRIPT = os.path.join(ROOT, "scripts", "fetch_cldr_snapshot.py")
spec = importlib.util.spec_from_file_location("fetch_cldr_snapshot", SCRIPT)
tool = importlib.util.module_from_spec(spec)
spec.loader.exec_module(tool)


def snapshot():
    with open(tool.SNAPSHOT, encoding="utf-8") as f:
        return json.load(f)


class TheCldrSnapshot(unittest.TestCase):
    def test_the_file_has_the_hash_the_pin_records(self):
        pin = tool.read_pin()
        self.assertEqual(pin["SNAPSHOT_SHA256"], tool.sha256_of(tool.SNAPSHOT), "cldr-snapshot.json changed without scripts/fetch_cldr_snapshot.py --write")

    def test_the_source_is_named_with_version_licence_and_package_integrity(self):
        pin = tool.read_pin()
        source = snapshot()["source"]
        self.assertEqual(pin["CLDR_VERSION"], source["version"])
        self.assertEqual("Unicode-3.0", source["licence"])
        for package in tool.PACKAGES:
            self.assertEqual(pin[f"PACKAGE_{package}_INTEGRITY"], source["packages"][package]["integrity"])
            self.assertEqual(pin["CLDR_VERSION"], source["packages"][package]["version"])
            self.assertRegex(pin[f"PACKAGE_{package}_INTEGRITY"], r"^sha512-[A-Za-z0-9+/]{86}==$")

    def test_it_holds_exactly_the_locales_of_the_hand_written_table_with_every_field(self):
        with open(tool.LOCALES_JSON, encoding="utf-8") as f:
            ours = json.load(f)
        locales = snapshot()["locales"]
        self.assertEqual(list(ours), list(locales))
        for code, data in locales.items():
            self.assertEqual({"decimal", "group", "months", "short"}, set(data), code)
            self.assertEqual(12, len(data["months"]), code)
            self.assertEqual(12, len(data["short"]), code)
            self.assertNotEqual(data["decimal"], data["group"], code)

    def test_it_holds_the_currency_symbols_and_the_compact_suffixes(self):
        data = snapshot()
        self.assertEqual(set(tool.CURRENCIES), set(data["currencySymbols"]["symbols"]))
        self.assertEqual({"locale", "thousand", "million", "billion"}, set(data["compactSuffixes"]))
        for suffix in ("thousand", "million", "billion"):
            self.assertTrue(re.fullmatch(r"0[A-Za-z]+", data["compactSuffixes"][suffix]), suffix)

    def test_the_offline_check_passes(self):
        old = sys.argv
        sys.argv = ["fetch_cldr_snapshot.py"]
        try:
            self.assertEqual(0, tool.main())
        finally:
            sys.argv = old


if __name__ == "__main__":
    unittest.main()
