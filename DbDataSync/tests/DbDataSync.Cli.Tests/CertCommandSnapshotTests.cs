namespace DbDataSync.Cli.Tests;

[CollectionDefinition("ConsoleCapture", DisableParallelization = true)]
public sealed class ConsoleCaptureCollection;

/// <summary>
/// <c>config cert</c>'s help text and exit codes, pinned so moving its verbs onto the shared command's registration
/// point cannot change what an operator or a script sees.
/// </summary>
[Collection("ConsoleCapture")]
public sealed class CertCommandSnapshotTests
{
    private const string ExpectedUsage =
        """
        Usage: dbdatasync cert use-pem --cert <path> --key <path> [--repo <path>]     (any OS)
               dbdatasync cert use-pfx --pfx <path> [--repo <path>]                    (any OS)
               dbdatasync cert status [--account <account>]                            (any OS; --account is Windows-only)
               dbdatasync cert new-self-signed [--file] [--days <n>] [--repo <path>]       (any OS; writes <repo>/tls/dbdatasync.pfx and points Kestrel at it — the default off Windows, --file asks for it on Windows too)
               dbdatasync cert new-self-signed [--store] --dns <names> [--days <n>] [--account <account>]  (Windows only; installs into the certificate store — the default there, and needs an elevated prompt)
               dbdatasync cert list [--location LocalMachine|CurrentUser]               (Windows only)
               dbdatasync cert enroll --dns <names> [--ca <config>] [--template <name>] [--account <account>]  (Windows only)
               dbdatasync cert renew [--ca <config>] [--template <name>] [--account <account>] [--days <n>]    (Windows only)
               dbdatasync cert retrieve --request-id <id> [--account <account>]        (Windows only)
               dbdatasync cert templates [--ca <config>]                                (Windows only)
               dbdatasync cert discover [--repo <path>]                                 (Windows only; finds the CA and template, saves them when there is only one)
               dbdatasync cert bind --thumbprint <thumbprint> [--location LocalMachine|CurrentUser] [--allow-invalid|--no-allow-invalid]  (Windows only)
        """;

    private static (int ExitCode, string Error) Run(params string[] args)
    {
        var original = Console.Error;
        var captured = new StringWriter();
        Console.SetError(captured);
        try
        {
            return (CertCommand.Run(args), captured.ToString().ReplaceLineEndings("\n"));
        }
        finally
        {
            Console.SetError(original);
        }
    }

    [Fact]
    public void NoArguments_PrintsTheUsageAndExitsOne()
    {
        var (exitCode, error) = Run();

        Assert.Equal(1, exitCode);
        Assert.Equal(ExpectedUsage.ReplaceLineEndings("\n") + "\n", error);
    }

    [Fact]
    public void UnknownVerb_SaysSo_PrintsTheUsageAndExitsOne()
    {
        var (exitCode, error) = Run("frobnicate");

        Assert.Equal(1, exitCode);
        Assert.Equal("Unknown cert command 'frobnicate'.\n" + ExpectedUsage.ReplaceLineEndings("\n") + "\n", error);
    }

    [Fact]
    public void WindowsOnlyVerbs_OffWindows_AreRefusedWithExitOne()
    {
        if (OperatingSystem.IsWindows())
            return; // the refusal is for other platforms

        foreach (var verb in new[] { "list", "enroll", "renew", "retrieve", "templates", "discover", "bind" })
        {
            var (exitCode, error) = Run(verb);

            Assert.Equal(1, exitCode);
            Assert.Contains("Windows-only", error);
        }
    }

    [Fact]
    public void VerbsAreMatchedCaseInsensitively()
    {
        var (exitCode, error) = Run("FROBNICATE");

        Assert.Equal(1, exitCode);
        Assert.StartsWith("Unknown cert command 'frobnicate'.", error);
    }

    /// <summary>Every verb <c>config cert</c> has accepted so far, in usage order. Removing or renaming one breaks scripts.</summary>
    [Fact]
    public void RegisteredVerbs_AreExactlyTheOnesSyncAlwaysHad()
    {
        Assert.Equal(
            ["use-pem", "use-pfx", "status", "new-self-signed", "list", "enroll", "renew", "retrieve", "templates", "discover", "bind"],
            CertCommand.VerbNames);
    }
}
