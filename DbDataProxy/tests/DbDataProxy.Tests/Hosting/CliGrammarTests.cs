using Json.Schema;
using DbDataKit.Cli;
using DbDataProxy.Server.Hosting;

namespace DbDataProxy.Tests.Hosting;

/// <summary>The command line is <c>dbdataproxy &lt;noun&gt; &lt;verb&gt;</c> (plan 13, C04): no default command, and the old forms are gone, not aliased.</summary>
public sealed class CliGrammarTests
{
    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Cli.RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString().Replace("\r\n", "\n"), error.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task The_help_lists_the_nouns()
    {
        var result = await Run("help");

        Assert.Equal(0, result.Exit);
        Assert.Equal("""
            Usage: dbdataproxy <noun> <verb> [options]

            Commands:
              server   The long-running process
              service  Registration of 'server run' with Windows or systemd
              cert     The server's TLS certificate
              secret   The encrypted secret store, secret references and protected values
              config   The server's own configuration
              query    A client for smoke-testing a running proxy
              health   Is a running proxy answering
              ui       The local interfaces

            Run 'dbdataproxy <noun> help' for the verbs of one.
            Add --format json to any command for a JSON document instead of text.

            """.Replace("\r\n", "\n"), result.Out);
    }

    [Fact]
    public async Task With_no_arguments_it_prints_the_help_and_does_not_run_the_server()
    {
        var result = await Run();

        Assert.Equal(CliExit.Usage, result.Exit);
        Assert.Contains("Usage: dbdataproxy <noun> <verb>", result.Out);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("hash-password")]
    [InlineData("protect-secret")]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("--healthcheck")]
    [InlineData("--Proxy:Listen:Port=9000")]
    public async Task The_old_forms_are_not_commands(string old)
    {
        var result = await Run(old);

        Assert.Equal(CliExit.Usage, result.Exit);
        Assert.Contains("is not a command", result.Error);
    }

    [Fact]
    public async Task The_server_noun_has_run_and_the_secret_noun_has_hash_and_protect()
    {
        var router = Cli.Router();

        Assert.Equal(["run", "pair", "clients", "unpair"], router.Nouns.Single(n => n.Name == "server").Verbs.Select(v => v.Name));
        Assert.Contains("hash", router.Nouns.Single(n => n.Name == "secret").Verbs.Select(v => v.Name));
        Assert.Contains("protect", router.Nouns.Single(n => n.Name == "secret").Verbs.Select(v => v.Name));
        Assert.Equal(["run"], router.Nouns.Single(n => n.Name == "query").Verbs.Select(v => v.Name));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Secret_hash_refuses_the_password_as_an_argument()
    {
        var result = await Run("secret", "hash", "hunter2");

        Assert.Equal(CliExit.Usage, result.Exit);
        Assert.DoesNotContain("hunter2", result.Error + result.Out);
        Assert.Contains("never an argument", result.Error);
    }

    [Fact]
    public async Task Query_run_without_its_options_prints_the_usage_with_the_new_name()
    {
        var result = await Run("query", "run");

        Assert.Equal(CliExit.Usage, result.Exit);
        Assert.Contains("dbdataproxy query run --user", result.Error);
    }

    [Fact]
    public async Task Health_check_refuses_a_bad_address()
    {
        var result = await Run("health", "check", "--url", "ftp://x");

        Assert.Equal(CliExit.Usage, result.Exit);
    }

    // ---- the JSON form (plan 13, C05) ----

    private static string SchemaPath() => Path.Combine(Support.WorkerBuild.RepositoryRoot(), "schemas", "output.schema.json");

    private static string McpToolsPath() => Path.Combine(Support.WorkerBuild.RepositoryRoot(), "schemas", "mcp-tools.json");

    [Fact]
    public async Task The_checked_in_mcp_tool_list_is_current()
    {
        var server = new DbDataKit.Cli.McpServer(Cli.Router(), new DbDataKit.Cli.McpOptions(new HashSet<string>(["secret", "query", "server pair", "server unpair"])), Cli.Program, "0");
        var reply = (await server.HandleAsync(System.Text.Json.Nodes.JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"tools/list"}""")!)).Single();
        string actual = reply["result"]!.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }).Replace("\r\n", "\n") + "\n";
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1") File.WriteAllText(McpToolsPath(), actual);

        Assert.Equal(File.ReadAllText(McpToolsPath()).Replace("\r\n", "\n"), actual);
    }

    [Fact]
    public void The_checked_in_output_schema_is_current()
    {
        string actual = Cli.Router().OutputSchema().Replace("\r\n", "\n");
        if (Environment.GetEnvironmentVariable("UPDATE_GOLDEN") == "1") File.WriteAllText(SchemaPath(), actual);

        Assert.Equal(File.ReadAllText(SchemaPath()).Replace("\r\n", "\n"), actual);
    }

    [Fact]
    public void Every_command_the_help_lists_has_a_schema_for_its_data()
    {
        var router = Cli.Router();
        using var schema = System.Text.Json.JsonDocument.Parse(router.OutputSchema());
        var defs = schema.RootElement.GetProperty("$defs").EnumerateObject().Select(p => p.Name).ToHashSet();

        Assert.NotEmpty(router.Specs());
        Assert.All(router.Specs(), spec => Assert.Contains("data_" + spec.Id, defs));
    }

    [Theory]
    [InlineData("health", "check", "--url", "ftp://x")]
    [InlineData("secret", "hash", "hunter2")]
    [InlineData("bogus")]
    [InlineData("query", "run")]
    public async Task A_command_that_fails_still_writes_one_document_that_satisfies_the_schema(params string[] args)
    {
        var originalError = Console.Error;
        using var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            var result = await Run([.. args, "--format", "json"]);

            using var document = System.Text.Json.JsonDocument.Parse(result.Out);
            var schema = Json.Schema.JsonSchema.FromText(Cli.Router().OutputSchema());
            Assert.NotEqual(0, result.Exit);
            Assert.Equal(result.Exit, document.RootElement.GetProperty("exit_code").GetInt32());
            Assert.True(schema.Evaluate(document.RootElement, new Json.Schema.EvaluationOptions { OutputFormat = Json.Schema.OutputFormat.List }).IsValid, result.Out);
            Assert.DoesNotContain("hunter2", result.Out);
        }
        finally { Console.SetError(originalError); }
    }
}

