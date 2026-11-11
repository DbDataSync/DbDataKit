namespace DbDataKit.Cli.Tests;

/// <summary>The router's precondition (plan 33 D14): a message from it refuses every verb, in text and in JSON, and leaves the help alone.</summary>
public sealed class RouterPreconditionTests
{
    private static NounRouter Router(Func<string?> precondition, Action ran) => new("demo",
    [
        ServerNoun.Create((_, _) => { ran(); return Task.FromResult(0); }),
    ], "1", precondition);

    private static async Task<(int Exit, string Out, string Err)> Run(NounRouter router, params string[] args)
    {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await router.RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task A_message_refuses_the_verb_before_it_runs_and_is_the_error()
    {
        var ran = false;
        var result = await Run(Router(() => "OLD_NAME is no longer read; use NEW_NAME.", () => ran = true), "server", "run");

        Assert.Equal(CliExit.Failure, result.Exit);
        Assert.False(ran);
        Assert.Contains("NEW_NAME", result.Err);
    }

    [Fact]
    public async Task The_json_document_carries_the_refusal_as_an_error()
    {
        var result = await Run(Router(() => "OLD_NAME is no longer read; use NEW_NAME.", () => { }), "server", "run", "--format", "json");

        Assert.Equal(CliExit.Failure, result.Exit);
        Assert.Contains("\"ok\": false", result.Out);
        Assert.Contains("NEW_NAME", result.Out);
    }

    [Fact]
    public async Task Help_is_not_refused_and_no_message_runs_the_verb()
    {
        var ran = false;
        var router = Router(() => "refused", () => ran = true);
        Assert.Equal(CliExit.Ok, (await Run(router, "help")).Exit);
        Assert.Equal(CliExit.Ok, (await Run(router, "server", "help")).Exit);
        Assert.False(ran);

        var open = Router(() => null, () => ran = true);
        Assert.Equal(CliExit.Ok, (await Run(open, "server", "run")).Exit);
        Assert.True(ran);
    }
}
