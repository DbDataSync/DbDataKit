using DbDataSync.Api.Configuration;
using DbDataSync.Api.Services;

namespace DbDataSync.Api.Tests;

/// <summary>
/// How the API starts a worker or a library validation: as <c>dbdatasync internal worker ...</c>, the same executable again, when the
/// running program is <c>DbDataSync.Cli</c>; as <c>dotnet exec</c> of a configured file otherwise (plan 13, slice F14).
/// </summary>
public sealed class ChildCommandTests
{
    [Fact]
    public void RunUnderTheLauncherOrAPublishedFile_TheProcessItselfIsTheCommand()
    {
        var self = ChildCommand.Self("/opt/dbdatasync/dbdatasync", "DbDataSync.Cli", "/opt/dbdatasync/versions/a/DbDataSync.Cli.dll");

        Assert.Equal("/opt/dbdatasync/dbdatasync", self!.Value.FileName);
        Assert.Empty(self.Value.LeadingArguments);
    }

    [Theory]
    [InlineData("/usr/share/dotnet/dotnet")]
    [InlineData(@"C:\Program Files\dotnet\dotnet.exe")]
    public void RunAsDotnetOfTheCliFile_TheDotnetHostIsGivenTheFileAgain(string dotnet)
    {
        var self = ChildCommand.Self(dotnet, "DbDataSync.Cli", "/app/DbDataSync.Cli.dll");

        Assert.Equal(dotnet, self!.Value.FileName);
        Assert.Equal(["exec", "/app/DbDataSync.Cli.dll"], self.Value.LeadingArguments);
    }

    [Theory]
    [InlineData("testhost")]
    [InlineData("DbDataSync.Api")]
    [InlineData(null)]
    public void AnyOtherEntryAssembly_CannotBeStartedAgainWithTheCliArguments(string? entryName) =>
        Assert.Null(ChildCommand.Self("/usr/share/dotnet/dotnet", entryName, "/x/y.dll"));

    [Fact]
    public void AConfiguredFile_IsRunWithDotnetExecWhateverTheEntryAssemblyIs()
    {
        var options = new ApiOptions
        {
            RepoRoot = "/tmp/repo",
            StateDbPath = "/tmp/repo/state.db",
            TaskRunnerDllPath = "/x/DbDataSync.TaskRunner.dll",
            CliDllPath = "/x/DbDataSync.Cli.dll",
        };

        var worker = ChildCommand.ForWorker(options);
        Assert.Equal("dotnet", worker.FileName);
        Assert.Equal(["exec", "/x/DbDataSync.TaskRunner.dll"], worker.LeadingArguments);
        var cli = ChildCommand.ForCli(options);
        Assert.Equal("dotnet", cli.FileName);
        Assert.Equal(["exec", "/x/DbDataSync.Cli.dll"], cli.LeadingArguments);
    }

    [Fact]
    public void NothingConfiguredAndNotTheCli_FallsBackToTheFilesBesideThisAssembly()
    {
        var options = new ApiOptions { RepoRoot = "/tmp/repo", StateDbPath = "/tmp/repo/state.db" };

        var (fileName, leading) = ChildCommand.ForWorker(options);

        Assert.Equal("dotnet", fileName);
        Assert.Equal("exec", leading[0]);
        Assert.EndsWith("DbDataSync.TaskRunner.dll", leading[1]);
    }
}
