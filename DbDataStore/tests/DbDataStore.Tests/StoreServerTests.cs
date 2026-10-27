using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Web.Trust;
using DbDataStore.Server;
using Microsoft.AspNetCore.Builder;

namespace DbDataStore.Tests;

/// <summary>The DbDataStore server (plan 19, Z01): a real HTTPS server in a directory of its own, paired the way a console pairs, reporting who it is and that it is well.</summary>
public sealed class StoreServerTests : IAsyncLifetime
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "dds-server-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ConsoleIdentity console = new("console-1", ConsoleSigningKey.Generate());
    private readonly List<string> log = [];
    private WebApplication app = null!;
    private Uri url = null!;
    private StoreSettings settings = null!;

    public async ValueTask InitializeAsync()
    {
        Directory.CreateDirectory(directory);
        settings = new StoreSettings(directory, new Uri("https://127.0.0.1:0"));
        app = StoreServer.Build(settings, StoreServer.Certificate(directory), log.Add);
        await app.StartAsync();
        url = new Uri(app.Urls.First());
    }

    public async ValueTask DisposeAsync()
    {
        await app.StopAsync();
        await app.DisposeAsync();
        try { Directory.Delete(directory, true); } catch (IOException) { }
    }

    private async Task<(HttpClient Http, PairedServer Paired)> Pair(ServerRole ceiling = ServerRole.Viewer)
    {
        var code = new PairingFiles(settings.ServerYaml).Codes().Create(ServerRoles.Close(ceiling));
        var paired = await PairingClient.PairAsync(url, code.Code, console, ClientKind.Console, "test console");
        return (new HttpClient(PairingClient.Pinned(paired.CertificateFingerprint)) { BaseAddress = url }, paired);
    }

    private static async Task<HttpResponseMessage> Send(HttpClient http, string path, string? token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null) request.Headers.TryAddWithoutValidation(ManagementAuthentication.TokenHeader, token);
        return await http.SendAsync(request);
    }

    [Fact]
    public async Task A_console_pairs_with_the_server_and_is_told_who_it_is()
    {
        var (http, paired) = await Pair();
        using var _ = http;
        var token = console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Viewer));

        var response = await Send(http, "/api/v1/info", token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var info = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.Equal("dbdatastore", (string)info["product"]!);
        Assert.Equal(StoreServer.Version, (string)info["version"]!);
        Assert.Equal(paired.InstanceId, (string)info["instance_id"]!);
        Assert.StartsWith("store-", paired.InstanceId);
    }

    [Fact]
    public async Task The_instance_id_is_the_same_after_a_restart_because_it_is_written_to_the_servers_directory()
    {
        var first = StoreServer.InstanceId(settings.ServerYaml);
        Assert.Equal(first, StoreServer.InstanceId(settings.ServerYaml));
        Assert.True(File.Exists(settings.ServerYaml));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Health_and_version_answer_without_a_token_and_everything_else_needs_one()
    {
        var (http, paired) = await Pair();
        using var _ = http;

        Assert.Equal(HttpStatusCode.OK, (await Send(http, "/health/live", null)).StatusCode);
        var version = JsonNode.Parse(await (await Send(http, "/version", null)).Content.ReadAsStringAsync())!;
        Assert.Equal("dbdatastore", (string)version["product"]!);

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, "/api/v1/info", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, "/api/v1/info", "garbage")).StatusCode);
        Assert.NotEmpty(log);

        // a token for another server is no use here
        var elsewhere = console.Issuer().Issue(new ConsoleTokenRequest("someone-else", "ada", "Ada", ServerRole.Viewer));
        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, "/api/v1/info", elsewhere)).StatusCode);
        Assert.Equal(paired.InstanceId, StoreServer.InstanceId(settings.ServerYaml));
    }

    [Fact]
    public async Task A_token_with_more_roles_than_the_pairing_allowed_is_refused()
    {
        var (http, paired) = await Pair(ServerRole.Viewer);
        using var _ = http;
        var token = console.Issuer().Issue(new ConsoleTokenRequest(paired.InstanceId, "ada", "Ada", ServerRole.Admin));

        Assert.Equal(HttpStatusCode.Unauthorized, (await Send(http, "/api/v1/info", token)).StatusCode);
    }

    [Fact]
    public async Task A_server_listens_on_https_only()
    {
        var plain = new StoreSettings(directory, new Uri("http://127.0.0.1:0"));
        var refused = Assert.Throws<ArgumentException>(() => StoreServer.Build(plain, StoreServer.Certificate(directory), _ => { }));
        Assert.Contains("HTTPS", refused.Message);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task The_command_line_pairs_a_client_and_lists_it_and_unpairs_it()
    {
        string Run(params string[] args)
        {
            var (output, error) = (new StringWriter(), new StringWriter());
            var exit = StoreCli.RunAsync([.. args, "--repo", directory], new CliContext(output, error, TextReader.Null)).GetAwaiter().GetResult();
            Assert.True(exit == 0, error.ToString());
            return output.ToString();
        }

        var made = Run("server", "pair", "--max-role", "viewer");
        var code = System.Text.RegularExpressions.Regex.Match(made, @"Pairing code: (\S+)").Groups[1].Value;
        Assert.NotEmpty(code);

        var paired = await PairingClient.PairAsync(url, code, console, ClientKind.Console, "from the command line");
        Assert.Contains("console-1", Run("server", "clients"));
        Run("server", "unpair", console.Id);
        Assert.DoesNotContain("console-1", Run("server", "clients"));
        Assert.NotNull(paired);
    }

    [Fact]
    public async Task The_command_line_has_the_shared_nouns_and_a_version()
    {
        var help = new StringWriter();
        await StoreCli.Router().RunAsync(["help"], new CliContext(help, new StringWriter(), TextReader.Null));
        foreach (var noun in new[] { "server", "service", "health" }) Assert.Contains(noun, help.ToString());

        // a health check of a server that is not there is exit 1, and its JSON document is the shared one
        var document = new StringWriter();
        var exit = await StoreCli.Router().RunAsync(["health", "check", "--url", "https://127.0.0.1:1/health/live", "--format", "json"], new CliContext(document, new StringWriter(), TextReader.Null));
        Assert.Equal(1, exit);
        Assert.Equal("dbdatastore.output/1", (string)JsonNode.Parse(document.ToString())!["schema"]!);
    }
}
