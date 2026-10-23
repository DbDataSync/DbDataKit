namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync config check|cert|secret|library|driver</c> — phase 115's dispatcher, forwarding
/// "everything after my own verb" to the existing command classes unchanged. These are thin routing
/// tests: full behavior for each nested group is already covered by its own suite
/// (<see cref="SecretCommandTests"/> here; <c>DbDataSync.Libraries.Tests</c> /
/// <c>DbDataSync.Drivers.*.Tests</c> elsewhere) — <c>cert</c> has no CLI test at all, Windows-only and
/// untestable in this environment, so there is nothing to route-test beyond the platform gate.
/// </summary>
public sealed class ConfigCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-config-tests-").FullName;
    private readonly string _secretRef = "dbdata:sync:test:routing";

    public void Dispose()
    {
        GitTempDirectory.DeleteRecursively(_root);
    }

    [Fact]
    public async Task NoSubcommand_PrintsUsageAndFails()
    {
        var (exitCode, output) = await Run([]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Usage", output);
    }

    [Fact]
    public async Task UnknownSubcommand_PrintsUsageAndFails()
    {
        var (exitCode, output) = await Run(["bogus"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("Unknown config subcommand 'bogus'", output);
    }

    [Fact]
    public async Task Check_ForwardsToTheReadinessEngine()
    {
        ServeCommand.Prepare(_root);

        var (exitCode, output) = await Run(["check", "--repo", _root]);

        // BindingCheck fails (nothing is listening), but Repo/State store/Auth all report Ok — proof
        // this reached the real check list, not just an empty/placeholder response.
        Assert.Equal(1, exitCode);
        Assert.Contains("Repo:", output);
        Assert.Contains("State store:", output);
    }

    /// <summary>Certificates, secrets, libraries and drivers are commands of their own since plan 13 (C06); the old <c>config</c> forms say what they are now.</summary>
    [Theory]
    [InlineData("secret")]
    [InlineData("library")]
    [InlineData("driver")]
    [InlineData("cert")]
    public async Task TheFormsThatMovedOutOfConfig_SayWhatTheyAreNow(string moved)
    {
        var (exitCode, output) = await Run([moved, "list"]);

        Assert.Equal(2, exitCode);
        Assert.Contains($"'dbdatasync config {moved}' is now 'dbdatasync {moved}'", output);
    }

    private static async Task<(int ExitCode, string Output)> Run(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = await ConfigCommand.RunAsync(args);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
