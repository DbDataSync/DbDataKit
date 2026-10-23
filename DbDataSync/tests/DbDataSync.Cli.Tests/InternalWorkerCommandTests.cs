namespace DbDataSync.Cli.Tests;

/// <summary><c>dbdatasync internal worker</c> is how the API starts a replication's worker; given nothing to work on it says what is missing and fails.</summary>
public sealed class InternalWorkerCommandTests
{
    [Fact]
    public async Task Worker_WithNoArguments_FailsNamingTheUsage()
    {
        var originalErr = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            var exitCode = await InternalCommand.RunAsync(["worker"]);

            Assert.Equal((int)DbDataSync.TaskRunner.ExitCode.ConfigError, exitCode);
            Assert.Contains("--repo-root", error.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
