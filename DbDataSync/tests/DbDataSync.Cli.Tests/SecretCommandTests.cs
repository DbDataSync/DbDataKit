using DbDataSync.Core.Secrets;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// <c>dbdatasync config secret set|list|remove</c> — thin wrappers over <see cref="SecretStore"/>, reachable
/// without going through a connection's own save flow (phase 79). The command opens the encrypted store under the repository root
/// (<c>--repo</c>), so these run against a scratch root and read back through a store opened on the same one.
/// </summary>
public sealed class SecretCommandTests : IDisposable
{
    private const string Ref = "dbdata:sync:test:sentinel";

    private readonly string _root = Directory.CreateTempSubdirectory("dbdatasync-secret-cmd-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* a scratch directory under the temp dir */ }
    }

    [Fact]
    public void SetThenList_ReportsItAsSet_WithoutPrintingTheValue()
    {
        var (setCode, setOutput) = Run(["set", Ref, "sup3r-secret"]);
        Assert.Equal(0, setCode);
        Assert.DoesNotContain("sup3r-secret", setOutput);

        var (listCode, output) = Run(["list", Ref]);

        Assert.Equal(0, listCode);
        Assert.Contains(Ref, output);
        Assert.Contains("set", output);
        Assert.DoesNotContain("sup3r-secret", output);
    }

    [Fact]
    public void List_ARefNeverSet_ReportsNotSet()
    {
        var (code, output) = Run(["list", Ref]);

        Assert.Equal(0, code);
        Assert.Contains("not set", output);
    }

    [Fact]
    public void SetThenRemove_ThenList_ReportsNotSet()
    {
        Run(["set", Ref, "sup3r-secret"]);
        var (removeCode, _) = Run(["remove", Ref]);
        Assert.Equal(0, removeCode);

        var (_, output) = Run(["list", Ref]);
        Assert.Contains("not set", output);
    }

    [Fact]
    public void Set_WrongArgumentCount_FailsWithoutStoringAnything()
    {
        var (code, _) = Run(["set", Ref]);

        Assert.NotEqual(0, code);
        var (_, output) = Run(["list", Ref]);
        Assert.Contains("not set", output);
    }

    /// <summary>What the command wrote is in the encrypted store of that root: a store opened on it afterwards finds it.</summary>
    [Fact]
    public void Set_IsResolvableFromAStoreOpenedOnTheSameRoot()
    {
        Run(["set", Ref, "sup3r-secret"]);

        Assert.Equal("sup3r-secret", SecretStore.Open(_root).Resolve(Ref));
    }

    /// <summary>A name in the old form (<c>dbdatasync:...</c>) is not a name of the store: setting one is refused with the form to use, and nothing is stored.</summary>
    [Fact]
    public void Set_AnOldFormName_IsRefusedNamingTheForm()
    {
        var (code, output) = Run(["set", "dbdatasync:connection:erp", "sup3r-secret"]);

        Assert.NotEqual(0, code);
        Assert.Contains("dbdata:sync:", output);
        Assert.DoesNotContain("sup3r-secret", output);
    }

    private (int ExitCode, string Output) Run(string[] args)
    {
        var originalOut = Console.Out;
        var originalErr = Console.Error;
        using var output = new StringWriter();
        Console.SetOut(output);
        Console.SetError(output);
        try
        {
            var exitCode = SecretCommand.Run([.. args, "--repo", _root]);
            return (exitCode, output.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalErr);
        }
    }
}
