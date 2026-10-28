using DbDataKit.Config.Git;
using DbDataSync.Core.Config;
using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli.Tests;

/// <summary><c>dbdatasync script list|test</c>: a fixture script of the repository runs against generated sample input, the way the editor's test button runs it.</summary>
[Collection("ConsoleCapture")]
public sealed class ScriptCommandTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-script-cmd-tests-").FullName;

    public void Dispose() => GitTempDirectory.DeleteRecursively(_root);

    private void Write(string name, string kind, string code)
    {
        LibGit2Sharp.Repository.Init(_root);
        new ConfigRepository(Path.Combine(_root, "config"), new GitCommitService(_root), SecretStore.Open(_root)).SaveScript(
            new ScriptDefinition { Manifest = new ScriptConfig { Name = name, Kind = kind }, Code = code }, new GitAuthor("test", "test@example.com"));
    }

    private static async Task<(int Exit, string Out, string Err)> RunAsync(params string[] args)
    {
        var (oldOut, oldErr) = (Console.Out, Console.Error);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try { return (await ScriptCommand.RunAsync(args), output.ToString(), error.ToString()); }
        finally { Console.SetOut(oldOut); Console.SetError(oldErr); }
    }

    private const string Upper = """
        using DbDataSync.Scripting.Abstractions;

        public sealed class Upper : ISqlColumnExpression
        {
            public string? RenderSql(SqlColumnExpressionContext c) => c.SourceColumn == "Name" ? $"UPPER({c.ColumnReference})" : null;
        }
        new Upper()
        """;

    [Fact]
    public async Task List_NamesEachScriptAndTheSlotItFills()
    {
        Write("upper", "sqlColumnExpression", Upper);

        var (exit, output, _) = await RunAsync("list", "--repo", _root);

        Assert.Equal(0, exit);
        Assert.Contains("upper\tsqlColumnExpression", output);
    }

    [Fact]
    public async Task Test_RunsTheScriptAgainstGeneratedInput_AndPrintsWhatItDid()
    {
        Write("upper", "sqlColumnExpression", Upper);

        var (exit, output, error) = await RunAsync("test", "upper", "--repo", _root);

        Assert.True(exit == 0, error);
        Assert.Contains("generated sample", output);
        Assert.Contains("UPPER(", output);
    }

    [Fact]
    public async Task Test_AScriptThatDoesNotCompile_FailsWithTheCompilerMessage()
    {
        Write("broken", "sqlColumnExpression", "this is not C#;\nnew X()");

        var (exit, _, error) = await RunAsync("test", "broken", "--repo", _root);

        Assert.Equal(1, exit);
        Assert.NotEmpty(error);
    }

    [Fact]
    public async Task Test_AScriptThatDoesNotExist_SaysSo()
    {
        var (exit, _, error) = await RunAsync("test", "nope", "--repo", _root);

        Assert.Equal(1, exit);
        Assert.Contains("'nope' does not exist", error);
    }
}
