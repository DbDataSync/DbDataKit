namespace DbDataSync.Cli.Tests;

/// <summary><c>dbdatasync internal worker</c> was how the API started a replication's worker; since plan 28c E22 the Runner runs every replication, and a unit or script that still starts it is told so.</summary>
public sealed class InternalWorkerCommandTests
{
    [Fact]
    public async Task Worker_is_refused_naming_the_runner()
    {
        var originalErr = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            var exitCode = await InternalCommand.RunAsync(["worker", "--repo-root", "/tmp/x"]);

            Assert.Equal(2, exitCode);
            Assert.Contains("the Runner runs every replication", error.ToString());
            Assert.Contains("plan 28c E22", error.ToString());
        }
        finally
        {
            Console.SetError(originalErr);
        }
    }
}
