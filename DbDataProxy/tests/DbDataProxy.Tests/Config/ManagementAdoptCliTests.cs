using System.Net;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Hosting;

namespace DbDataProxy.Tests.Config;

/// <summary><c>dbdataproxy datasource adopt</c> (plan 13a, T13): the verb makes the same call as the management API's adopt, over the local channel, as the person at this machine.</summary>
public sealed partial class ManagementTests
{
    /// <summary>The command line reads the data directory's <c>server.yaml</c> to find the local channel, so the test names it there as well as on the command line the server started with.</summary>
    private async Task StartWithLocalChannelAsync()
    {
        var socketDirectory = Path.Combine(dataDirectory, "run");
        await StartAsync(localChannel: true, localDirectory: socketDirectory);
        File.AppendAllText(Path.Combine(ConfigDirectory, "server.yaml"), $"management: {{ localChannel: true, localChannelDirectory: '{socketDirectory}' }}\n");
    }

    private async Task<(int Exit, string Out, string Error)> RunCliAsync(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Cli.RunAsync([.. args, "--repo", dataDirectory], new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    private string ConnectionPath(string name) => Path.Combine(ConfigDirectory, "connections", name + ".yaml");

    [Fact]
    public async Task The_adopt_verb_hands_a_hand_made_data_source_to_a_server_client_by_name_or_id_and_the_audit_names_the_person_at_this_machine()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix socket's folder is chosen here; the Windows pipe has one name per machine");
        await StartWithLocalChannelAsync();
        var admin = await PairAsync();
        var publisher = await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/second", ServerRole.Admin, Put(Plain("second"), null));
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsAsync(build1, publisher, HttpMethod.Get, "/api/v1/data-sources/by-hand", ServerRole.Publisher)).StatusCode);

        var adopted = await RunCliAsync("datasource", "adopt", "by-hand", "build-1");
        Assert.Equal(0, adopted.Exit);
        Assert.Contains("Adopted 'by-hand'", adopted.Out);
        Assert.Contains("managed_by: build-1", File.ReadAllText(ConnectionPath("by-hand")));
        Assert.Equal(HttpStatusCode.OK, (await CallAsAsync(build1, publisher, HttpMethod.Get, "/api/v1/data-sources/by-hand", ServerRole.Publisher)).StatusCode);   // the client manages it now

        // by the client's name, and as a JSON document; adopting what is already the client's changes nothing
        var again = await RunCliAsync("datasource", "adopt", "by-hand", "BUILD-1", "--format", "json");
        Assert.Equal(0, again.Exit);
        var document = JsonNode.Parse(again.Out)!;
        Assert.Equal("datasource adopt", (string)document["command"]!);
        Assert.False((bool)document["data"]!["adopted"]!);
        Assert.Equal("build-1", (string)document["data"]!["client"]!);
        Assert.Equal(0, (await RunCliAsync("datasource", "adopt", "second", "build-1")).Exit);

        var audit = File.ReadAllLines(Path.Combine(dataDirectory, "management-audit.log")).Select(l => JsonNode.Parse(l)!).ToList();
        Assert.Contains(audit, a => (string)a["command"]! == "data-source adopt" && (string)a["target"]! == "by-hand" && (string)a["outcome"]! == "adopted by build-1"
            && (string)a["userId"]! == Environment.UserName && (string)a["consoleId"]! == "local");
    }

    [Fact]
    public async Task The_adopt_verb_refuses_a_client_that_is_not_a_server_client_an_unknown_one_a_policy_and_a_missing_source_and_writes_nothing()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "The Unix socket's folder is chosen here; the Windows pipe has one name per machine");
        await StartWithLocalChannelAsync();
        var admin = await PairAsync();   // the console, paired as a console
        await PairPublisherAsync(build1);
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/by-hand", ServerRole.Admin, Put(Plain("by-hand"), null));
        await CallAsync(admin, HttpMethod.Put, "/api/v1/data-sources/limited", ServerRole.Admin, Put(Conn.Yaml("limited", "duckdb", "Data Source=:memory:", proxy: ["allow_writes: true"]), null));
        var handMade = File.ReadAllText(ConnectionPath("by-hand"));
        var limited = File.ReadAllText(ConnectionPath("limited"));

        var notServer = await RunCliAsync("datasource", "adopt", "by-hand", console.Id);
        Assert.Equal(CliExit.Failure, notServer.Exit);
        Assert.Contains("not as a server client", notServer.Error);
        var unknown = await RunCliAsync("datasource", "adopt", "by-hand", "nobody");
        Assert.Equal(CliExit.Failure, unknown.Exit);
        Assert.Contains("server clients", unknown.Error);
        var policy = await RunCliAsync("datasource", "adopt", "limited", "build-1");
        Assert.Equal(CliExit.Failure, policy.Exit);
        Assert.Contains("extensions.proxy", policy.Error);
        var missing = await RunCliAsync("datasource", "adopt", "nothing-here", "build-1");
        Assert.Equal(CliExit.Failure, missing.Exit);
        Assert.Contains("404", missing.Error);
        Assert.Equal(CliExit.Usage, (await RunCliAsync("datasource", "adopt", "by-hand")).Exit);

        Assert.Equal(handMade, File.ReadAllText(ConnectionPath("by-hand")));
        Assert.Equal(limited, File.ReadAllText(ConnectionPath("limited")));
    }

    [Fact]
    public async Task The_adopt_verb_says_so_when_the_local_channel_is_off_or_nothing_listens()
    {
        await StartAsync();   // management on, local channel off
        await PairPublisherAsync(build1);
        var off = await RunCliAsync("datasource", "adopt", "erp", "build-1");
        Assert.Equal(CliExit.Failure, off.Exit);
        Assert.Contains("local channel is off", off.Error);

        File.AppendAllText(Path.Combine(ConfigDirectory, "server.yaml"), $"management: {{ localChannel: true, localChannelDirectory: '{Path.Combine(dataDirectory, "nobody-home")}' }}\n");
        var silent = await RunCliAsync("datasource", "adopt", "erp", "build-1");
        Assert.Equal(CliExit.Failure, silent.Exit);
        Assert.Contains("did not answer", silent.Error);
    }
}

public sealed partial class ManagementTests
{
    [Fact]
    public async Task The_clients_call_lists_the_server_clients_by_id_and_name_for_an_administrator_only()
    {
        await StartAsync();
        var admin = await PairAsync();   // a console: not offered as a taker
        await PairPublisherAsync(build1);

        var listed = await CallAsync(admin, HttpMethod.Get, "/api/v1/clients", ServerRole.Admin);
        Assert.Equal(HttpStatusCode.OK, listed.StatusCode);
        var text = await listed.Content.ReadAsStringAsync();
        var clients = JsonNode.Parse(text)!["clients"]!.AsArray();
        Assert.Equal("build-1", (string)clients.Single()!["id"]!);
        Assert.Equal("build-1", (string)clients.Single()!["name"]!);
        Assert.DoesNotContain("key", text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, (await CallAsync(admin, HttpMethod.Get, "/api/v1/clients", ServerRole.Viewer)).StatusCode);
    }
}
