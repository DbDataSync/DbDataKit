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

/// <summary>A noun's removed verb answers its sentence through the router (exit 2) and is not a verb: the help does not list it and the noun's other verbs still run.</summary>
public sealed class RemovedVerbTests
{
    private static readonly Noun Service = new("service", "The service",
    [
        new Verb("check", "Check it", EffectClass.OfflineOnly, "", (_, context) => { context.Out.WriteLine("checked"); return Task.FromResult(0); }, DataSchemas.None),
    ], new Dictionary<string, string> { ["doctor"] = "run 'service check' instead." });

    private static async Task<(int Exit, string Out, string Error)> Run(Noun noun, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await new NounRouter("tool", [noun]).RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_removed_verb_answers_its_sentence_with_exit_2()
    {
        var doctor = await Run(Service, "service", "doctor", "--name", "x");

        Assert.Equal(CliExit.Usage, doctor.Exit);
        Assert.Equal("'tool service doctor' is gone: run 'service check' instead.", doctor.Error.Trim());
        Assert.Empty(doctor.Out);
    }

    [Fact]
    public async Task The_other_verbs_run_and_the_help_and_a_noun_made_with_more_verbs_keep_the_removed_one_out()
    {
        var more = Service.With(new Verb("unit", "Print the unit", EffectClass.OfflineOnly, "", (_, _) => Task.FromResult(0), DataSchemas.None));

        Assert.Equal(0, (await Run(more, "service", "check")).Exit);
        Assert.Equal(CliExit.Usage, (await Run(more, "service", "doctor")).Exit);
        Assert.Contains("is gone", (await Run(more, "service", "doctor")).Error);
        Assert.DoesNotContain("doctor", new NounRouter("tool", [more]).NounHelp(more));
        Assert.Contains("is not a verb", (await Run(more, "service", "cure")).Error);
    }
}
