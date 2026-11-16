using System.Text.Json;
using DbDataKit.Cli;
using Json.Schema;

namespace DbDataSync.Cli.Tests;

/// <summary>Sync's command line on the shared router: every verb has a schema, and a command's JSON document satisfies the checked-in schema (plan 13, C07). The Sync server's nouns are gone (plan 33 SC23, <c>RemovedServerTests</c>).</summary>
public sealed class SyncCliTests
{
    private static string SchemaPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DbDataSync.slnf")))
                return Path.Combine(dir.FullName, "schemas", "output.schema.json");
        throw new DirectoryNotFoundException("DbDataSync.slnf was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheNounsAreTheSuiteGrammar() =>
        Assert.Equal(["script", "provisioning", "library", "driver", "package", "lineage", "ui"], SyncCli.Router().Nouns.Select(n => n.Name));

    [Fact]
    public void TheCommandsOutsideTheRouterAreTheOnesThatStayUnchanged() =>
        Assert.Equal(["tool", "version"], SyncCli.Legacy.Order(StringComparer.Ordinal));

    private static string McpToolsPath() => Path.Combine(Path.GetDirectoryName(SchemaPath())!, "mcp-tools.json");

    [Fact]
    public async Task TheCheckedInMcpToolListIsCurrent()
    {
        var server = new McpServer(SyncCli.Router(), new McpOptions(new HashSet<string>(["library validate"])), SyncCli.Program, "0");
        var reply = (await server.HandleAsync(System.Text.Json.Nodes.JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)).Single();
        string actual = reply["result"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1") File.WriteAllText(McpToolsPath(), actual);

        Assert.Equal(File.ReadAllText(McpToolsPath()).Replace("\r\n", "\n"), actual);
    }

    [Fact]
    public void TheCheckedInOutputSchemaIsCurrent()
    {
        var actual = SyncCli.Router().OutputSchema().Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SchemaPath())!);
            File.WriteAllText(SchemaPath(), actual);
        }

        Assert.Equal(File.ReadAllText(SchemaPath()).Replace("\r\n", "\n"), actual);
    }

    [Theory]
    [InlineData("bogus")]
    [InlineData("script", "not-a-script-command")]
    [InlineData("package", "publish")]
    public async Task AFailingCommandStillWritesOneDocumentThatSatisfiesTheSchema(params string[] args)
    {
        var output = new StringWriter();
        var exit = await SyncCli.Router("1.2.3").RunAsync([.. args, "--format", "json"], new CliContext(output, new StringWriter(), TextReader.Null));

        using var document = JsonDocument.Parse(output.ToString());
        var schema = JsonSchema.FromText(SyncCli.Router().OutputSchema());
        Assert.NotEqual(0, exit);
        Assert.Equal(exit, document.RootElement.GetProperty("exit_code").GetInt32());
        Assert.True(schema.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid, output.ToString());
    }

    [Fact]
    public async Task TheJsonFormOfAVerbCapturesWhatTheTextFormPrints()
    {
        var output = new StringWriter();
        var originalOut = Console.Out;

        var exit = await SyncCli.Router().RunAsync(["library", "list", "--format", "json"], new CliContext(output, new StringWriter(), TextReader.Null));

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exit);
        Assert.NotEqual(0, document.RootElement.GetProperty("messages").GetArrayLength());
        Assert.Same(originalOut, Console.Out);   // the scope puts the console back
    }
}
