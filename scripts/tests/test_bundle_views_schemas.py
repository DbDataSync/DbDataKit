import os
import sys
import unittest

sys.path.insert(0, os.path.join(os.path.dirname(__file__), ".."))
import bundle_views_schemas as bundler  # noqa: E402


class BundleTests(unittest.TestCase):
    def test_the_committed_bundles_are_what_the_sources_give(self):
        self.assertEqual(0, bundler.main(["--check"]))

    def test_a_bundle_has_no_reference_that_leaves_the_file(self):
        for kind in bundler.KINDS:
            text = bundler.text_of(kind)
            self.assertNotIn('"$ref": "http', text)
            self.assertNotIn("example.internal", text)

    def test_a_reference_to_something_that_is_not_in_defs_is_refused(self):
        def loader(name):
            if name == "a":
                return {"$schema": "x", "$id": "https://x.invalid/a.schema.json", "properties": {"p": {"$ref": "https://x.invalid/b.schema.json#/properties/q"}}}
            return {"$schema": "x", "$id": "https://x.invalid/b.schema.json", "properties": {"q": {}}}
        with self.assertRaises(SystemExit):
            bundler.bundle("a", loader)


if __name__ == "__main__":
    unittest.main()
