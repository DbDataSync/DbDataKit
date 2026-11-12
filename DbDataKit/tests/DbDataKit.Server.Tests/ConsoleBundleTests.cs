using System.Net;
using DbDataKit.Cli;
using DbDataKit.Server;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace DbDataKit.Server.Tests;

/// <summary>
/// The single-machine bundle (plan 23a FQ18, D8, QF6): a server of this machine that holds the bundle token is paired by the console when it announces, with nobody confirming; a server without the token, with another one, or from another machine
/// still waits for an administrator; the token file is the account's own (mode 0600) and a token others can read is not used.
/// </summary>
public sealed class ConsoleBundleTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-bundle-" + Guid.NewGuid().ToString("N")[..8]);
    private WebApplication app = null!;
    private PairableServer server = null!;
    private Uri announce = null!;

    public async ValueTask InitializeAsync()
    {
        server = await PairableServer.StartAsync();
        announce = new Uri($"http://127.0.0.1:{TestPorts.Next()}");
        app = ConsoleHost.Build(new ConsoleSettings(directory, new Uri("https://localhost:0"), null, announce), ConsoleHost.Certificate(directory), _ => { }, webRoot: Path.Combine(directory, "none"));
        await app.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        await server.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private ConsoleAnnouncer Announcer(string? tokenFile) =>
        new("dbdatagateway", PairableServer.InstanceId, "Test gateway", () => server.Url.Port, server.Url, announce, server.Clients, server.Codes, bundleTokenFile: tokenFile);

    private string MakeBundle(params string[] members) => ConsoleBundle.Init(directory, members, rotate: false);

    [Fact]
    public async Task A_server_that_holds_the_token_is_paired_at_once_and_the_audit_says_so_without_the_token()
    {
        var tokenFile = MakeBundle();
        using var client = new HttpClient();

        Assert.True(await Announcer(tokenFile).AnnounceOnceAsync(client));

        Assert.Equal("dbdatakit console", Assert.Single(server.Clients.All()).Name);                      // nobody confirmed
        Assert.Equal(server.Fingerprint, app.Services.GetRequiredService<InstanceRegistry>().All().Single().CertificateFingerprint);
        Assert.Empty(app.Services.GetRequiredService<PendingInstances>().All());
        var one = Assert.Single(app.Services.GetRequiredService<AuditLog>().Read(new AuditQuery(Verb: "pairing.auto"))).Entry;
        Assert.Equal(("bundle", AuditEvent.Ok), (one.ViaKind, one.Outcome));
        Assert.DoesNotContain(File.ReadAllText(tokenFile).Trim(), one.Detail ?? "");
    }

    [Fact]
    public async Task A_server_without_the_token_or_with_another_waits_for_an_administrator()
    {
        MakeBundle();
        var wrong = Path.Combine(directory, "wrong.token");
        File.WriteAllText(wrong, new string('x', 43) + "\n");
        using var client = new HttpClient();

        Assert.True(await Announcer(null).AnnounceOnceAsync(client));
        Assert.True(await Announcer(wrong).AnnounceOnceAsync(client));

        Assert.Single(app.Services.GetRequiredService<PendingInstances>().All());
        Assert.Empty(server.Clients.All());
        Assert.Empty(app.Services.GetRequiredService<InstanceRegistry>().All());
    }

    [Fact]
    public async Task A_console_with_no_bundle_pairs_nothing_by_itself_whatever_the_server_sends()
    {
        var tokenFile = Path.Combine(directory, "bundle.token");
        Directory.CreateDirectory(directory);
        File.WriteAllText(tokenFile, new string('y', 43) + "\n");
        using var client = new HttpClient();

        Assert.True(await Announcer(tokenFile).AnnounceOnceAsync(client));

        Assert.Single(app.Services.GetRequiredService<PendingInstances>().All());
        Assert.Empty(server.Clients.All());
    }

    [Fact]
    public async Task A_product_that_is_not_a_member_waits()
    {
        var tokenFile = MakeBundle("dbdatasync");
        using var client = new HttpClient();

        Assert.True(await Announcer(tokenFile).AnnounceOnceAsync(client));       // this one announces as dbdatagateway

        Assert.Single(app.Services.GetRequiredService<PendingInstances>().All());
        Assert.Empty(server.Clients.All());
    }

    [Fact]
    public void The_token_is_accepted_only_from_this_machine_and_in_constant_time_form()
    {
        MakeBundle();
        var (bundle, problem) = ConsoleBundle.Load(directory);
        Assert.Null(problem);
        var token = File.ReadAllText(bundle!.TokenFile).Trim();

        Assert.True(bundle.Accepts(token, "dbdatasync", IPAddress.Loopback));
        Assert.True(bundle.Accepts(token, "dbdatasync", IPAddress.IPv6Loopback));
        Assert.True(bundle.Accepts(token, "dbdatasync", IPAddress.Loopback.MapToIPv6()));
        Assert.False(bundle.Accepts(token, "dbdatasync", IPAddress.Parse("10.1.2.3")));       // another machine, even with the token
        Assert.False(bundle.Accepts(token + "x", "dbdatasync", IPAddress.Loopback));
        Assert.False(bundle.Accepts(null, "dbdatasync", IPAddress.Loopback));
        Assert.False(bundle.Accepts(token, "dbdatasync", null));
    }

    [Fact]
    public void The_token_file_is_the_accounts_own_and_a_token_others_can_read_is_not_used()
    {
        var tokenFile = MakeBundle();
        Assert.True(File.ReadAllText(tokenFile).Trim().Length >= 32);
        if (OperatingSystem.IsWindows()) return;                                              // the Windows ACL is the installer's (a Windows host was not available)
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(tokenFile));

        File.SetUnixFileMode(tokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
        var (bundle, problem) = ConsoleBundle.Load(directory);
        Assert.Null(bundle);
        Assert.Contains("other accounts can read the bundle token", problem);
        Assert.Contains("chmod 600", problem);
    }

    [Fact]
    public async Task The_command_makes_the_files_keeps_the_token_and_rotates_it_on_request()
    {
        async Task<(int Exit, IDictionary<string, object?> Data)> Run(params string[] args)
        {
            var context = new CliContext(new StringWriter(), new StringWriter(), TextReader.Null);
            return (await ConsoleCli.RunAsync(args, context), context.Data);
        }

        var first = await Run("bundle", "init", "--members", "dbdatasync,dbdatabuild", "--repo", directory);
        Assert.Equal(0, first.Exit);
        var tokenFile = (string)first.Data["token_file"]!;
        var token = File.ReadAllText(tokenFile);
        Assert.Contains("\"schema\": \"bundle/1\"", File.ReadAllText(Path.Combine(directory, "bundle.json")));

        await Run("bundle", "init", "--repo", directory);
        Assert.Equal(token, File.ReadAllText(tokenFile));                                      // kept
        await Run("bundle", "init", "--rotate", "--repo", directory);
        Assert.NotEqual(token, File.ReadAllText(tokenFile));

        var shown = await Run("bundle", "show", "--repo", directory);
        Assert.Equal(0, shown.Exit);
        Assert.Equal(true, shown.Data["enabled"]);
        Assert.Empty((System.Collections.IList)shown.Data["members"]!);               // the last init named none: any product of this machine
        Assert.NotEqual(0, (await Run("bundle", "init", "--members", "nonsense", "--repo", directory)).Exit);
    }
}
