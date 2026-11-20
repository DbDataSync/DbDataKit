namespace DbDataKit.Cli.Tests;

/// <summary>Plan 33 SC23: a command that went with its server answers with the sentence naming where its feature went (exit 2), never "not a command"; a noun and verb key wins over the noun's.</summary>
public sealed class RemovedCommandsTests
{
    private static readonly RemovedCommands Removed = new("tool", new Dictionary<string, string>
    {
        ["server"] = "the server was deleted: use the Workspace.",
        ["model put"] = "authored in the Workspace now.",
    });

    [Fact]
    public async Task A_removed_noun_answers_with_its_sentence_and_exit_2()
    {
        var error = new StringWriter();
        Assert.Equal(CliExit.Usage, await Removed.TryAnswerAsync(["server", "run", "--repo", "x"], error));
        Assert.Equal("'tool server' is gone: the server was deleted: use the Workspace.", error.ToString().Trim());
    }

    [Fact]
    public void A_removed_verb_wins_over_its_noun_and_the_rest_of_the_noun_stays()
    {
        Assert.Equal("'tool model put' is gone: authored in the Workspace now.", Removed.AnswerFor(["model", "put", "--file", "m.yaml"]));
        Assert.Null(Removed.AnswerFor(["model", "publish", "m"]));
        Assert.Null(Removed.AnswerFor(["--repo", "x"]));
        Assert.Null(Removed.AnswerFor([]));
    }

    [Fact]
    public async Task A_command_that_stays_is_not_answered()
    {
        var error = new StringWriter();
        Assert.Null(await Removed.TryAnswerAsync(["state", "show"], error));
        Assert.Empty(error.ToString());
    }
}
