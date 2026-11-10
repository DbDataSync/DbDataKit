namespace DbDataSync.Cli.Tests;

/// <summary>
/// Every <c>dbdatasync</c> command resolves its repo root the same way — see <see cref="DbDataSyncRoot"/>.
/// Exercised over a real temp directory tree rather than mocked, since the whole point of the walk-up
/// is filesystem behaviour (parent directories, presence/absence of a file) that a mock would just
/// restate.
/// </summary>
public sealed class DbDataSyncRootTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-root-tests-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void ExplicitRepoFlag_WinsOutright()
    {
        var elsewhere = Path.Combine(_root, "elsewhere");
        Directory.CreateDirectory(elsewhere);
        File.WriteAllText(Path.Combine(_root, DbDataSync.Core.Config.DbDataSyncConfigFile.FileName), "");

        var resolved = DbDataSyncRoot.Resolve(["--repo", elsewhere], _root);

        Assert.Equal(elsewhere, resolved);
    }

    [Fact]
    public void ExplicitRepoFlag_ShortCircuitsEvenWhenAConfigFileExistsOnTheWayUp()
    {
        var deep = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(deep);
        File.WriteAllText(Path.Combine(_root, DbDataSync.Core.Config.DbDataSyncConfigFile.FileName), "");

        var elsewhere = Path.Combine(_root, "elsewhere");
        var resolved = DbDataSyncRoot.Resolve(["--repo", elsewhere], deep);

        Assert.Equal(elsewhere, resolved);
    }

    [Fact]
    public void CwdHasTheFile_ResolvesToCwd()
    {
        File.WriteAllText(Path.Combine(_root, DbDataSync.Core.Config.DbDataSyncConfigFile.FileName), "");

        var resolved = DbDataSyncRoot.Resolve([], _root);

        Assert.Equal(_root, resolved);
    }

    [Fact]
    public void AParentSeveralLevelsUp_HasTheFile_WalksUpToFindIt()
    {
        File.WriteAllText(Path.Combine(_root, DbDataSync.Core.Config.DbDataSyncConfigFile.FileName), "");
        var deep = Path.Combine(_root, "a", "b", "c");
        Directory.CreateDirectory(deep);

        var resolved = DbDataSyncRoot.Resolve([], deep);

        Assert.Equal(_root, resolved);
    }

    [Fact]
    public void NeitherCwdNorAnyParent_HasTheFile_FallsBackToDefaultRoot()
    {
        var deep = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(deep);

        var resolved = DbDataSyncRoot.Resolve([], deep);

        Assert.Equal(CliOptions.DefaultRoot, resolved);
    }

    /// <summary>The environment step: <c>DBDATAKIT_DATA_SYNC</c> beats <see cref="CliOptions.DefaultRoot"/>
    /// but loses to an explicit <c>--repo</c> and to a real walk-up hit.</summary>
    [Fact]
    public void RepoRootEnvVar_BeatsDefaultRoot_ButLosesToExplicitRepoAndToAWalkUpHit()
    {
        var deep = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(deep);
        var envRoot = Path.Combine(_root, "env-root");

        Environment.SetEnvironmentVariable("DBDATAKIT_DATA_SYNC", envRoot);
        try
        {
            Assert.Equal(envRoot, DbDataSyncRoot.Resolve([], deep));

            var explicitRoot = Path.Combine(_root, "explicit-root");
            Assert.Equal(explicitRoot, DbDataSyncRoot.Resolve(["--repo", explicitRoot], deep));

            File.WriteAllText(Path.Combine(_root, DbDataSync.Core.Config.DbDataSyncConfigFile.FileName), "");
            Assert.Equal(_root, DbDataSyncRoot.Resolve([], deep));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBDATAKIT_DATA_SYNC", null);
        }
    }

    /// <summary>The install-wide root (<c>DBDATAKIT_DATA</c>) names the folder <c>sync</c> in it; the service's own variable beats it.</summary>
    [Fact]
    public void DataRootEnvVar_NamesTheSyncFolderInIt_AndTheServicesOwnVariableBeatsIt()
    {
        var deep = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(deep);
        var dataRoot = Path.Combine(_root, "data");
        var own = Path.Combine(_root, "own");

        Environment.SetEnvironmentVariable("DBDATAKIT_DATA", dataRoot);
        try
        {
            Assert.Equal(Path.Combine(dataRoot, "sync"), DbDataSyncRoot.Resolve([], deep));
            Environment.SetEnvironmentVariable("DBDATAKIT_DATA_SYNC", own);
            Assert.Equal(own, DbDataSyncRoot.Resolve([], deep));
            Assert.Equal(Path.Combine(_root, "x"), DbDataSyncRoot.Resolve(["--repo", Path.Combine(_root, "x")], deep));
        }
        finally
        {
            Environment.SetEnvironmentVariable("DBDATAKIT_DATA", null);
            Environment.SetEnvironmentVariable("DBDATAKIT_DATA_SYNC", null);
        }
    }

    /// <summary>Unset behaves exactly as before this phase — no accidental always-on default.</summary>
    [Fact]
    public void RepoRootEnvVar_Unset_FallsBackToDefaultRootAsBefore()
    {
        Environment.SetEnvironmentVariable("DBDATAKIT_DATA_SYNC", null);
        var deep = Path.Combine(_root, "a", "b");
        Directory.CreateDirectory(deep);

        var resolved = DbDataSyncRoot.Resolve([], deep);

        Assert.Equal(CliOptions.DefaultRoot, resolved);
    }
}
