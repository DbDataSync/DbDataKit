using DbDataKit.Secrets.Cli;
using Xunit;

namespace DbDataKit.Secrets.Cli.Tests;

internal sealed class FakeConsole : IConsole
{
    public List<string> Out { get; } = [];
    public List<string> Err { get; } = [];
    public bool IsInputRedirected { get; set; }
    public string Stdin { get; set; } = "";
    public Queue<string?> Prompts { get; } = new();
    public int PromptCount;
    public void WriteLine(string text) => Out.Add(text);
    public void WriteError(string text) => Err.Add(text);
    public string ReadStdin() => Stdin;
    public string? ReadSecret(string prompt) { PromptCount++; return Prompts.Count > 0 ? Prompts.Dequeue() : null; }
    public string All => string.Join("\n", Out.Concat(Err));
}

internal sealed class MemoryStore : ISecretStore, ISecretResolver
{
    public Dictionary<string, string> Data { get; } = [];
    public Task SetAsync(SecretName n, SecretValue v, CancellationToken ct = default) { Data[n.ToString()] = v.RevealString(); return Task.CompletedTask; }
    public Task DeleteAsync(SecretName n, CancellationToken ct = default) { Data.Remove(n.ToString()); return Task.CompletedTask; }
    public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var k in Data.Keys.Order()) if (k.StartsWith(prefix, StringComparison.Ordinal)) yield return SecretName.Parse(k);
    }
    public ValueTask<SecretValue> ResolveAsync(SecretRef r, CancellationToken ct = default) =>
        r.Kind == SecretRefKind.Secret && Data.TryGetValue(r.Target, out var v) ? ValueTask.FromResult(SecretValue.From(v))
        : r.Kind == SecretRefKind.Env && r.Target == "PRESENT" ? ValueTask.FromResult(SecretValue.From("envval"))
        : throw new SecretNotFoundException(r.ToAuditString());
}

public class CliTests
{
    private const string Name = "dbdata:build:connection:wh:write";

    private static (SecretCli cli, MemoryStore store, FakeConsole con) Make()
    {
        var s = new MemoryStore();
        return (new SecretCli(s, s), s, new FakeConsole());
    }

    [Fact]
    public void Set_FromStdin_StripsOneTrailingNewline_AndNeverPrintsValue()
    {
        var (cli, store, con) = Make();
        con.IsInputRedirected = true;
        con.Stdin = "p@ss\r\n";
        Assert.Equal(0, cli.Execute(["set", Name], con));
        Assert.Equal("p@ss", store.Data[Name]);
        Assert.DoesNotContain("p@ss", con.All);
        Assert.Equal(0, con.PromptCount);
    }

    [Fact]
    public void Set_FromPrompt_RequiresMatchingConfirmation()
    {
        var (cli, store, con) = Make();
        con.Prompts.Enqueue("abc"); con.Prompts.Enqueue("abd");
        Assert.Equal(1, cli.Execute(["set", Name], con));
        Assert.Empty(store.Data);

        var con2 = new FakeConsole();
        con2.Prompts.Enqueue("abc"); con2.Prompts.Enqueue("abc");
        Assert.Equal(0, cli.Execute(["set", Name], con2));
        Assert.Equal("abc", store.Data[Name]);
    }

    [Fact]
    public void Set_RejectsValueOnCommandLine_AndNeverStoresOrEchoesIt()
    {
        var (cli, store, con) = Make();
        Assert.Equal(2, cli.Execute(["set", Name, "hunter2"], con));
        Assert.Empty(store.Data);
        Assert.DoesNotContain("hunter2", con.All);
        Assert.Contains("never accepted on the command line", con.All);
    }

    [Fact]
    public void Set_NoTerminalNoStdin_FailsCleanly_AndEmptyValueRejected()
    {
        var (cli, store, con) = Make();
        Assert.Equal(1, cli.Execute(["set", Name], con));
        var c2 = new FakeConsole { IsInputRedirected = true, Stdin = "\n" };
        Assert.Equal(1, cli.Execute(["set", Name], c2));
        Assert.Empty(store.Data);
    }

    [Fact]
    public void Set_BadName_IsUsageError()
    {
        var (cli, _, con) = Make();
        con.IsInputRedirected = true;
        Assert.Equal(2, cli.Execute(["set", "not-a-name"], con));
        Assert.Equal(2, cli.Execute(["set"], con));
    }

    [Fact]
    public void Get_MasksByDefault_RevealsOnRequest_AndReportsMissing()
    {
        var (cli, store, con) = Make();
        store.Data[Name] = "topsecret";
        Assert.Equal(0, cli.Execute(["get", Name], con));
        Assert.DoesNotContain("topsecret", con.All);
        Assert.Contains("***", con.All);

        var c2 = new FakeConsole();
        Assert.Equal(0, cli.Execute(["get", Name, "--reveal"], c2));
        Assert.Equal(["topsecret"], c2.Out);

        var c3 = new FakeConsole();
        Assert.Equal(1, cli.Execute(["get", "dbdata:build:connection:none:read"], c3));
        Assert.Contains("not found", c3.All);
    }

    [Fact]
    public void List_PrintsNamesOnly_WithPrefixFilter()
    {
        var (cli, store, con) = Make();
        store.Data["dbdata:build:connection:a"] = "v1";
        store.Data["dbdata:proxy:datasource:b"] = "v2";
        Assert.Equal(0, cli.Execute(["list", "dbdata:build:"], con));
        Assert.Equal(["dbdata:build:connection:a"], con.Out);
        Assert.DoesNotContain("v1", con.All);

        var all = new FakeConsole();
        cli.Execute(["list"], all);
        Assert.Equal(2, all.Out.Count);
    }

    [Fact]
    public void Rm_Deletes()
    {
        var (cli, store, con) = Make();
        store.Data[Name] = "x";
        Assert.Equal(0, cli.Execute(["rm", Name], con));
        Assert.Empty(store.Data);
        Assert.Equal(2, cli.Execute(["rm", "bad"], new FakeConsole()));
    }

    [Fact]
    public void Test_ReportsOkOrNotFound_WithoutPrintingValue()
    {
        var (cli, _, con) = Make();
        Assert.Equal(0, cli.Execute(["test", "env:PRESENT"], con));
        Assert.DoesNotContain("envval", con.All);
        Assert.Contains("ok", con.All);
        var c2 = new FakeConsole();
        Assert.Equal(1, cli.Execute(["test", "env:ABSENT"], c2));
        Assert.Contains("not found", c2.All);
        Assert.Equal(2, cli.Execute(["test", "garbage"], new FakeConsole()));
    }

    [Fact]
    public void Dpapi_Blob_NotEchoedByTest()
    {
        var (cli, _, con) = Make();
        cli.Execute(["test", "dpapi:SECRETBLOB"], con);
        Assert.DoesNotContain("SECRETBLOB", con.All);
    }

    [Fact]
    public void UnknownCommand_AndHelp()
    {
        var (cli, _, con) = Make();
        Assert.Equal(2, cli.Execute(["frobnicate"], con));
        Assert.Equal(2, cli.Execute([], new FakeConsole()));
        Assert.Equal(0, cli.Execute(["--help"], new FakeConsole()));
    }

    [Fact]
    public void ProviderFailure_IsReportedAsError_NotCrash()
    {
        var chain = new SecretResolverChain([new FileSecretProvider(new FileSecretOptions { MaxBytes = 1 })]);
        var tmp = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tmp, "too big");
            var cli = new SecretCli(chain, new MemoryStore());
            var con = new FakeConsole();
            Assert.Equal(1, cli.Execute(["test", "file:" + tmp], con));
            Assert.Contains("InvalidDataException", con.All);
        }
        finally { File.Delete(tmp); }
    }
}
