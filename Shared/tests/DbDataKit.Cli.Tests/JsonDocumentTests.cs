using System.Text.Json;
using DbDataKit.Cli;
using Json.Schema;

namespace DbDataKit.Cli.Tests;

/// <summary>The <c>--format json</c> document and its schema (plan 13, C05).</summary>
public sealed class JsonDocumentTests
{
    private static NounRouter Router() => new("dbdatademo",
    [
        ServerNoun.Create((_, _) => Task.FromResult(0)),
        HealthNoun.Create(new Probe(), "http://localhost:5080/api/health"),
        ConfigNoun.Create(new Config()),
    ], version: "9.8.7");

    private sealed class Probe : IHealthProbe
    {
        public Task<HealthResult> CheckAsync(Uri url, CancellationToken cancellationToken) => Task.FromResult(new HealthResult(url.Host == "ok", url.Host == "ok" ? "HTTP 200" : "nothing answered"));
    }

    private sealed class Config : IConfigSurface
    {
        public IReadOnlyList<ConfigEntry> Show() => [new("listen.port", "5080", "server.yaml"), new("state.password", "hunter2", "secret store", IsSecret: true)];
        public ConfigEntry? Get(string key) => Show().FirstOrDefault(e => e.Key == key);
        public ConfigSetResult Set(string key, string value) => ConfigSetResult.Success;
        public IReadOnlyList<string> Check() => [];
        public string Schema() => "{}";
    }

    private static async Task<(int Exit, JsonDocument Document, string Raw)> Run(params string[] args)
    {
        var output = new StringWriter();
        var exit = await Router().RunAsync(args, new CliContext(output, new StringWriter(), TextReader.Null));
        return (exit, JsonDocument.Parse(output.ToString()), output.ToString());
    }

    private static JsonSchema Schema() => JsonSchema.FromText(Router().OutputSchema());

    private static bool Valid(JsonDocument document) => Schema().Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;

    [Fact]
    public async Task The_document_names_the_command_the_version_and_the_exit_code()
    {
        var (exit, document, _) = await Run("health", "check", "--url", "http://ok/health", "--format", "json");

        var root = document.RootElement;
        Assert.Equal(0, exit);
        Assert.Equal("dbdatademo.output/1", root.GetProperty("schema").GetString());
        Assert.Equal("health check", root.GetProperty("command").GetString());
        Assert.Equal("9.8.7", root.GetProperty("tool_version").GetString());
        Assert.Equal(0, root.GetProperty("exit_code").GetInt32());
        Assert.True(root.GetProperty("ok").GetBoolean());
        Assert.True(root.GetProperty("data").GetProperty("healthy").GetBoolean());
        Assert.Equal(["healthy: HTTP 200"], root.GetProperty("messages").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task A_failing_command_still_writes_a_document_with_its_exit_code_and_its_error_lines()
    {
        var (exit, document, _) = await Run("health", "check", "--url", "http://down/health", "--format=json");

        Assert.Equal(1, exit);
        Assert.False(document.RootElement.GetProperty("ok").GetBoolean());
        Assert.Contains("unhealthy: nothing answered", document.RootElement.GetProperty("errors").EnumerateArray().Select(e => e.GetString()));
        Assert.False(document.RootElement.GetProperty("data").GetProperty("healthy").GetBoolean());
    }

    [Fact]
    public async Task Text_output_is_not_mixed_into_the_document()
    {
        var (_, _, raw) = await Run("health", "check", "--url", "http://ok/health", "--format", "json");

        Assert.StartsWith("{", raw.TrimStart());
        Assert.Single(raw.Split((char)10), l => l.StartsWith("{", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_usage_mistake_is_a_document_too_with_exit_code_2()
    {
        var (exit, document, _) = await Run("bogus", "--format", "json");

        Assert.Equal(2, exit);
        Assert.Equal("", document.RootElement.GetProperty("command").GetString());
        Assert.Contains("is not a command", document.RootElement.GetProperty("errors")[0].GetString());
    }

    [Theory]
    [InlineData("--format", "yaml")]
    [InlineData("--format")]
    public async Task Any_other_format_is_refused(params string[] format)
    {
        var errors = new StringWriter();
        var exit = await Router().RunAsync(["health", "check", .. format], new CliContext(new StringWriter(), errors, TextReader.Null));

        Assert.Equal(CliExit.Usage, exit);
        Assert.Contains("json", errors.ToString());
    }

    [Theory]
    [InlineData("server", "run")]
    [InlineData("health", "check", "--url", "http://ok/health")]
    [InlineData("health", "check", "--url", "http://down/health")]
    [InlineData("config", "show")]
    [InlineData("config", "get", "listen.port")]
    [InlineData("config", "get", "nope")]
    [InlineData("config", "check")]
    [InlineData("config", "set", "listen.port", "5090")]
    [InlineData("config", "schema")]
    [InlineData("config", "get")]
    [InlineData("bogus")]
    public async Task Every_document_satisfies_the_output_schema(params string[] args)
    {
        var (_, document, _) = await Run([.. args, "--format", "json"]);

        Assert.True(Valid(document), document.RootElement.ToString());
    }

    [Fact]
    public async Task The_schema_rejects_a_document_whose_data_has_a_property_the_verb_does_not_declare()
    {
        var (_, document, raw) = await Run("health", "check", "--url", "http://ok/health", "--format", "json");
        var altered = JsonDocument.Parse(raw.Replace("\"healthy\": true", "\"healthy\": true, \"surprise\": 1"));

        Assert.True(Valid(document));
        Assert.False(Valid(altered));
    }

    [Fact]
    public void The_schema_has_a_data_definition_per_verb_and_is_stable()
    {
        var router = Router();
        using var schema = JsonDocument.Parse(router.OutputSchema());

        var defs = schema.RootElement.GetProperty("$defs").EnumerateObject().Select(p => p.Name).ToHashSet();
        Assert.All(router.Specs(), s => Assert.Contains("data_" + s.Id, defs));
        Assert.DoesNotContain(defs, d => d.EndsWith("_partial", StringComparison.Ordinal) && !defs.Contains(d[..^"_partial".Length]));
        Assert.Equal(router.OutputSchema(), Router().OutputSchema());
    }

    [Fact]
    public void A_verb_without_a_data_schema_is_refused_when_the_router_is_made()
    {
        var noun = new Noun("x", "x", [new Verb("y", "y", EffectClass.OfflineOnly, "", (_, _) => Task.FromResult(0))]);

        var ex = Assert.Throws<ArgumentException>(() => new NounRouter("p", [noun]));

        Assert.Contains("'x y' has no data schema", ex.Message);
    }

    [Fact]
    public void A_data_schema_that_is_not_json_is_refused()
    {
        var noun = new Noun("x", "x", [new Verb("y", "y", EffectClass.OfflineOnly, "", (_, _) => Task.FromResult(0), "{ not json")]);

        Assert.Throws<ArgumentException>(() => new NounRouter("p", [noun]));
    }
}
