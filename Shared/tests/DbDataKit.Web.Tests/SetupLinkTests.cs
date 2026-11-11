using DbDataKit.State;
using DbDataKit.Web.Auth;
using Microsoft.Extensions.Logging.Abstractions;

namespace DbDataKit.Web.Tests;

/// <summary>Plan 23a FQ19: the setup link of a new install: its lifetime setting, the link as a whole address, and the first-run host that writes it.</summary>
public sealed class SetupLinkTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("ddk-setup-link-").FullName;

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Settings(string text)
    {
        var path = Path.Combine(_root, "settings.yaml");
        File.WriteAllText(path, text);
        return path;
    }

    private (UserStore Users, InviteStore Invites) Stores()
    {
        var database = new StateDatabase(Path.Combine(_root, "state.db"), new StateSchema([IdentitySchema.Accounts, IdentitySchema.Invites, IdentitySchema.AccountKeys]));
        return (new UserStore(database), new InviteStore(database));
    }

    [Fact]
    public void No_file_or_no_section_is_24_hours_and_a_section_sets_it_beside_what_else_the_file_holds()
    {
        Assert.Equal(TimeSpan.FromHours(24), SetupLinkSettingsFile.Load(Path.Combine(_root, "none.yaml")).Lifetime);
        Assert.Equal(TimeSpan.FromHours(24), SetupLinkSettingsFile.Load(Settings("session:\n  length_hours: 8\n")).Lifetime);
        Assert.Equal(TimeSpan.FromHours(6), SetupLinkSettingsFile.Load(Settings("session:\n  length_hours: 8\nsetup:\n  link_lifetime_hours: 6\nrunner:\n  enabled: false\n")).Lifetime);
        Assert.Equal(TimeSpan.FromMinutes(90), SetupLinkSettingsFile.Load(Settings("setup:\n  link_lifetime_hours: 1.5\n")).Lifetime);
    }

    [Theory]
    [InlineData("setup:\n  link_lifetime_hours: 0\n", "line 2", "setup.link_lifetime_hours")]
    [InlineData("setup:\n  link_lifetime_hours: 169\n", "line 2", "from 1 to 168")]
    [InlineData("setup:\n  link_lifetime_hours: soon\n", "line 2", "'soon'")]
    [InlineData("a: 1\nsetup:\n  lifetime: 5\n", "line 3", "'lifetime' is not a setup setting")]
    [InlineData("setup: 5\n", "line 1", "setup is a set of settings")]
    public void A_setting_that_is_not_one_or_is_out_of_range_is_refused_naming_the_file_and_line(string text, string line, string message)
    {
        var path = Settings(text);
        var ex = Assert.Throws<InvalidOperationException>(() => SetupLinkSettingsFile.Load(path));
        Assert.Contains("settings.yaml", ex.Message);
        Assert.Contains(line, ex.Message);
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void The_link_is_a_whole_address_with_the_code_in_the_fragment_and_the_next_replaces_it()
    {
        var (_, invites) = Stores();

        var first = SetupLink.Make(invites, new SetupLinkSettings(TimeSpan.FromHours(6)), "https://console.example:8480/");
        Assert.StartsWith("https://console.example:8480/invite#", first.Url);
        Assert.InRange(first.ExpiresAtUtc - DateTimeOffset.UtcNow, TimeSpan.FromHours(5.9), TimeSpan.FromHours(6.1));
        var firstCode = first.Url.Split('#')[1];
        Assert.NotNull(invites.Find(firstCode));

        var second = SetupLink.Make(invites, null, null);
        Assert.StartsWith("/invite#", second.Url);                                       // without an address it is the path
        Assert.Null(invites.Find(firstCode));
        Assert.Single(invites.ListOutstanding());
    }

    [Fact]
    public async Task A_link_that_has_ended_is_not_valid_and_one_that_was_used_is_not_valid()
    {
        var (users, invites) = Stores();
        var ended = SetupLink.Make(invites, new SetupLinkSettings(TimeSpan.FromMilliseconds(1)), "https://x");
        await Task.Delay(50);
        Assert.Null(invites.Find(ended.Url.Split('#')[1]));

        var live = SetupLink.Make(invites, SetupLinkSettings.Default, "https://x");
        var invite = invites.Find(live.Url.Split('#')[1])!;
        var ada = users.CreateUser("Ada", null, UserRole.Admin);
        Assert.True(invites.Redeem(invite.Id, ada.Id));
        Assert.Null(invites.Find(live.Url.Split('#')[1]));
        Assert.False(invites.Redeem(invite.Id, ada.Id));
    }

    [Fact]
    public async Task The_first_run_host_writes_the_link_the_setting_asks_for_and_the_command_that_makes_a_new_one()
    {
        var (users, invites) = Stores();
        var product = new AuthProduct("DbDataKit", "dbdatakit user invite", "dbdatakit setup link");
        var bootstrap = new BootstrapInvite(users, invites, new AuthOptions { Product = product, AdminGroup = "g", NetworkAdmin = AdminNetworkTrust.Disabled },
            new BootstrapInviteLocation(_root, "https://console.example:8480"), NullLogger<BootstrapInvite>.Instance, new SetupLinkSettings(TimeSpan.FromHours(3)));

        await bootstrap.StartAsync(CancellationToken.None);

        var text = File.ReadAllText(Path.Combine(_root, BootstrapInvite.FileName));
        Assert.Contains("https://console.example:8480/invite#", text);
        Assert.Contains("`dbdatakit setup link`", text);
        Assert.DoesNotContain("user invite", text);
        var invite = Assert.Single(invites.ListOutstanding());
        Assert.InRange(invite.ExpiresAtUtc - DateTimeOffset.UtcNow, TimeSpan.FromHours(2.9), TimeSpan.FromHours(3.1));
    }
}
