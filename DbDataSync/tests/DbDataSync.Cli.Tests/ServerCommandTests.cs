using DbDataKit.Cli;

namespace DbDataSync.Cli.Tests;

/// <summary>The renamed forms (plan 13, C06) and the noun-verb grammar of Sync's command line (C07).</summary>
public sealed class ServerCommandTests
{
    private static async Task<(int ExitCode, string Output, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await SyncCli.Router().RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task WithNoVerb_ItPrintsBothVerbsAndFails()
    {
        var (exitCode, output, _) = await Run("server");

        Assert.Equal(2, exitCode);
        Assert.Contains("server run", output);
        Assert.Contains("server setup", output);
    }

    [Fact]
    public async Task AnUnknownVerb_IsRefusedByName()
    {
        var (exitCode, _, error) = await Run("server", "start");

        Assert.Equal(2, exitCode);
        Assert.Contains("'start' is not a verb", error);
    }

    [Theory]
    [InlineData("serve", "server run")]
    [InlineData("setup", "server setup")]
    [InlineData("SERVE", "server run")]
    public void TheOldTopLevelForms_NameTheirReplacement(string old, string replacement) =>
        Assert.Equal(replacement, RetiredCommands.ReplacementFor(old));

    [Theory]
    [InlineData("server")]
    [InlineData("config")]
    [InlineData("health")]
    public void ACurrentCommand_IsNotRetired(string command) => Assert.Null(RetiredCommands.ReplacementFor(command));

    [Fact]
    public async Task HealthWithoutTheCheckVerb_SaysHowToAsk()
    {
        var (exitCode, _, error) = await Run("health");

        Assert.Equal(2, exitCode);
        Assert.DoesNotContain("Unknown", error);
    }
}
