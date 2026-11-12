using DbDataKit.Config.Yaml;

namespace DbDataKit.Connections.Abstractions.Tests;

/// <summary>
/// Owner decision 2026-11-11: a program says which group of connection kinds it loads (database, delivery, chat model, embedding model, HTTP API, any), and a file of another group is refused naming the connection, its kind and the group accepted.
/// </summary>
public sealed class ConnectionKindGroupTests
{
    private const string Database = "schema: connection/1\nname: warehouse\nengine: duckdb\naddress: { database: x.db }\n";
    private const string Smtp = "schema: connection/2\nkind: smtp\nname: mail\nsmtp:\n  host: smtp.example.com\n  sender: \"DbData <noreply@example.com>\"\n";
    private const string Slack = "schema: connection/2\nkind: http\nname: slack-ops\nendpoint: https://hooks.slack.com/services/{token}\nendpoint_secrets:\n  token: secret:dbdatakit:server:connection:slack-ops:token\nwebhook: { format: slack }\n";
    private const string Api = "schema: connection/2\nkind: http\nname: crm\nendpoint: https://api.example.com/v1\n";
    private const string Chat = "schema: connection/2\nkind: model-provider\nname: claude\nsign_in: { method: api_key, key_secret: env:K }\nmodel_provider:\n  provider: anthropic\n  models:\n    - { id: claude-sonnet }\n";
    private const string Embed = "schema: connection/2\nkind: model-provider\nname: embed\nmodel_provider:\n  provider: ollama\n  purpose: embeddings\n  models:\n    - { id: nomic-embed-text }\n";

    private static readonly (string Name, string Yaml)[] All = [("database", Database), ("smtp", Smtp), ("webhook", Slack), ("api", Api), ("chat", Chat), ("embed", Embed)];

    private static string Words(ConnectionLoadResult r) => string.Join("\n", r.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error));

    public static TheoryData<string, string[]> Accepted => new()
    {
        { "LoadDatabaseConnection", ["database"] },
        { "LoadDeliveryConnection", ["smtp", "webhook"] },
        { "LoadModelConnection", ["chat"] },
        { "LoadEmbeddingConnection", ["embed"] },
        { "LoadHttpConnection", ["webhook", "api"] },
        { "LoadConnectionOfAnyKind", ["database", "smtp", "webhook", "api", "chat", "embed"] },
    };

    private static ConnectionLoadResult Call(string helper, string yaml, string file) => helper switch
    {
        "LoadDatabaseConnection" => ConnectionDefinitionLoader.LoadDatabaseConnection(yaml, file),
        "LoadDeliveryConnection" => ConnectionDefinitionLoader.LoadDeliveryConnection(yaml, file),
        "LoadModelConnection" => ConnectionDefinitionLoader.LoadModelConnection(yaml, file),
        "LoadEmbeddingConnection" => ConnectionDefinitionLoader.LoadEmbeddingConnection(yaml, file),
        "LoadHttpConnection" => ConnectionDefinitionLoader.LoadHttpConnection(yaml, file),
        _ => ConnectionDefinitionLoader.LoadConnectionOfAnyKind(yaml, file),
    };

    [Theory]
    [MemberData(nameof(Accepted))]
    public void Each_helper_loads_its_group_and_refuses_every_other_naming_the_connection_its_kind_and_the_group(string helper, string[] accepted)
    {
        foreach (var (name, yaml) in All)
        {
            var file = $"connections/{name}.yml";
            var r = Call(helper, yaml, file);
            if (accepted.Contains(name)) { Assert.True(r.Ok, $"{helper} should load {name}: {Words(r)}"); continue; }
            Assert.False(r.Ok, $"{helper} should refuse {name}");
            var d = Assert.Single(r.Diagnostics, x => x.Code == ConnectionDefinitionLoader.KindNotKept);
            Assert.Equal(file, d.Path);
            Assert.Contains($"'{r.Definition?.Name ?? yaml.Split("name: ")[1].Split('\n')[0]}'", d.Message, StringComparison.Ordinal);
            Assert.Contains("this program accepts the", d.Message, StringComparison.Ordinal);
            Assert.Contains("group only", d.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_refusal_names_the_kind_and_the_accepted_group_in_words()
    {
        var smtp = Assert.Single(ConnectionDefinitionLoader.LoadDatabaseConnection(Smtp, "connections/mail.yml").Diagnostics, x => x.Code == ConnectionDefinitionLoader.KindNotKept);
        Assert.Contains("'mail' (file 'connections/mail.yml') is of kind `smtp`, and this program accepts the database group only", smtp.Message, StringComparison.Ordinal);
        var chat = Assert.Single(ConnectionDefinitionLoader.LoadEmbeddingConnection(Chat, "connections/claude.yml").Diagnostics, x => x.Code == ConnectionDefinitionLoader.KindNotKept);
        Assert.Contains("`model-provider` (purpose chat)", chat.Message, StringComparison.Ordinal);
        Assert.Contains("embedding model provider", chat.Message, StringComparison.Ordinal);
        var db = Assert.Single(ConnectionDefinitionLoader.LoadHttpConnection(Database, "connections/warehouse.yml").Diagnostics, x => x.Code == ConnectionDefinitionLoader.KindNotKept);
        Assert.Contains("`database`", db.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_group_is_a_set_and_there_is_no_default_load()
    {
        var both = ConnectionDefinitionLoader.Load(Smtp, "connections/mail.yml", ConnectionKindGroup.Database | ConnectionKindGroup.Delivery);
        Assert.True(both.Ok, Words(both));
        Assert.False(ConnectionDefinitionLoader.Load(Smtp, "connections/mail.yml", ConnectionKindGroup.None).Ok);
        Assert.Equal(typeof(ConnectionKindGroup), typeof(ConnectionDefinitionLoader).GetMethod("Load", [typeof(string), typeof(string), typeof(ConnectionKindGroup), typeof(bool)])!.GetParameters()[2].ParameterType);
        Assert.False(typeof(ConnectionDefinitionLoader).GetMethod("Load", [typeof(string), typeof(string), typeof(ConnectionKindGroup), typeof(bool)])!.GetParameters()[2].HasDefaultValue);
    }
}
