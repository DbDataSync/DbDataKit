using System.Text.Json;
using DbDataKit.Cli;
using Json.Schema;

namespace DbDataSync.Cli.Tests;

/// <summary>Sync's command line on the shared router: every verb has a schema, the cert verbs are the ones <see cref="CertCommand"/> has, and a command's JSON document satisfies the checked-in schema (plan 13, C07).</summary>
public sealed class SyncCliTests
{
    private static string SchemaPath()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DbDataSync.slnx")))
                return Path.Combine(dir.FullName, "schemas", "output.schema.json");
        throw new DirectoryNotFoundException("DbDataSync.slnx was not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void TheCertNounHasExactlyTheVerbsTheCertCommandRegisters() =>
        Assert.Equal(CertCommand.VerbNames.Order(StringComparer.Ordinal), SyncCli.Router().Nouns.Single(n => n.Name == "cert").Verbs.Select(v => v.Name).Order(StringComparer.Ordinal));

    [Fact]
    public void TheNounsAreTheSuiteGrammar() =>
        Assert.Equal(["server", "service", "cert", "secret", "library", "driver", "config", "health"], SyncCli.Router().Nouns.Select(n => n.Name));

    [Fact]
    public void TheCommandsOutsideTheRouterAreTheOnesThatStayUnchanged() =>
        Assert.Equal(["internal", "invite", "launcher", "tool", "update", "version"], SyncCli.Legacy.Order(StringComparer.Ordinal));

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
    [InlineData("health", "check", "--url", "http://127.0.0.1:1")]
    [InlineData("bogus")]
    [InlineData("server", "start")]
    [InlineData("cert", "not-a-cert-command")]
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

        var exit = await SyncCli.Router().RunAsync(["secret", "list", "dbdata:sync:test:json-capture", "--repo", Directory.CreateTempSubdirectory("dbdatasync-json-").FullName, "--format", "json"],
            new CliContext(output, new StringWriter(), TextReader.Null));

        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal(0, exit);
        Assert.Contains("dbdata:sync:test:json-capture  not set", document.RootElement.GetProperty("messages")[0].GetString());
        Assert.Same(originalOut, Console.Out);   // the scope puts the console back
    }
}
