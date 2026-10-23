using DbDataKit.Cli;
using DbDataProxy.Server.Hosting;

namespace DbDataProxy.Tests.Hosting;

/// <summary>The command line is <c>dbdataproxy &lt;noun&gt; &lt;verb&gt;</c> (plan 13, C04): no default command, and the old forms are gone, not aliased.</summary>
public sealed class CliGrammarTests
{
    private static async Task<(int Exit, string Out, string Error)> Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var exit = await Cli.RunAsync(args, new CliContext(output, error, TextReader.Null));
        return (exit, output.ToString().Replace("\r\n", "\n"), error.ToString().Replace("\r\n", "\n"));
    }

    [Fact]
    public async Task The_help_lists_the_nouns()
    {
        var result = await Run("help");

        Assert.Equal(0, result.Exit);
        Assert.Equal("""
            Usage: dbdataproxy <noun> <verb> [options]

            Commands:
              server   The long-running process
              service  Registration of 'server run' with Windows or systemd
              cert     The server's TLS certificate
              secret   The encrypted secret store, secret references and protected values
              config   The server's own configuration
              query    A client for smoke-testing a running proxy
              health   Is a running proxy answering

            Run 'dbdataproxy <noun> help' for the verbs of one.

            """.Replace("\r\n", "\n"), result.Out);
    }

    [Fact]
    public async Task With_no_arguments_it_prints_the_help_and_does_not_run_the_server()
    {
        var result = await Run();

        Assert.Equal(CliExit.Usage, result.Exit);
        Assert.Contains("Usage: dbdataproxy <noun> <verb>", result.Out);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("hash-password")]
    [InlineData("protect-secret")]
    [InlineData("install")]
    [InlineData("uninstall")]
    [InlineData("--healthcheck")]
    [InlineData("--Proxy:Listen:Port=9000")]
    public async Task The_old_forms_are_not_commands(string old)
    {
        var result = await Run(old);

        Assert.Equal(CliExit.Usage, result.Exit);
        Assert.Contains("is not a command", result.Error);
    }

    [Fact]
    public async Task The_server_noun_has_run_and_the_secret_noun_has_hash_and_protect()
    {
        var router = Cli.Router();

        Assert.Equal(["run"], router.Nouns.Single(n => n.Name == "server").Verbs.Select(v => v.Name));
        Assert.Contains("hash", router.Nouns.Single(n => n.Name == "secret").Verbs.Select(v => v.Name));
        Assert.Contains("protect", router.Nouns.Single(n => n.Name == "secret").Verbs.Select(v => v.Name));
        Assert.Equal(["run"], router.Nouns.Single(n => n.Name == "query").Verbs.Select(v => v.Name));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Secret_hash_refuses_the_password_as_an_argument()
    {
        var result = await Run("secret", "hash", "hunter2");

        Assert.Equal(CliExit.Usage, result.Exit);
        Assert.DoesNotContain("hunter2", result.Error + result.Out);
        Assert.Contains("never an argument", result.Error);
    }

    [Fact]
    public async Task Query_run_without_its_options_prints_the_usage_with_the_new_name()
    {
        var originalError = Console.Error;
        using var error = new StringWriter();
        Console.SetError(error);
        try
        {
            var result = await Run("query", "run");
            Assert.Equal(CliExit.Usage, result.Exit);
            Assert.Contains("dbdataproxy query run --user", error.ToString());
        }
        finally { Console.SetError(originalError); }
    }

    [Fact]
    public async Task Health_check_refuses_a_bad_address()
    {
        var result = await Run("health", "check", "--url", "ftp://x");

        Assert.Equal(CliExit.Usage, result.Exit);
    }
}
