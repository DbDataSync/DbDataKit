using DbDataSync.Core.Config;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// Plan 13, X05 (clean break): Sync's own sign-in remnants are gone, and none goes quietly. <c>dbdatasync invite</c> says where invitations are made now, and
/// a <c>server.yaml</c> that still sets <c>Auth:Passkeys:*</c> (or <c>App:AlternateUrls</c>, which only widened the passkey origins) stops <c>server run</c> naming the setting.
/// </summary>
public sealed class RemovedSignInTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-removed-signin-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    [Theory]
    [InlineData("DbDataSync:\n  Auth:\n    Passkeys:\n      RelyingPartyId: example.com\n", "DbDataSync:Auth:Passkeys:RelyingPartyId")]
    [InlineData("DbDataSync:\n  Auth:\n    Passkeys:\n      Mode: disabled\n", "DbDataSync:Auth:Passkeys:Mode")]
    [InlineData("DbDataSync:\n  App:\n    AlternateUrls: https://other.example\n", "DbDataSync:App:AlternateUrls")]
    [InlineData("DbDataSync:\n  Auth:\n    Windows:\n      AdminGroup: Admins\n", "DbDataSync:Auth:Windows:AdminGroup")]
    [InlineData("DbDataSync:\n  Auth:\n    Windows:\n      Mode: disabled\n", "DbDataSync:Auth:Windows:Mode")]
    [InlineData("DbDataSync:\n  Auth:\n    Network:\n      Admin: loopback\n", "DbDataSync:Auth:Network:Admin")]
    [InlineData("DbDataSync:\n  Auth:\n    Network:\n      Viewer: remote\n", "DbDataSync:Auth:Network:Viewer")]
    public async Task AServerYamlThatStillSetsARemovedSetting_StopsServerRun_NamingIt_AndCreatesNothingElse(string yaml, string key)
    {
        var file = DbDataSyncConfigFile.PathIn(_root);
        File.WriteAllText(file, yaml);

        var (exitCode, error) = await RunServer();

        Assert.Equal(1, exitCode);
        Assert.Contains($"'{key}'", error);
        Assert.Contains(file, error);
        Assert.Contains("no longer read", error);
        Assert.False(Directory.Exists(Path.Combine(_root, ".git")), "nothing is created beside a refused setting");
        Assert.False(File.Exists(Path.Combine(_root, "FIRST-RUN.txt")));
    }

    [Fact]
    public async Task AServerYamlWithoutThem_IsNotRefusedForThem()
    {
        File.WriteAllText(DbDataSyncConfigFile.PathIn(_root), "DbDataSync:\n  App:\n    Url: http://localhost:5080\n");

        Assert.Null(DbDataSyncRoot.LegacyRefusal(_root, baseDirectory: _root));
        await Task.CompletedTask;
    }

    /// <summary>Plan 13a, X06: the refusal of Windows sign-in and network trust names the file too, when the check is the one every command makes (not only <c>server run</c>).</summary>
    [Fact]
    public void ASettingOfWindowsSignIn_IsRefusedByTheRootCheck_NamingTheFile()
    {
        var file = DbDataSyncConfigFile.PathIn(_root);
        File.WriteAllText(file, "DbDataSync:\n  Auth:\n    Windows:\n      AdminGroup: Admins\n");

        var refusal = DbDataSyncRoot.LegacyRefusal(_root, baseDirectory: _root);

        Assert.NotNull(refusal);
        Assert.Contains("'DbDataSync:Auth:Windows:AdminGroup'", refusal);
        Assert.Contains(file, refusal);
    }

    [Theory]
    [InlineData("invite")]
    [InlineData("INVITE")]
    public void TheInviteCommand_IsGone_AndSaysWhereInvitationsAreMadeNow(string command)
    {
        var message = RetiredCommands.RemovedBecause(command);

        Assert.NotNull(message);
        Assert.Contains("dbdatasync invite", message);
        Assert.Contains("dbdatakit user invite", message);
        Assert.False(SyncCli.Legacy.Contains(command), "it is not dispatched any more");
        Assert.Null(typeof(ServeCommand).Assembly.GetType("DbDataSync.Cli.InviteCommand"));
    }

    [Theory]
    [InlineData("server")]
    [InlineData("health")]
    public void ACurrentCommand_IsNotReportedRemoved(string command) => Assert.Null(RetiredCommands.RemovedBecause(command));

    private async Task<(int ExitCode, string Error)> RunServer()
    {
        var originalErr = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            return (await ServeCommand.RunAsync(["--repo", _root]), error.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
