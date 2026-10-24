using DbDataKit.Cli;
using DbDataKit.Secrets.Cli;
using Xunit;

namespace DbDataKit.Secrets.Cli.Tests;

public class SecretNounTests
{
    private const string Name = "dbdata:build:connection:wh:write";

    private static (NounRouter Router, MemoryStore Store, FakeConsole Console) Make()
    {
        var store = new MemoryStore();
        var console = new FakeConsole();
        var cli = new SecretCli(store, store, store, () => [new SecretRef(SecretRefKind.Env, "PRESENT")], v => "dpapi:PROTECTED");
        return (new NounRouter("prod", [SecretNoun.Create(cli, _ => console)]), store, console);
    }

    private static CliContext Context() => new(new StringWriter(), new StringWriter(), TextReader.Null);

    [Fact]
    public void The_noun_has_the_shared_verbs() =>
        Assert.Equal(["set", "list", "delete", "check", "rotate-key", "export", "import", "protect"],
            Make().Router.Nouns.Single().Verbs.Select(v => v.Name));

    [Fact]
    public async Task Secret_set_through_the_noun_reads_the_value_from_standard_input_and_never_prints_it()
    {
        var (router, store, console) = Make();
        console.IsInputRedirected = true;
        console.Stdin = "p@ss\n";

        var exit = await router.RunAsync(["secret", "set", Name], Context());

        Assert.Equal(0, exit);
        Assert.Equal("p@ss", store.Data[Name]);
        Assert.DoesNotContain("p@ss", console.All);
    }

    [Fact]
    public async Task Secret_list_and_delete_go_through_the_noun()
    {
        var (router, store, console) = Make();
        store.Data[Name] = "x";

        Assert.Equal(0, await router.RunAsync(["secret", "list"], Context()));
        Assert.Contains(Name, console.All);
        Assert.Equal(0, await router.RunAsync(["secret", "delete", Name], Context()));
        Assert.Empty(store.Data);
    }

    [Fact]
    public async Task Secret_check_with_nothing_named_checks_the_references_in_the_configuration()
    {
        var (router, _, console) = Make();

        Assert.Equal(0, await router.RunAsync(["secret", "check"], Context()));
        Assert.Contains("env:PRESENT", console.All);
    }
}
