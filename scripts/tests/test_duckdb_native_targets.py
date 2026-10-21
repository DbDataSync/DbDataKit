import os
import shutil
import subprocess
import tempfile
import unittest

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
TARGETS = os.path.join(ROOT, "DbDataKit", "build", "DuckDb.Native.targets")

PROJECT = """<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Library</OutputType>
    <DuckDbNativeRid>{rid}</DuckDbNativeRid>
    <DuckDbNativeDir>{native}</DuckDbNativeDir>
  </PropertyGroup>
  <Import Project="{targets}" />
</Project>
"""


@unittest.skipUnless(shutil.which("dotnet"), "dotnet is needed")
class DuckDbNativeTargets(unittest.TestCase):
    def setUp(self):
        self.work = tempfile.mkdtemp()
        os.makedirs(os.path.join(self.work, "native"))
        # A folder of its own with no Directory.Build.props above it, so only the targets under test are in play.
        self.env = dict(os.environ, DOTNET_NOLOGO="1", DOTNET_CLI_TELEMETRY_OPTOUT="1")

    def tearDown(self):
        shutil.rmtree(self.work, ignore_errors=True)

    def build(self, rid, library=None):
        native = os.path.join(self.work, "native")
        if library:
            with open(os.path.join(native, library), "wb") as f:
                f.write(b"engine")
        with open(os.path.join(self.work, "P.csproj"), "w", encoding="utf-8") as f:
            f.write(PROJECT.format(rid=rid, native=native, targets=TARGETS))
        return subprocess.run(["dotnet", "build", os.path.join(self.work, "P.csproj"), "-v", "q", "--nologo"],
                              env=self.env, capture_output=True, text=True, cwd=self.work)

    def test_a_missing_library_stops_the_build_with_the_fetch_command(self):
        result = self.build("linux-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("scripts/fetch-duckdb.sh linux-x64", result.stdout)

    def test_a_platform_that_is_not_pinned_stops_the_build(self):
        result = self.build("linux-musl-x64")
        self.assertNotEqual(0, result.returncode)
        self.assertIn("No DuckDB engine is pinned for the runtime identifier 'linux-musl-x64'", result.stdout)

    def test_the_library_is_copied_next_to_the_output(self):
        result = self.build("linux-x64", "libduckdb.so")
        self.assertEqual(0, result.returncode, result.stdout)
        self.assertTrue(os.path.exists(os.path.join(self.work, "bin", "Debug", "net10.0", "libduckdb.so")))

    def test_each_platform_has_its_own_library_name(self):
        for rid, library in [("win-x64", "duckdb.dll"), ("osx-arm64", "libduckdb.dylib"), ("linux-arm64", "libduckdb.so")]:
            with self.subTest(rid=rid):
                shutil.rmtree(os.path.join(self.work, "bin"), ignore_errors=True)
                shutil.rmtree(os.path.join(self.work, "obj"), ignore_errors=True)
                result = self.build(rid, library)
                self.assertEqual(0, result.returncode, result.stdout)
                self.assertTrue(os.path.exists(os.path.join(self.work, "bin", "Debug", "net10.0", library)))


if __name__ == "__main__":
    unittest.main()
