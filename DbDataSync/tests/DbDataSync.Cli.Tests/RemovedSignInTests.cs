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
        File.WriteAllText(DbDataSyncConfigFile.PathIn(_root), "DbDataSync:\n  Auth:\n    Windows:\n      AdminGroup: Admins\n");

        Assert.Null(DbDataSyncRoot.LegacyRefusal(_root, baseDirectory: _root));
        await Task.CompletedTask;
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
