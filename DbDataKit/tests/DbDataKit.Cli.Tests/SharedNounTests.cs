using System.Text.Json;
using DbDataKit.Cli;

namespace DbDataKit.Cli.Tests;

public sealed class SharedNounTests
{
    private sealed class Io
    {
        public StringWriter Out { get; } = new();
        public StringWriter Error { get; } = new();
        public CliContext Context(CancellationToken cancellation = default) => new(Out, Error, TextReader.Null, cancellation);
    }

    private sealed class FakeProbe(Func<Uri, CancellationToken, Task<HealthResult>> answer) : IHealthProbe
    {
        public Uri? Asked { get; private set; }
        public Task<HealthResult> CheckAsync(Uri url, CancellationToken cancellationToken) { Asked = url; return answer(url, cancellationToken); }
    }

    private sealed class FakeConfig : IConfigSurface
    {
        public Dictionary<string, ConfigEntry> Entries { get; } = new()
        {
            ["listen.port"] = new("listen.port", "5080", "server.yaml"),
            ["state.password"] = new("state.password", "hunter2", "secret store", IsSecret: true),
        };
        public List<string> Problems { get; } = [];
        public IReadOnlyList<ConfigEntry> Show() => [.. Entries.Values];
        public ConfigEntry? Get(string key) => Entries.GetValueOrDefault(key);
        public ConfigSetResult Set(string key, string value)
        {
            if (!Entries.ContainsKey(key)) return new(false, $"There is no setting '{key}'.");
            if (!int.TryParse(value, out _)) return new(false, $"'{key}' takes a whole number.");
            Entries[key] = Entries[key] with { Value = value };
            return ConfigSetResult.Success;
        }
        public IReadOnlyList<string> Check() => Problems;
        public string Schema() => "{\"type\":\"object\"}";
    }

    private static NounRouter Router(IHealthProbe? probe = null, IConfigSurface? config = null, Func<string[], CliContext, Task<int>>? run = null) => new("dbdatademo",
    [
        ServerNoun.Create(run ?? ((_, _) => Task.FromResult(0))),
        ConfigNoun.Create(config ?? new FakeConfig()),
        HealthNoun.Create(probe ?? new FakeProbe((_, _) => Task.FromResult(new HealthResult(true, "HTTP 200"))), "http://localhost:5080/api/health"),
    ]);

    // ---- the router and the help ----

    [Fact]
    public void The_help_lists_the_nouns_and_is_stable()
    {
        Assert.Equal("""
            Usage: dbdatademo <noun> <verb> [options]

            Commands:
              server  The long-running process
              config  The server's own configuration
              health  Is a running server answering

            Run 'dbdatademo <noun> help' for the verbs of one.
            Add --format json to any command for a JSON document instead of text.

            """.Replace("\r\n", "\n"), Router().Help());
    }

    [Fact]
    public void The_help_of_a_noun_lists_its_verbs_with_their_effect_and_is_stable()
    {
        var router = Router();

        Assert.Equal("""
            dbdatademo health: Is a running server answering

            Verbs:
              health check [--url <url>] [--timeout <seconds>]
                  Ask the server's health address; exit 0 when it answers, 1 when it does not [Offline only]

            """.Replace("\r\n", "\n"), router.NounHelp(router.Nouns.Single(n => n.Name == "health")));
    }

    [Theory]
    [InlineData(new string[0], CliExit.Usage)]
    [InlineData(new[] { "help" }, CliExit.Ok)]
    [InlineData(new[] { "config" }, CliExit.Usage)]
    [InlineData(new[] { "config", "help" }, CliExit.Ok)]
    [InlineData(new[] { "bogus" }, CliExit.Usage)]
    [InlineData(new[] { "config", "bogus" }, CliExit.Usage)]
    public async Task Help_and_mistakes_have_the_documented_exit_codes(string[] args, int expected)
    {
        var io = new Io();

        Assert.Equal(expected, await Router().RunAsync(args, io.Context()));
    }

    [Fact]
    public async Task An_unknown_command_names_itself_and_the_way_to_the_list()
    {
        var io = new Io();

        await Router().RunAsync(["bogus"], io.Context());

        Assert.Equal("dbdatademo: 'bogus' is not a command. Run 'dbdatademo help' for the list.\n", io.Error.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public void A_noun_with_more_verbs_keeps_its_own_and_lists_the_specs()
    {
        var noun = ServerNoun.Create((_, _) => Task.FromResult(0)).With(new Verb("pair", "Pair a console", EffectClass.RepoFilesOnly, "", (_, _) => Task.FromResult(0)));

        Assert.Equal(["server run", "server pair"], noun.Specs().Select(s => s.Name));
        Assert.All(noun.Specs(), s => Assert.True(s.Implemented));
    }

    // ---- server run ----

    [Fact]
    public async Task Server_run_passes_the_arguments_after_it_and_returns_the_servers_exit_code()
    {
        string[]? seen = null;
        var router = Router(run: (args, _) => { seen = args; return Task.FromResult(7); });

        var exit = await router.RunAsync(["server", "run", "--url", "http://0.0.0.0:80"], new Io().Context());

        Assert.Equal(7, exit);
        Assert.Equal(["--url", "http://0.0.0.0:80"], seen!);
    }

    [Fact]
    public async Task Server_run_stops_when_its_token_is_cancelled()
    {
        using var cancellation = new CancellationTokenSource();
        var router = Router(run: async (_, context) => { try { await Task.Delay(Timeout.Infinite, context.Cancellation); } catch (OperationCanceledException) { } return 0; });

        var running = router.RunAsync(["server", "run"], new Io().Context(cancellation.Token));
        cancellation.Cancel();

        Assert.Equal(0, await running.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    // ---- health check ----

    [Fact]
    public async Task Health_check_is_healthy_when_the_probe_says_so_and_asks_the_default_address()
    {
        var probe = new FakeProbe((_, _) => Task.FromResult(new HealthResult(true, "HTTP 200")));
        var io = new Io();

        var exit = await Router(probe).RunAsync(["health", "check"], io.Context());

        Assert.Equal(CliExit.Ok, exit);
        Assert.Equal("http://localhost:5080/api/health", probe.Asked!.ToString());
        Assert.Equal("healthy: HTTP 200\n", io.Out.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task Health_check_fails_when_the_probe_says_no_and_asks_the_address_it_was_given()
    {
        var probe = new FakeProbe((_, _) => Task.FromResult(new HealthResult(false, "HTTP 503")));
        var io = new Io();

        var exit = await Router(probe).RunAsync(["health", "check", "--url", "https://srv:8443/health"], io.Context());

        Assert.Equal(CliExit.Failure, exit);
        Assert.Equal("https://srv:8443/health", probe.Asked!.ToString());
        Assert.Contains("unhealthy: HTTP 503", io.Error.ToString());
    }

    [Fact]
    public async Task Health_check_gives_up_after_its_timeout_without_waiting_for_the_probe()
    {
        var probe = new FakeProbe(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return new HealthResult(true, ""); });
        var io = new Io();

        var exit = await Router(probe).RunAsync(["health", "check", "--timeout", "1"], io.Context()).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(CliExit.Failure, exit);
        Assert.Contains("no answer in 1 s", io.Error.ToString());
    }

    [Theory]
    [InlineData("--url", "not a url")]
    [InlineData("--url", "ftp://host/")]
    [InlineData("--timeout", "0")]
    [InlineData("--timeout", "soon")]
    [InlineData("--bogus", "1")]
    public async Task Health_check_refuses_arguments_it_cannot_use_before_asking_anything(string option, string value)
    {
        var probe = new FakeProbe((_, _) => Task.FromResult(new HealthResult(true, "")));

        var exit = await Router(probe).RunAsync(["health", "check", option, value], new Io().Context());

        Assert.Equal(CliExit.Usage, exit);
        Assert.Null(probe.Asked);
    }

    [Fact]
    public async Task The_http_probe_reports_a_status_and_never_the_body()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable) { Content = new StringContent("secret host prod-db-01") });
        var result = await new HttpHealthProbe(new HttpClient(handler)).CheckAsync(new Uri("http://x/health"), CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.Equal("HTTP 503", result.Detail);
    }

    [Fact]
    public async Task The_http_probe_says_nothing_answered_for_a_connection_failure()
    {
        var handler = new StubHandler(_ => throw new HttpRequestException("connection refused to prod-db-01:5432"));
        var result = await new HttpHealthProbe(new HttpClient(handler)).CheckAsync(new Uri("http://x/health"), CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.Equal("nothing answered", result.Detail);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    // ---- config ----

    [Fact]
    public async Task Config_show_lists_every_setting_with_its_origin_and_masks_a_secret()
    {
        var io = new Io();

        await Router().RunAsync(["config", "show"], io.Context());

        Assert.Equal("listen.port = 5080  (server.yaml)\nstate.password = ***  (secret store)\n", io.Out.ToString().Replace("\r\n", "\n"));
        Assert.DoesNotContain("hunter2", io.Out.ToString());
    }

    [Fact]
    public async Task Config_show_as_json_masks_a_secret_too()
    {
        var io = new Io();

        await Router().RunAsync(["config", "show", "--format", "json"], io.Context());

        using var document = JsonDocument.Parse(io.Out.ToString());
        var settings = document.RootElement.GetProperty("data").GetProperty("settings");
        Assert.Equal(2, settings.GetArrayLength());
        Assert.Equal("***", settings[1].GetProperty("value").GetString());
        Assert.True(settings[1].GetProperty("is_secret").GetBoolean());
        Assert.DoesNotContain("hunter2", io.Out.ToString());
    }

    [Fact]
    public async Task Config_get_prints_one_value_and_an_unknown_key_is_a_failure()
    {
        var io = new Io();
        var found = await Router().RunAsync(["config", "get", "listen.port"], io.Context());
        var missing = await Router().RunAsync(["config", "get", "nope"], io.Context());

        Assert.Equal(CliExit.Ok, found);
        Assert.Equal("5080\n", io.Out.ToString().Replace("\r\n", "\n"));
        Assert.Equal(CliExit.Failure, missing);
        Assert.Contains("There is no setting 'nope'", io.Error.ToString());
    }

    [Fact]
    public async Task Config_set_changes_a_setting_and_a_refusal_explains_itself_without_the_value()
    {
        var config = new FakeConfig();
        var io = new Io();

        var ok = await Router(config: config).RunAsync(["config", "set", "listen.port", "5090"], io.Context());
        var bad = await Router(config: config).RunAsync(["config", "set", "listen.port", "hunter2"], io.Context());

        Assert.Equal(CliExit.Ok, ok);
        Assert.Equal("5090", config.Entries["listen.port"].Value);
        Assert.Equal(CliExit.Failure, bad);
        Assert.DoesNotContain("hunter2", io.Error.ToString());
        Assert.Equal("5090", config.Entries["listen.port"].Value);
    }

    [Fact]
    public async Task Config_set_never_takes_a_secret_and_points_to_secret_set()
    {
        var config = new FakeConfig();
        var io = new Io();

        var exit = await Router(config: config).RunAsync(["config", "set", "state.password", "new-secret"], io.Context());

        Assert.Equal(CliExit.Usage, exit);
        Assert.Contains("secret set", io.Error.ToString());
        Assert.DoesNotContain("new-secret", io.Error.ToString());
        Assert.Equal("hunter2", config.Entries["state.password"].Value);
    }

    [Fact]
    public async Task Config_check_is_ok_when_nothing_is_wrong_and_lists_the_problems_when_something_is()
    {
        var config = new FakeConfig();
        var okIo = new Io();
        var badIo = new Io();

        var ok = await Router(config: config).RunAsync(["config", "check"], okIo.Context());
        config.Problems.Add("listen.port: 70000 is not a port");
        var bad = await Router(config: config).RunAsync(["config", "check"], badIo.Context());

        Assert.Equal(CliExit.Ok, ok);
        Assert.Equal("ok\n", okIo.Out.ToString().Replace("\r\n", "\n"));
        Assert.Equal(CliExit.Failure, bad);
        Assert.Contains("70000 is not a port", badIo.Error.ToString());
    }

    [Fact]
    public async Task Config_check_as_json_reports_ok_and_the_problems()
    {
        var config = new FakeConfig { Problems = { "a" } };
        var io = new Io();

        var exit = await Router(config: config).RunAsync(["config", "check", "--format", "json"], io.Context());

        using var document = JsonDocument.Parse(io.Out.ToString());
        Assert.Equal(CliExit.Failure, exit);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(1, document.RootElement.GetProperty("exit_code").GetInt32());
        Assert.Equal("a", document.RootElement.GetProperty("data").GetProperty("problems")[0].GetString());
        Assert.Equal("a", document.RootElement.GetProperty("errors")[0].GetString());
    }

    [Fact]
    public async Task Config_schema_prints_the_schema()
    {
        var io = new Io();

        Assert.Equal(CliExit.Ok, await Router().RunAsync(["config", "schema"], io.Context()));
        Assert.Equal("{\"type\":\"object\"}\n", io.Out.ToString().Replace("\r\n", "\n"));
    }

    [Theory]
    [InlineData("config", "get")]
    [InlineData("config", "get", "a", "b")]
    [InlineData("config", "set", "a")]
    [InlineData("config", "check", "--bogus")]
    public async Task Config_verbs_refuse_the_wrong_arguments_with_the_usage_exit_code(params string[] args)
    {
        var io = new Io();

        Assert.Equal(CliExit.Usage, await Router().RunAsync(args, io.Context()));
        Assert.StartsWith("Usage: config ", io.Error.ToString());
    }
}
