using DbDataSync.Api;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataSync.Api.Tests;

/// <summary>
/// Plan 13, E04 (clean break): the host the API's own entry point builds reads <c>server.yaml</c>, the environment and the command line, and refuses a settings
/// file of an earlier release by name instead of reading it.
/// </summary>
public sealed class LegacyHostSettingsHostTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-host-legacy-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Theory]
    [InlineData("appsettings.json")]
    [InlineData("dbdatasync.config.yaml")]
    public void AnOldSettingsFileInTheRepoRoot_StopsTheHost_NamingTheFile(string name)
    {
        var old = Path.Combine(_root, name);
        File.WriteAllText(old, name.EndsWith(".json") ? "{}" : "DbDataSync:\n  Url: http://localhost:5080\n");

        var failure = Assert.Throws<InvalidOperationException>(() => DbDataSyncHost.Build(["--DbDataSync:App:RepoRoot", _root]));

        Assert.Contains(old, failure.Message);
        Assert.Contains("no longer read", failure.Message);
    }

    [Fact]
    public void TheHostAddsNoJsonSettingsFile()
    {
        // A fresh repository root: nothing refused. A current directory with its own appsettings.json would have been read by the stock host.
        var original = Directory.GetCurrentDirectory();
        var elsewhere = Directory.CreateTempSubdirectory("dbdatasync-host-cwd-").FullName;
        File.WriteAllText(Path.Combine(elsewhere, "appsettings.json"), "{ \"DbDataSync\": { \"App\": { \"Url\": \"http://from-appsettings:1\" } } }");
        Directory.SetCurrentDirectory(elsewhere);
        try
        {
            var app = DbDataSyncHost.Build(["--DbDataSync:App:RepoRoot", _root, "--DbDataSync:State:Port", "0"]);

            Assert.Null(app.Configuration["DbDataSync:App:Url"]);
        }
        finally
        {
            Directory.SetCurrentDirectory(original);
            Directory.Delete(elsewhere, recursive: true);
        }
    }

    /// <summary>Plan 13, X05 and plan 13a, X06: a layer other than server.yaml that still sets a removed setting stops the host too, naming it.</summary>
    [Theory]
    [InlineData("--DbDataSync:Auth:Passkeys:RelyingPartyId=example.com", "DbDataSync:Auth:Passkeys:RelyingPartyId")]
    [InlineData("--DbDataSync:App:AlternateUrls=https://other.example", "DbDataSync:App:AlternateUrls")]
    [InlineData("--DbDataSync:Auth:Windows:AdminGroup=Admins", "DbDataSync:Auth:Windows:AdminGroup")]
    [InlineData("--DbDataSync:Auth:Network:Admin=loopback", "DbDataSync:Auth:Network:Admin")]
    public void ARemovedSettingOnTheCommandLine_StopsTheHost_NamingIt(string argument, string key)
    {
        var failure = Assert.Throws<InvalidOperationException>(() => DbDataSyncHost.Build(["--DbDataSync:App:RepoRoot", _root, argument]));

        Assert.Contains($"'{key}'", failure.Message);
    }

    /// <summary>Plan 13a, X06: a server.yaml that sets Windows sign-in or network trust is refused, and the message names the setting and the file.</summary>
    [Theory]
    [InlineData("DbDataSync:\n  Auth:\n    Windows:\n      AdminGroup: Admins\n", "DbDataSync:Auth:Windows:AdminGroup")]
    [InlineData("DbDataSync:\n  Auth:\n    Windows:\n      ViewerGroup: Viewers\n", "DbDataSync:Auth:Windows:ViewerGroup")]
    [InlineData("DbDataSync:\n  Auth:\n    Network:\n      Admin: loopback\n", "DbDataSync:Auth:Network:Admin")]
    [InlineData("DbDataSync:\n  Auth:\n    Network:\n      Viewer: remote\n", "DbDataSync:Auth:Network:Viewer")]
    public void AServerYamlThatSetsWindowsSignInOrNetworkTrust_StopsTheHost_NamingTheSettingAndTheFile(string yaml, string key)
    {
        File.WriteAllText(Path.Combine(_root, "server.yaml"), yaml);

        var failure = Assert.Throws<InvalidOperationException>(() => DbDataSyncHost.Build(["--DbDataSync:App:RepoRoot", _root]));

        Assert.Contains($"'{key}'", failure.Message);
        Assert.Contains("server.yaml", failure.Message);
        Assert.Contains("no longer read", failure.Message);
    }

    [Fact]
    public void ARemovedSettingInServerYaml_StopsTheHost_NamingIt()
    {
        File.WriteAllText(Path.Combine(_root, "server.yaml"), "DbDataSync:\n  Auth:\n    Passkeys:\n      Mode: enabled\n");

        var failure = Assert.Throws<InvalidOperationException>(() => DbDataSyncHost.Build(["--DbDataSync:App:RepoRoot", _root]));

        Assert.Contains("'DbDataSync:Auth:Passkeys:Mode'", failure.Message);
    }

    [Fact]
    public void SyncRunsNoFirstRunInvitation_AndHasNoPasskeys_NorUsersSessionsOrInvitations()
    {
        var app = DbDataSyncHost.Build(["--DbDataSync:App:RepoRoot", _root, "--DbDataSync:State:Port", "0"]);

        Assert.DoesNotContain(app.Services.GetServices<Microsoft.Extensions.Hosting.IHostedService>(), service => service is DbDataKit.Web.Auth.BootstrapInvite);
        Assert.False(app.Services.GetRequiredService<DbDataKit.Web.Auth.PasskeyOptions>().Enabled);
        Assert.False(File.Exists(Path.Combine(_root, DbDataKit.Web.Auth.BootstrapInvite.FileName)));
        // Plan 13a, X06: no sign-in of its own, so nothing that stores a user, a session or an invitation is registered.
        Assert.Null(app.Services.GetService<DbDataKit.State.UserStore>());
        Assert.Null(app.Services.GetService<DbDataKit.State.SessionStore>());
        Assert.Null(app.Services.GetService<DbDataKit.State.InviteStore>());
        Assert.Null(app.Services.GetService<DbDataKit.State.IUserAccounts>());
        Assert.Null(app.Services.GetService<DbDataKit.Web.Auth.PasskeyService>());
    }
}
