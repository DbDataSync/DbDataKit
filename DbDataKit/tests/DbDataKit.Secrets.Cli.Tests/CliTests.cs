using DbDataKit.Secrets.Cli;
using DbDataKit.Secrets.Store;
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

internal sealed class MemoryStore : ISecretStore, ISecretResolver, ISecretAdmin
{
    public Dictionary<string, string> Data { get; } = [];
    public Task SetAsync(SecretName n, SecretValue v, CancellationToken ct = default) { Data[n.ToString()] = v.RevealString(); return Task.CompletedTask; }
    public Task DeleteAsync(SecretName n, CancellationToken ct = default) { Data.Remove(n.ToString()); return Task.CompletedTask; }
    public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.CompletedTask;
        foreach (var k in Data.Keys.Order()) if (k.StartsWith(prefix, StringComparison.Ordinal)) yield return SecretName.Parse(k);
    }
    public List<string> Admin { get; } = [];
    public Task<IReadOnlyList<SecretInfo>> ListInfoAsync(string prefix = "", CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<SecretInfo>>(Data.Keys.Order().Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Select(k => new SecretInfo(SecretName.Parse(k), new DateTimeOffset(2026, 10, 21, 12, 30, 0, TimeSpan.Zero), "k1")).ToList());
    public Task<KeyRotationResult> RotateKeyAsync(Action<string>? checkpoint = null, CancellationToken ct = default) { Admin.Add("rotate"); return Task.FromResult(new KeyRotationResult("k2", ["k1"], Data.Count, false)); }
    public Task<int> ExportAsync(string path, SecretValue passphrase, string prefix = "", CancellationToken ct = default) { Admin.Add($"export {path} prefix={prefix} pass={passphrase.RevealString()}"); return Task.FromResult(Data.Count); }
    public Task<SecretImportResult> ImportAsync(string path, SecretValue passphrase, bool overwrite = false, CancellationToken ct = default) { Admin.Add($"import {path} overwrite={overwrite} pass={passphrase.RevealString()}"); return Task.FromResult(new SecretImportResult(2, 1, overwrite ? 1 : 0)); }
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
        return (new SecretCli(s, s, s, () => [new SecretRef(SecretRefKind.Env, "PRESENT"), new SecretRef(SecretRefKind.Secret, "dbdata:build:connection:wh:write")], v => "dpapi:PROTECTED"), s, new FakeConsole());
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
    public void There_is_no_get_and_no_reveal_flag()
    {
        var (cli, store, con) = Make();
        store.Data[Name] = "topsecret";
        Assert.Equal(2, cli.Execute(["get", Name, "--reveal"], con));
        Assert.DoesNotContain("topsecret", con.All);
        Assert.DoesNotContain("--reveal", SecretCli.Usage);
        Assert.DoesNotContain("get <", SecretCli.Usage);
    }

    [Fact]
    public void List_PrintsNamesOnly_WithPrefixFilter()
    {
        var (cli, store, con) = Make();
        store.Data["dbdata:build:connection:a"] = "v1";
        store.Data["dbdata:proxy:datasource:b"] = "v2";
        Assert.Equal(0, cli.Execute(["list", "dbdata:build:"], con));
        Assert.Single(con.Out);
        Assert.StartsWith("dbdata:build:connection:a  written 2026-10-21 12:30 UTC  key k1", con.Out[0]);
        Assert.DoesNotContain("v1", con.All);

        var all = new FakeConsole();
        cli.Execute(["list"], all);
        Assert.Equal(2, all.Out.Count);
    }

    [Fact]
    public void Delete_Deletes_And_Rm_Still_Works()
    {
        var (cli, store, con) = Make();
        store.Data[Name] = "x";
        Assert.Equal(0, cli.Execute(["delete", Name], con));
        Assert.Empty(store.Data);
        Assert.Equal(2, cli.Execute(["delete", "bad"], new FakeConsole()));
        store.Data[Name] = "x";
        Assert.Equal(0, cli.Execute(["rm", Name], new FakeConsole()));
        Assert.Empty(store.Data);
    }

    [Fact]
    public void Check_ReportsOkOrNotFound_WithoutPrintingValue()
    {
        var (cli, _, con) = Make();
        Assert.Equal(0, cli.Execute(["check", "env:PRESENT"], con));
        Assert.DoesNotContain("envval", con.All);
        Assert.Contains("ok", con.All);
        var c2 = new FakeConsole();
        Assert.Equal(1, cli.Execute(["check", "env:ABSENT"], c2));
        Assert.Contains("not found", c2.All);
        Assert.Equal(2, cli.Execute(["check", "garbage"], new FakeConsole()));
    }

    [Fact]
    public void Check_WithNoArguments_ResolvesEveryReferenceOfTheConfiguration_AndFailsWhenOneIsMissing()
    {
        var (cli, store, con) = Make();
        Assert.Equal(1, cli.Execute(["check"], con));                        // the secret is not stored yet
        Assert.Contains("ok: env:PRESENT", con.All);
        Assert.Contains($"not found: secret:{Name}", con.All);
        Assert.Contains("1 of 2 reference(s) do not resolve", con.All);

        store.Data[Name] = "stored";
        var again = new FakeConsole();
        Assert.Equal(0, cli.Execute(["check"], again));
        Assert.DoesNotContain("stored", again.All);
    }

    [Fact]
    public void Check_WithoutAConfigurationHook_AsksForReferences()
    {
        var s = new MemoryStore();
        var cli = new SecretCli(s, s);
        Assert.Equal(2, cli.Execute(["check"], new FakeConsole()));
    }

    [Fact]
    public void Dpapi_Blob_NotEchoedByCheck()
    {
        var (cli, _, con) = Make();
        cli.Execute(["check", "dpapi:SECRETBLOB"], con);
        Assert.DoesNotContain("SECRETBLOB", con.All);
    }

    [Fact]
    public void RotateKey_ReportsWhatItDid()
    {
        var (cli, store, con) = Make();
        store.Data[Name] = "x";
        Assert.Equal(0, cli.Execute(["rotate-key"], con));
        Assert.Contains("1 value(s) now under key k2", con.All);
        Assert.Equal(["rotate"], store.Admin);
        Assert.Equal(2, cli.Execute(["rotate-key", "extra"], new FakeConsole()));
    }

    [Fact]
    public void Export_AsksForThePassphraseTwice_AndNeverTakesItOnTheCommandLine()
    {
        var (cli, store, con) = Make();
        con.Prompts.Enqueue("a passphrase of length"); con.Prompts.Enqueue("a passphrase of length");
        Assert.Equal(0, cli.Execute(["export", "out.yaml", "--prefix", "dbdata:proxy:"], con));
        Assert.Equal(["export out.yaml prefix=dbdata:proxy: pass=a passphrase of length"], store.Admin);
        Assert.DoesNotContain("a passphrase of length", con.All);

        var mismatch = new FakeConsole();
        mismatch.Prompts.Enqueue("one passphrase here"); mismatch.Prompts.Enqueue("another passphrase");
        Assert.Equal(1, cli.Execute(["export", "out.yaml"], mismatch));
        Assert.Single(store.Admin);

        Assert.Equal(2, cli.Execute(["export", "out.yaml", "hunter2hunter2"], new FakeConsole()));      // a second positional is not a passphrase
    }

    [Fact]
    public void Export_And_Import_CanReadThePassphraseFromStdin()
    {
        var (cli, store, _) = Make();
        var con = new FakeConsole { IsInputRedirected = true, Stdin = "piped passphrase here\r\nignored second line\n" };
        Assert.Equal(0, cli.Execute(["export", "out.yaml", "--passphrase-stdin"], con));
        Assert.Equal(0, cli.Execute(["import", "out.yaml", "--passphrase-stdin", "--overwrite"], new FakeConsole { IsInputRedirected = true, Stdin = "piped passphrase here\n" }));
        Assert.Equal(["export out.yaml prefix= pass=piped passphrase here", "import out.yaml overwrite=True pass=piped passphrase here"], store.Admin);
    }

    [Fact]
    public void Import_ReportsCounts_AndNeedsNoConfirmation()
    {
        var (cli, store, con) = Make();
        con.Prompts.Enqueue("the exporting passphrase");
        Assert.Equal(0, cli.Execute(["import", "in.yaml"], con));
        Assert.Contains("imported 2, overwritten 0, kept 1", con.All);
        Assert.Equal(1, con.PromptCount);
        Assert.Equal(["import in.yaml overwrite=False pass=the exporting passphrase"], store.Admin);
    }

    [Fact]
    public void Protect_PrintsTheInlineForm_NotTheValue()
    {
        var (cli, _, con) = Make();
        con.IsInputRedirected = true; con.Stdin = "plain-value\n";
        Assert.Equal(0, cli.Execute(["protect"], con));
        Assert.Equal(["dpapi:PROTECTED"], con.Out);
        Assert.DoesNotContain("plain-value", con.All);

        var s = new MemoryStore();
        var none = new FakeConsole { IsInputRedirected = true, Stdin = "v\n" };
        Assert.Equal(1, new SecretCli(s, s).Execute(["protect"], none));
        Assert.Contains("not available on this platform", none.All);
    }

    [Fact]
    public void Without_an_admin_the_store_verbs_say_so_and_list_shows_names_only()
    {
        var s = new MemoryStore();
        s.Data["dbdata:build:connection:a"] = "v";
        var cli = new SecretCli(s, s);
        foreach (var verb in new[] { new[] { "rotate-key" }, ["export", "x.yaml"], ["import", "x.yaml"] })
        {
            var con = new FakeConsole { IsInputRedirected = true, Stdin = "a long enough passphrase\n" };
            Assert.Equal(1, cli.Execute([.. verb, "--passphrase-stdin"], con));
            Assert.Contains("not available", con.All);
        }
        var list = new FakeConsole();
        Assert.Equal(0, cli.Execute(["list"], list));
        Assert.Equal(["dbdata:build:connection:a"], list.Out);
    }

    /// <summary>No verb prints a value: every one is run with a sentinel as the value and as the passphrase, and the sentinel is looked for in everything written.</summary>
    [Theory]
    [InlineData("set")]
    [InlineData("list")]
    [InlineData("delete")]
    [InlineData("check")]
    [InlineData("rotate-key")]
    [InlineData("export")]
    [InlineData("import")]
    [InlineData("protect")]
    public void No_verb_writes_the_value_or_the_passphrase_to_stdout_or_stderr(string verb)
    {
        const string Sentinel = "S3NT1NEL-do-not-print";
        var s = new MemoryStore();
        s.Data[Name] = Sentinel;
        var cli = new SecretCli(s, s, s, () => [new SecretRef(SecretRefKind.Secret, Name)], v => "dpapi:ENCRYPTED");
        var con = new FakeConsole { IsInputRedirected = true, Stdin = Sentinel + "\n" };
        string[] args = verb switch
        {
            "set" => ["set", Name, "--stdin"],
            "delete" => ["delete", Name],
            "export" => ["export", "o.yaml", "--passphrase-stdin"],
            "import" => ["import", "o.yaml", "--passphrase-stdin"],
            "protect" => ["protect", "--stdin"],
            _ => [verb],
        };
        cli.Execute(args, con);
        Assert.DoesNotContain(Sentinel, con.All);
        // and the command that fails has no value to print either
        var failing = new FakeConsole { IsInputRedirected = true, Stdin = Sentinel + "\n" };
        cli.Execute([.. args, "--bogus-flag", "extra-positional"], failing);
        Assert.DoesNotContain(Sentinel, failing.All);
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
            Assert.Equal(1, cli.Execute(["check", "file:" + tmp], con));
            Assert.Contains("InvalidDataException", con.All);
        }
        finally { File.Delete(tmp); }
    }
}
