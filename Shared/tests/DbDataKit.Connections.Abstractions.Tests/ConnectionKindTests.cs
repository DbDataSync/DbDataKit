using DbDataKit.Config.Yaml;
using DbDataKit.Dialect;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>
/// Plan 27a IX01: a <c>kind</c> in <c>connection/2</c> (open, additive): <c>database</c> (the default), <c>http</c> and <c>model-provider</c>. A database file reads exactly as before, a file of an outside kind loads with its secret references,
/// an unknown kind is refused naming the file, a program that keeps databases only refuses an outside file naming it, and a canary secret written as itself is refused without being echoed.
/// </summary>
public sealed class ConnectionKindTests
{
    private const string Http = "schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com/v1\nsign_in:\n  method: api_key\n  name: X-Api-Key\n  key_secret: secret:dbdata:console:connection:crm:key\n";

    private static ConnectionLoadResult Any(string yaml, string file = "connections/x.yml") => ConnectionDefinitionLoader.Load(yaml, file, outsideKinds: true);

    private static string Errors(ConnectionLoadResult r) => string.Join("\n", r.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error));

    [Fact]
    public void A_database_file_reads_as_before_and_its_kind_is_database()
    {
        foreach (var text in new[] { "schema: connection/1\nname: w\nengine: postgres\naddress: { host: h, database: d }\n", "schema: connection/2\nname: w\nengine: postgres\naddress: { host: h, database: d }\n", "schema: connection/2\nkind: database\nname: w\nengine: postgres\naddress: { host: h }\n" })
        {
            var strict = ConnectionDefinitionLoader.Load(text);
            Assert.True(strict.Ok, Errors(strict));
            Assert.Equal("database", strict.Definition!.KindName);
            Assert.False(strict.Definition.IsOutside);
            Assert.True(Any(text).Ok);
        }
    }

    [Fact]
    public void A_missing_engine_of_a_database_is_still_refused_and_an_outside_key_in_a_database_is_too()
    {
        var noEngine = ConnectionDefinitionLoader.Load("schema: connection/1\nname: w\naddress: { host: h }\n", "c.yml");
        Assert.False(noEngine.Ok);
        Assert.Contains("engine", Errors(noEngine), StringComparison.Ordinal);
        var mixed = ConnectionDefinitionLoader.Load("schema: connection/2\nname: w\nengine: postgres\naddress: { host: h }\nendpoint: https://x.example.com\n");
        Assert.Contains(mixed.Diagnostics, d => d.Code == ConnectionDefinitionLoader.BadOutside && d.Severity == ConfigSeverity.Error);
    }

    [Fact]
    public void A_file_of_kind_http_loads_with_its_secret_reference_and_the_value_is_nowhere_in_it()
    {
        var r = Any(Http);
        Assert.True(r.Ok, Errors(r));
        var d = r.Definition!;
        Assert.Equal("http", d.KindName);
        Assert.True(d.IsOutside);
        Assert.Equal("https://api.example.com/v1", d.Endpoint);
        Assert.Equal("api_key", d.SignIn!.Method);
        Assert.Equal("secret:dbdata:console:connection:crm:key", d.SignIn.KeySecret);
        Assert.Equal("header", d.SignIn.In);
    }

    [Fact]
    public void A_program_that_keeps_databases_refuses_an_outside_file_naming_it()
    {
        var r = ConnectionDefinitionLoader.Load(Http, "connections/crm.yaml");
        Assert.False(r.Ok);
        var e = Assert.Single(r.Diagnostics, d => d.Severity == ConfigSeverity.Error);
        Assert.Equal(ConnectionDefinitionLoader.KindNotKept, e.Code);
        Assert.Contains("connections/crm.yaml", e.Message, StringComparison.Ordinal);
        Assert.Equal("connections/crm.yaml", e.Path);
        Assert.Contains("`http`", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_kind_is_refused_naming_the_file_and_the_kinds()
    {
        var r = Any("schema: connection/2\nkind: ftp\nname: x\nendpoint: https://a.example.com\n", "connections/odd.yml");
        Assert.False(r.Ok);
        var e = Assert.Single(r.Diagnostics, d => d.Severity == ConfigSeverity.Error);
        Assert.Equal(ConnectionDefinitionLoader.BadKind, e.Code);
        Assert.Contains("connections/odd.yml", e.Message, StringComparison.Ordinal);
        Assert.Contains("`ftp`", e.Message, StringComparison.Ordinal);
        Assert.Contains("`http`", e.Fix, StringComparison.Ordinal);
        Assert.Equal(2, e.Line);
    }

    [Fact]
    public void A_format_1_file_cannot_say_a_kind()
    {
        var r = Any("schema: connection/1\nkind: http\nname: x\nendpoint: https://a.example.com\n", "connections/x.yml");
        var e = Assert.Single(r.Diagnostics, d => d.Severity == ConfigSeverity.Error);
        Assert.Equal(ConnectionDefinitionLoader.SectionNotInThisFormat, e.Code);
        Assert.Contains("schema: connection/2", e.Fix, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("endpoint: https://user:pass@api.example.com\n")]
    [InlineData("endpoint: https://api.example.com/v1?api_key=abc\n")]
    [InlineData("endpoint: ftp://api.example.com\n")]
    public void An_address_with_a_name_a_password_or_a_query_is_refused(string endpoint)
    {
        var r = Any($"schema: connection/2\nkind: http\nname: x\n{endpoint}");
        Assert.False(r.Ok);
        Assert.Contains("no user name, password or query", Errors(r), StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_written_as_itself_is_refused_without_being_echoed()
    {
        const string canary = "sk-CANARY-1f9e8d7c6b5a";
        var r = Any($"schema: connection/2\nkind: http\nname: x\nendpoint: https://a.example.com\nsign_in: {{ method: api_key, key_secret: {canary} }}\n");
        Assert.False(r.Ok);
        Assert.Contains(r.Diagnostics, d => d.Code == ConnectionDefinitionLoader.BadSecretRef);
        // the message says what a reference is; it does not repeat what was written
        Assert.DoesNotContain(canary, string.Join("\n", r.Diagnostics.Select(d => d.ToString() + d.Message + d.Fix)), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("method: api_key", "needs `key_secret`")]
    [InlineData("method: basic, user: u", "needs `password_secret`")]
    [InlineData("method: oauth2_client_credentials, client_id: c", "needs `token_url`")]
    [InlineData("method: client_certificate", "needs one of")]
    [InlineData("method: none, key_secret: env:K", "does not belong")]
    public void Each_sign_in_method_asks_for_what_it_needs(string signIn, string message)
    {
        var r = Any($"schema: connection/2\nkind: http\nname: x\nendpoint: https://a.example.com\nsign_in: {{ {signIn} }}\n");
        Assert.False(r.Ok);
        Assert.Contains(message, Errors(r), StringComparison.Ordinal);
    }

    [Fact]
    public void An_unknown_sign_in_method_and_a_database_key_in_an_outside_file_are_refused()
    {
        Assert.False(Any("schema: connection/2\nkind: http\nname: x\nendpoint: https://a.example.com\nsign_in: { method: kerberos }\n").Ok);
        var r = Any("schema: connection/2\nkind: http\nname: x\nendpoint: https://a.example.com\nengine: postgres\naddress: { host: h }\n");
        Assert.False(r.Ok);
        Assert.Contains("a key of a database connection", Errors(r), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("test_path: /v1/ping\n", true)]
    [InlineData("test_path: health\n", true)]
    [InlineData("test_path: /v1/ping?key=abc\n", false)]
    [InlineData("test_path: https://other.example.com/x\n", false)]
    [InlineData("test_path: /../admin\n", false)]
    public void The_test_path_is_an_address_beneath_the_endpoint_with_no_query(string line, bool ok)
    {
        var r = Any(Http + line);
        Assert.Equal(ok, r.Ok);
        if (!ok) Assert.Contains("test_path", Errors(r), StringComparison.Ordinal);
        if (ok) Assert.Equal(line.Split(": ")[1].TrimEnd('\n'), r.Definition!.TestPath);
    }

    [Fact]
    public void A_test_path_does_not_belong_to_a_database_or_a_model_provider()
    {
        Assert.False(ConnectionDefinitionLoader.Load("schema: connection/2\nname: w\nengine: postgres\naddress: { host: h }\ntest_path: /x\n").Ok);
        Assert.False(Any("schema: connection/2\nkind: model-provider\nname: p\ntest_path: /x\nmodel_provider: { provider: ollama, models: [ { id: m } ] }\n").Ok);
    }

    private const string Provider = "schema: connection/2\nkind: model-provider\nname: anthropic-main\nsign_in: { method: api_key, key_secret: env:ANTHROPIC_API_KEY }\nmodel_provider:\n  provider: anthropic\n  models:\n    - { id: claude-sonnet, window: 200000, max_output: 8192, price: { input: 3, output: 15 } }\n";

    [Fact]
    public void A_model_provider_has_its_models_prices_and_the_default_address_of_its_provider()
    {
        var r = Any(Provider);
        Assert.True(r.Ok, Errors(r));
        var m = r.Definition!.ModelProvider!;
        Assert.Equal("anthropic", m.Provider);
        Assert.Equal("chat", m.Purpose);
        var model = Assert.Single(m.Models);
        Assert.Equal(("claude-sonnet", 200000, 8192), (model.Id, model.Window, model.MaxOutput));
        Assert.Equal((3m, 15m), (model.Price!.Input, model.Price.Output));
        Assert.Null(r.Definition.Endpoint);   // the address is the provider's own; a reader asks ModelProviderSpec.DefaultEndpoint
    }

    [Theory]
    [InlineData("kind: model-provider\nname: p\nmodel_provider: { provider: openai-compatible, models: [ { id: m } ] }\nendpoint: http://localhost:8080/v1\n", "locality")]
    [InlineData("kind: model-provider\nname: p\nmodel_provider: { provider: openai-compatible, locality: local, models: [ { id: m } ] }\n", "no default address")]
    [InlineData("kind: model-provider\nname: p\nsign_in: { method: api_key, key_secret: env:K }\nmodel_provider: { provider: anthropic, models: [] }\n", "at least one model")]
    [InlineData("kind: model-provider\nname: p\nmodel_provider: { provider: anthropic, models: [ { id: m } ] }\n", "needs `sign_in")]
    [InlineData("kind: model-provider\nname: p\nsign_in: { method: api_key, key_secret: env:K }\nmodel_provider: { provider: anthropic, models: [ { id: m }, { id: m } ] }\n", "more than once")]
    [InlineData("kind: model-provider\nname: p\nsign_in: { method: basic, user: u, password_secret: env:K }\nmodel_provider: { provider: anthropic, models: [ { id: m } ] }\n", "api_key")]
    [InlineData("kind: model-provider\nname: p\nsign_in: { method: api_key, key_secret: env:K }\n", "needs a `model_provider` section")]
    public void A_model_provider_that_is_incomplete_says_what_is_missing(string body, string message)
    {
        var r = Any("schema: connection/2\n" + body);
        Assert.False(r.Ok);
        Assert.Contains(message, Errors(r), StringComparison.Ordinal);
    }

    [Fact]
    public void A_local_provider_needs_no_key()
    {
        var r = Any("schema: connection/2\nkind: model-provider\nname: local\nmodel_provider: { provider: ollama, models: [ { id: llama3 } ] }\n");
        Assert.True(r.Ok, Errors(r));
    }

    [Fact]
    public void The_json_schema_names_the_new_keys_and_a_save_writes_the_current_format()
    {
        var json = ConnectionDefinitionLoader.JsonSchema();
        foreach (var key in new[] { "\"kind\"", "\"endpoint\"", "key_secret", "oauth2_client_credentials", "model_provider" })
            Assert.Contains(key, json, StringComparison.Ordinal);
        Assert.Contains("schema: connection/2", ConnectionDefinitionLoader.WithCurrentVersion(Http), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ConnectionFailureKind.Network, true)]
    [InlineData(ConnectionFailureKind.Timeout, true)]
    [InlineData(ConnectionFailureKind.RateLimited, true)]
    [InlineData(ConnectionFailureKind.Unavailable, true)]
    [InlineData(ConnectionFailureKind.Login, false)]
    [InlineData(ConnectionFailureKind.Tls, false)]
    [InlineData(ConnectionFailureKind.Permission, false)]
    [InlineData(ConnectionFailureKind.Syntax, false)]
    [InlineData(ConnectionFailureKind.Unsupported, false)]
    [InlineData(ConnectionFailureKind.NotFound, false)]
    [InlineData(ConnectionFailureKind.Other, false)]
    public void A_failure_says_whether_a_retry_can_help(ConnectionFailureKind kind, bool temporary)
    {
        var failure = new ConnectionFailure(kind, "x", new EngineId("http"));
        Assert.Equal(temporary, failure.IsTemporary);
        Assert.False(string.IsNullOrWhiteSpace(ConnectionFailure.TextFor(kind)));
    }

    [Fact]
    public void Every_kind_of_failure_has_its_own_sentence_except_other()
    {
        var sentences = Enum.GetValues<ConnectionFailureKind>().Select(ConnectionFailure.TextFor).ToList();
        Assert.Equal(sentences.Count, sentences.Distinct().Count());
    }
}
