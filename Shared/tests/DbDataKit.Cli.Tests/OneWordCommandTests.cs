namespace DbDataKit.Cli.Tests;

/// <summary>A command of one word (<c>demo pair --host ...</c>): every argument after the noun goes to its verb, and the help, the JSON document, the output schema and the MCP tools treat it as one command.</summary>
public sealed class OneWordCommandTests
{
    private static NounRouter Router(List<string[]> heard) => new("demo",
    [
        ServerNoun.Create((_, _) => Task.FromResult(0)),
        Noun.Command("pair", new Verb("pair", "Join the console", EffectClass.RepoFilesOnly, "--host <address>",
            (args, context) => { heard.Add(args); context.Data["host"] = args.LastOrDefault(); return Task.FromResult(0); },
            DataSchemas.Object([("host", DataSchemas.String)], "host"))),
    ], "1");

    private static async Task<(int Exit, string Out)> Run(NounRouter router, params string[] args)
    {
        var output = new StringWriter();
        var exit = await router.RunAsync(args, new CliContext(output, new StringWriter(), TextReader.Null));
        return (exit, output.ToString());
    }

    [Fact]
    public async Task Every_argument_after_the_noun_goes_to_the_verb_even_none()
    {
        var heard = new List<string[]>();
        var router = Router(heard);
        Assert.Equal(0, (await Run(router, "pair", "--host", "https://console:7800")).Exit);
        Assert.Equal(0, (await Run(router, "pair")).Exit);
        Assert.Equal(["--host", "https://console:7800"], heard[0]);
        Assert.Empty(heard[1]);
    }

    [Fact]
    public async Task The_help_the_json_document_the_output_schema_and_the_tools_name_it_as_one_word()
    {
        var router = Router([]);
        var help = await Run(router, "pair", "help");
        Assert.Contains("  pair --host <address>", help.Out);
        Assert.Contains("\"command\": \"pair\"", (await Run(router, "pair", "--host", "x", "--format", "json")).Out);
        Assert.Contains(router.Specs(), s => s.Name == "pair");
        Assert.Contains("\"const\": \"pair\"", router.OutputSchema());
        Assert.DoesNotContain(McpServer.Offered(router, new McpOptions(new HashSet<string>(), AllowWrites: true)), t => t.Noun.Name == "pair");
    }
}
