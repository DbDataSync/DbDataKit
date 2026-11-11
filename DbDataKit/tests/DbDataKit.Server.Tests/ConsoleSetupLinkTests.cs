using DbDataKit.Cli;
using DbDataKit.Server;
using DbDataKit.State;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The setup link of a new install (plan 23a FQ19): <c>dbdatakit setup link</c> on the machine makes the first-run invitation again, as an address that works once and ends, replaces the unused links, rewrites <c>FIRST-RUN.txt</c>, is
/// recorded in the audit, and is refused once the console has a user. The link's lifetime is the setting <c>setup.link_lifetime_hours</c> of <c>settings.yaml</c>.
/// </summary>
public sealed class ConsoleSetupLinkTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-setup-link-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var context = new CliContext(o, e, TextReader.Null);
        var exit = await ConsoleCli.RunAsync(args, context);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    private static string CodeOf(string url) => url.Split("/invite#")[1];

    private StateDatabase Open() => ConsoleState.Open(directory, ConsoleState.Resolve(directory).Settings);

    [Fact]
    public async Task The_command_prints_a_link_that_ends_and_writes_it_to_first_run_and_the_audit_says_who_made_it()
    {
        var made = await Run("setup", "link", "--url", "https://console.example:8480", "--repo", directory);

        Assert.Equal(0, made.Exit);
        var url = (string)made.Data["url"]!;
        Assert.StartsWith("https://console.example:8480/invite#", url);
        Assert.Contains(url, made.Out);
        var expires = DateTimeOffset.Parse((string)made.Data["expires"]!);
        Assert.InRange(expires - DateTimeOffset.UtcNow, TimeSpan.FromHours(23.9), TimeSpan.FromHours(24.1));      // the default, QF5

        var text = File.ReadAllText(Path.Combine(directory, "FIRST-RUN.txt"));
        Assert.Contains(url, text);
        Assert.Contains("dbdatakit setup link", text);
        Assert.Contains("works once", text);

        var database = Open();
        var invite = new IdentityInviteStore(database).Find(CodeOf(url));
        Assert.NotNull(invite);
        Assert.Equal(UserRole.Admin, invite.Role);

        var entry = Assert.Single(new AuditLog(database, ConsoleState.ServiceId).Read(new AuditQuery(Verb: "setup.link")));
        Assert.Equal(("command", "setup", AuditEvent.Ok), (entry.Entry.ViaKind, entry.Entry.TargetKind, entry.Entry.Outcome));
        Assert.DoesNotContain(CodeOf(url), entry.Entry.Detail ?? "");                                              // never the code
    }

    [Fact]
    public async Task A_new_link_replaces_the_old_one_which_is_dead_and_the_link_works_once()
    {
        var first = CodeOf((string)(await Run("setup", "link", "--repo", directory)).Data["url"]!);
        var second = CodeOf((string)(await Run("setup", "link", "--repo", directory)).Data["url"]!);

        var database = Open();
        var invites = new IdentityInviteStore(database);
        Assert.Null(invites.Find(first));
        var invite = invites.Find(second);
        Assert.NotNull(invite);
        Assert.Contains(second, File.ReadAllText(Path.Combine(directory, "FIRST-RUN.txt")));
        Assert.DoesNotContain(first, File.ReadAllText(Path.Combine(directory, "FIRST-RUN.txt")));

        // redeemed by the first administrator, it is spent
        var admin = new IdentityUserStore(database).CreateUser("Ada", null, UserRole.Admin);
        Assert.True(invites.Redeem(invite.Id, admin.Id));
        Assert.Null(invites.Find(second));
        Assert.False(invites.Redeem(invite.Id, admin.Id));
    }

    [Fact]
    public async Task The_lifetime_is_a_setting_and_a_bad_one_is_refused_naming_the_file_and_line()
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "settings.yaml"), "setup:\n  link_lifetime_hours: 6\n");
        var made = await Run("setup", "link", "--repo", directory);
        Assert.Equal(0, made.Exit);
        Assert.InRange(DateTimeOffset.Parse((string)made.Data["expires"]!) - DateTimeOffset.UtcNow, TimeSpan.FromHours(5.9), TimeSpan.FromHours(6.1));

        File.WriteAllText(Path.Combine(directory, "settings.yaml"), "setup:\n  link_lifetime_hours: 500\n");
        var bad = await Run("setup", "link", "--repo", directory);
        Assert.NotEqual(0, bad.Exit);
        Assert.Contains("settings.yaml, line 2", bad.Err);
        Assert.Contains("setup.link_lifetime_hours", bad.Err);
    }

    [Fact]
    public async Task Once_the_console_has_a_user_there_is_no_setup_link_to_make_and_the_message_names_the_invitation()
    {
        await Run("setup", "link", "--repo", directory);
        new IdentityUserStore(Open()).CreateUser("Ada", null, UserRole.Admin);

        var again = await Run("setup", "link", "--repo", directory);

        Assert.NotEqual(0, again.Exit);
        Assert.Contains("already", again.Err);
        Assert.Contains("user invite", again.Err);
        Assert.Single(new AuditLog(Open(), ConsoleState.ServiceId).Read(new AuditQuery(Verb: "setup.link")));        // the refused one made nothing
    }

    [Fact]
    public async Task The_command_is_in_the_help_and_takes_no_stray_words()
    {
        Assert.Contains("setup", (await Run("help")).Out);
        var stray = await Run("setup", "link", "now", "--repo", directory);
        Assert.Equal(2, stray.Exit);
        Assert.False(File.Exists(Path.Combine(directory, "FIRST-RUN.txt")));
    }
}
