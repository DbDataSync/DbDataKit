using DbDataSync.Cli.Tui;
using DbDataSync.Core.Config;
using DbDataSync.Libraries;

namespace DbDataSync.Cli;

/// <summary>
/// <c>dbdatasync server setup</c> — a TUI (Terminal.Gui, phase 128) for a fresh install or an existing one
/// alike: <see cref="Tui.SetupScreen"/> is the one screen both cases open, pre-filled from the current
/// configuration when there is one. Everything it writes goes through the same
/// <see cref="DbDataSyncConfigFile"/> and <c>SecretStore</c> calls a
/// scripted deployment already uses; this exists to save a first-time operator from having to know
/// docs/configuration.md by heart, not to add a configuration path nothing else uses.
/// </summary>
public static class SetupCommand
{
    public static Task<int> RunAsync(string[] args) => RunAsync(args, LibraryInstaller.InstallAsync);

    /// <summary>
    /// The <paramref name="installLibrary"/> seam exists only for tests — a real run always passes
    /// <see cref="LibraryInstaller.InstallAsync"/>, which shells out to <c>dotnet publish</c> and would
    /// make every driver-step test a network call.
    /// </summary>
    internal static async Task<int> RunAsync(
        string[] args,
        Func<string, string, IReadOnlyList<PackageRef>, string, string?, CancellationToken, Task<LibraryManifest>> installLibrary)
    {
        // No fallback for a missing terminal: setup needs a real console the same way any full-screen
        // TUI does, and anyone without one already has the config file and the individual CLI
        // subcommands (`config check`, `config secret set`, ...) — this refuses rather than degrading.
        if (Console.IsInputRedirected || Console.IsOutputRedirected)
        {
            Console.Error.WriteLine(
                "setup is interactive — run `dbdatasync config check` to check a configuration, or " +
                $"edit {DbDataSyncConfigFile.FileName} directly (see docs/configuration.md).");
            return 1;
        }

        using var session = TuiSession.Start();
        var app = session.App;

        var candidate = DbDataSyncRoot.Resolve(args);
        // Clean break (plan 13, E04): the old files are refused, and setup is how the settings are written again, so it says what it found and stops.
        if (DbDataSyncRoot.LegacyRefusal(candidate) is { } refusal)
        {
            Console.Error.WriteLine(refusal);
            return 1;
        }

        var root = candidate;

        if (!ExistingSetup.DetectedAt(root))
        {
            root = Path.GetFullPath(Dialogs.PromptText(app, "dbdatasync server setup", "Config folder", candidate));

            if (ExistingSetup.DetectedAt(root))
                Dialogs.Message(app, "Already configured", "That folder already has a DbDataSync configuration.");
            else
                ServeCommand.Prepare(root);
        }

        return await SetupScreen.RunAsync(root, app, installLibrary);
    }
}
