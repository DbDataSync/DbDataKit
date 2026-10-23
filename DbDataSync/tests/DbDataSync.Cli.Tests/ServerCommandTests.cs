namespace DbDataSync.Cli.Tests;

/// <summary><c>dbdatasync server run|setup</c> and the renamed forms (plan 13, C06).</summary>
public sealed class ServerCommandTests
{
    private static async Task<(int ExitCode, string Output)> Run(params string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try { return (await ServerCommand.RunAsync(args), output.ToString()); }
        finally { Console.SetOut(originalOut); Console.SetError(originalErr); }
    }

    [Fact]
    public async Task WithNoVerb_ItPrintsBothVerbsAndFails()
    {
        var (exitCode, output) = await Run();

        Assert.Equal(2, exitCode);
        Assert.Contains("dbdatasync server run", output);
        Assert.Contains("dbdatasync server setup", output);
    }

    [Fact]
    public async Task AnUnknownVerb_IsRefusedByName()
    {
        var (exitCode, output) = await Run("start");

        Assert.Equal(2, exitCode);
        Assert.Contains("'start' is not a verb", output);
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
        var originalErr = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            Assert.Equal(2, await HealthCommand.RunAsync([]));
            Assert.Contains("dbdatasync health check", error.ToString());
        }
        finally { Console.SetError(originalErr); }
    }
}
