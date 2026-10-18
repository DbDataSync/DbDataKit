namespace DbDataBuild.Tests.Unit;

/// <summary>
/// Golden-file comparison. Files live in tests/DbDataBuild.Tests.Unit/Golden. A missing file fails the test; set UPDATE_GOLDEN=1 to write
/// the actual output (then review the diff before committing).
/// </summary>
internal static class GoldenFile
{
    public static void Assert(string name, string actual)
    {
        var path = Path.Combine(PolyglotBindingTests.RepoRoot(), "tests", "DbDataBuild.Tests.Unit", "Golden", name);
        var normalized = actual.Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, normalized);
            return;
        }
        Xunit.Assert.True(File.Exists(path), $"Golden file missing: {name}. Run with UPDATE_GOLDEN=1, then review it.");
        Xunit.Assert.Equal(File.ReadAllText(path).Replace("\r\n", "\n"), normalized);
    }
}
