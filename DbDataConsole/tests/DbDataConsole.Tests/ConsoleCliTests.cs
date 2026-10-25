using System.Text.Json.Nodes;
using DbDataConsole.Server;
using DbDataKit.Cli;
using DbDataKit.State;

namespace DbDataConsole.Tests;

/// <summary>The console's command line (plan 13, U07): the shared grammar, and <c>user list|invite</c> over the same state database the server uses.</summary>
public sealed class ConsoleCliTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-cli-" + Guid.NewGuid().ToString("N")[..8]);

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

    [Fact]
    public async Task The_console_has_the_shared_nouns_and_its_own_user_noun()
    {
        var help = await Run("help");
        Assert.Equal(0, help.Exit);
        foreach (var noun in new[] { "server", "user", "service", "health" })
            Assert.Contains(noun, help.Out);
    }

    [Fact]
    public async Task A_wrong_start_is_refused_with_the_usage_exit_code()
    {
        Assert.Equal(2, (await Run("server", "run", "--repo", directory, "--listen", "http://localhost:1")).Exit);
        Assert.Equal(2, (await Run("server", "run", "--repo", directory, "--bogus")).Exit);
        Assert.Equal(2, (await Run("user", "invite", "--repo", directory, "--role", "emperor")).Exit);
        Assert.Equal(1, (await Run("user", "invite", "--repo", directory, "--user", "nobody")).Exit);
    }

    [Fact]
    public async Task An_invitation_can_be_printed_before_the_console_has_ever_started_and_is_good()
    {
        var made = await Run("user", "invite", "--repo", directory, "--role", "viewer", "--url", "https://console.example:8480/");
        Assert.Equal(0, made.Exit);
        var url = (string)made.Data["url"]!;
        Assert.StartsWith("https://console.example:8480/invite#", url);

        var code = url.Split('#')[1];
        var invites = new InviteStore(ConsoleState.Open(directory));
        var found = invites.Find(code);
        Assert.NotNull(found);
        Assert.Equal(UserRole.Viewer, found!.Role);
    }

    [Fact]
    public async Task The_user_list_shows_who_can_sign_in_and_says_so_when_nobody_can()
    {
        var empty = await Run("user", "list", "--repo", directory);
        Assert.Equal(0, empty.Exit);
        Assert.Contains("No user yet", empty.Out);

        var users = new UserStore(ConsoleState.Open(directory));
        var ada = users.CreateUser("Ada", "ada@example.com", UserRole.Admin);
        users.AddCredential(ada.Id, CredentialMethods.Passkey, "cred-1", "key", label: "Passkey");

        var list = await Run("user", "list", "--repo", directory);
        Assert.Contains("Ada  Admin  enabled  1 way(s) to sign in", list.Out);
        var row = Assert.Single((IEnumerable<Dictionary<string, object?>>)list.Data["users"]!);
        Assert.Equal("Ada", row["name"]);
        Assert.Equal(1, row["credentials"]);
    }

    [Fact]
    public async Task The_directory_comes_from_the_variable_when_there_is_no_option()
    {
        Assert.Equal(directory, ConsoleSettings.DefaultDirectory(v => v == ConsoleSettings.DirectoryVariable ? directory : null));
        Assert.EndsWith(Path.Combine("dbdatakit", "console"), ConsoleSettings.DefaultDirectory(_ => null));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_service_noun_prints_a_systemd_unit_that_runs_the_console()
    {
        var unit = await Run("service", "unit", "--name", "dbdatakit");
        Assert.Equal(0, unit.Exit);
        Assert.Contains("server run", unit.Out);
        Assert.Contains("dbdatakit", unit.Out);
    }
}
