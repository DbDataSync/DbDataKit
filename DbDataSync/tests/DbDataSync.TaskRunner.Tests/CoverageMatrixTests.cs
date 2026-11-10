namespace DbDataSync.TaskRunner.Tests;

/// <summary>The coverage matrix (<c>DbDataSync/architecture/runner-coverage-matrix.md</c>) names every pair <see cref="ReplicationPairsTests"/> runs, and the paths it runs them on: a pair added to the tests without the page fails here.</summary>
public sealed class CoverageMatrixTests
{
    private static string Page()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "architecture", "runner-coverage-matrix.md");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
        }

        throw new InvalidOperationException("architecture/runner-coverage-matrix.md was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void EveryPairOfTheReplicationPairsTests_IsOnThePage_WithItsPaths()
    {
        var page = Page();
        foreach (var row in ReplicationPairsTests.Pairs.Select(r => (Source: (string)r[0], Target: (string)r[1])).Distinct())
        {
            var line = page.Split('\n').SingleOrDefault(l => l.StartsWith($"| {row.Source} → {row.Target} |", StringComparison.Ordinal));
            Assert.True(line is not null, $"{row.Source} → {row.Target} is not in the coverage matrix");
            var runsOnSupervisor = ReplicationPairsTests.Pairs.Any(r => (string)r[0] == row.Source && (string)r[1] == row.Target && (RunExecutorIntegrationTests.RunMode)r[2] == RunExecutorIntegrationTests.RunMode.Supervisor);
            Assert.Equal(runsOnSupervisor, line!.Split('|')[3].Trim() == "proven");
        }
    }
}
