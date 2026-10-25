using DbDataConsole.Server;
using DbDataKit.Cli;
using DbDataKit.Web.Trust;

namespace DbDataConsole.Tests;

/// <summary>The console's <c>instance</c> verbs (plan 13, T04, T07 and U12) against a real pairing exchange.</summary>
public sealed class ConsoleInstanceCliTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ddk-console-cli-" + Guid.NewGuid().ToString("N")[..8]);
    private PairableServer server = null!;

    public async ValueTask InitializeAsync() => server = await PairableServer.StartAsync();

    public async ValueTask DisposeAsync()
    {
        await server.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<(int Exit, string Out, string Err, IDictionary<string, object?> Data)> Run(params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        var context = new CliContext(o, e, TextReader.Null);
        var exit = await ConsoleCli.RunAsync([.. args, "--repo", directory], context);
        return (exit, o.ToString(), e.ToString(), context.Data);
    }

    [Fact]
    public async Task A_server_is_paired_listed_and_forgotten_from_the_command_line()
    {
        var empty = await Run("instance", "list");
        Assert.Equal(0, empty.Exit);
        Assert.Contains("No server is paired", empty.Out);

        var added = await Run("instance", "add", server.Url.AbsoluteUri, "--code", server.NewCode(), "--name", "build-1");
        Assert.Equal(0, added.Exit);
        Assert.Equal(PairableServer.InstanceId, added.Data["id"]);
        Assert.Equal(server.Fingerprint, added.Data["fingerprint"]);
        Assert.Contains("pinned", added.Out);

        var listed = await Run("instance", "list");
        Assert.Contains("build-1", listed.Out);
        Assert.Contains("fake-server 9.9.9  ready", listed.Out);
        Assert.Equal(true, Assert.Single((IEnumerable<Dictionary<string, object?>>)listed.Data["instances"]!)["reachable"]);

        var offline = await Run("instance", "list", "--offline");
        Assert.DoesNotContain("ready", offline.Out);

        var removed = await Run("instance", "remove", "build-1");
        Assert.Equal(0, removed.Exit);
        Assert.Contains("server unpair", removed.Out);
        Assert.Equal(1, (await Run("instance", "remove", "build-1")).Exit);
        Assert.Contains("No server is paired", (await Run("instance", "list")).Out);
    }

    [Fact]
    public async Task A_bad_pairing_is_refused_and_says_why()
    {
        Assert.Equal(2, (await Run("instance", "add", server.Url.AbsoluteUri)).Exit);                                  // no code
        Assert.Equal(2, (await Run("instance", "add", "http://example.test", "--code", "x")).Exit);                      // not https
        var wrong = await Run("instance", "add", server.Url.AbsoluteUri, "--code", "AAAAA-BBBBB-CCCCC-DDDDD");
        Assert.Equal(1, wrong.Exit);
        Assert.Contains("code", wrong.Err);
        Assert.Empty(server.Clients.All());
    }

    [Fact]
    public async Task The_signing_key_is_rotated_on_every_server_without_pairing_again_and_a_server_that_was_down_is_picked_up_next_time()
    {
        Assert.Equal(0, (await Run("instance", "add", server.Url.AbsoluteUri, "--code", server.NewCode())).Exit);
        var before = Assert.Single(server.Clients.All()).PublicKey;

        var rotated = await Run("instance", "rotate-key");
        Assert.Equal(0, rotated.Exit);
        Assert.Contains("new key is the console's key", rotated.Out);
        var after = Assert.Single(server.Clients.All()).PublicKey;
        Assert.NotEqual(Convert.ToHexString(before), Convert.ToHexString(after));

        // the console still reaches the server with the new key
        Assert.Contains("ready", (await Run("instance", "list")).Out);
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_stays_on_the_old_key_and_the_rotation_says_to_run_it_again()
    {
        var port = server.Url.Port;
        var serverDirectory = server.Directory;
        Assert.Equal(0, (await Run("instance", "add", server.Url.AbsoluteUri, "--code", server.NewCode(), "--name", "flaky")).Exit);
        await server.StopAsync();

        var rotated = await Run("instance", "rotate-key");
        Assert.Equal(1, rotated.Exit);
        Assert.Contains("flaky: still on the old key", rotated.Out);
        Assert.Contains("Run 'instance rotate-key' again", rotated.Out);
        Assert.Equal(["flaky"], ((IEnumerable<string>)rotated.Data["pending"]!).ToList());

        // the same server comes back (same directory, so the same certificate and the same paired clients), and the next run finishes the job
        await using var again = await PairableServer.StartAsync(port, serverDirectory);
        var finished = await Run("instance", "rotate-key");
        Assert.Equal(0, finished.Exit);
        Assert.Contains("new key is the console's key", finished.Out);
    }
}
