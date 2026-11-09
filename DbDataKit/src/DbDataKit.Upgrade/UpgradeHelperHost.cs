using System.Diagnostics;
using DbDataKit.Update;
using DbDataKit.Update.Service;

namespace DbDataKit.Upgrade;

/// <summary>What the helper needs, as the arguments of its command line (<c>--dbdata-upgrade-helper</c>).</summary>
public sealed record HelperInvocation(string Root, string Product, string ProgramDirectory, string ServiceName, string RunningVersion, string TrustedKey, string HealthUrl)
{
    public const string Flag = "--dbdata-upgrade-helper";

    public IReadOnlyList<string> ToArguments() => [Flag, "--root", Root, "--product", Product, "--program-dir", ProgramDirectory, "--service", ServiceName, "--running", RunningVersion, "--trusted-key", TrustedKey, "--health-url", HealthUrl];

    public static HelperInvocation? TryParse(string[] args)
    {
        if (args.Length == 0 || args[0] != Flag) return null;
        string? Get(string key) { var i = Array.IndexOf(args, key); return i > 0 && i + 1 < args.Length ? args[i + 1] : null; }
        return new HelperInvocation(Get("--root") ?? throw Missing("--root"), Get("--product") ?? throw Missing("--product"), Get("--program-dir") ?? throw Missing("--program-dir"),
            Get("--service") ?? throw Missing("--service"), Get("--running") ?? throw Missing("--running"), Get("--trusted-key") ?? throw Missing("--trusted-key"), Get("--health-url") ?? throw Missing("--health-url"));
    }

    private static ArgumentException Missing(string flag) => new($"{Flag} needs {flag}.");
}

/// <summary>
/// The short-lived helper that swaps the program (plan 24a UC09): a copy of the old program, started by <see cref="ProcessUpgradeLauncher"/> with <see cref="HelperInvocation.Flag"/>, which runs <see cref="UpdateHelper"/> (the shelved
/// updater's swap with rollback, used as code) over the plan the server staged, and ends. A product's <c>Main</c> calls <see cref="TryRunAsync"/> first.
/// </summary>
public static class UpgradeHelperHost
{
    /// <summary>Runs the helper when <paramref name="args"/> ask for it and returns its exit code (0 committed, 1 rolled back or refused, 2 needs an administrator); null when they do not, and the program starts as usual.</summary>
    public static async Task<int?> TryRunAsync(string[] args, TextWriter? log = null)
    {
        HelperInvocation? invocation;
        try { invocation = HelperInvocation.TryParse(args); }
        catch (ArgumentException ex) { (log ?? Console.Error).WriteLine(ex.Message); return 3; }
        if (invocation is null) return null;
        var outcome = await RunAsync(invocation, new CommandServiceControl(invocation.ServiceName), new VersionHealthProbe(), log is null ? Console.WriteLine : log.WriteLine);
        return outcome.Kind switch { UpdateOutcomeKind.Committed or UpdateOutcomeKind.NoPlan => 0, UpdateOutcomeKind.NeedsOperator => 2, _ => 1 };
    }

    /// <summary>The swap itself, with the service control and the health probe given (a test passes fakes).</summary>
    public static Task<UpdateOutcome> RunAsync(HelperInvocation invocation, IServiceControl service, IHealthProbe health, Action<string>? log = null, CancellationToken ct = default)
    {
        var layout = new InstallLayout(invocation.Root);
        var separator = invocation.TrustedKey.IndexOf('=');
        var trust = new TrustStore([new TrustedKey(invocation.TrustedKey[..separator], Convert.FromBase64String(invocation.TrustedKey[(separator + 1)..]))]);
        var helper = new UpdateHelper(layout, new ProgramFolderPointer(layout, invocation.ProgramDirectory, invocation.RunningVersion), service, health, new NoMigrations(), trust,
            new UpdateHelperOptions { Product = invocation.Product, Channel = Channel.Snapshot }, log: log);
        return helper.RunAsync(ct);
    }
}

/// <summary>
/// Starts the helper as a process that survives the service stopping: a copy of the program folder (on Windows a running program cannot be moved, and the folder is about to be), run from the upgrade area, under
/// <c>systemd-run</c> where the server is a systemd unit (a child of the unit is stopped with it).
/// </summary>
public sealed class ProcessUpgradeLauncher : IUpgradeLauncher
{
    public Task LaunchAsync(HelperInvocation invocation, CancellationToken ct)
    {
        var copy = Path.Combine(invocation.Root, "helper-" + Guid.NewGuid().ToString("N")[..8]);
        CopyDirectory(invocation.ProgramDirectory, copy);
        var command = Command(invocation, copy, Environment.ProcessPath, Environment.GetCommandLineArgs().ElementAtOrDefault(0),
            OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("INVOCATION_ID") is not null && File.Exists("/usr/bin/systemd-run"));
        var info = new ProcessStartInfo(command[0]) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = copy, RedirectStandardInput = true };
        foreach (var a in command.Skip(1)) info.ArgumentList.Add(a);
        Process.Start(info)?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// The command that runs the copy of the program: its own executable (a single-file app or an apphost), or <c>dotnet app.dll</c> for a framework-dependent install, where the entry dll is the first command-line argument.
    /// <c>Assembly.Location</c> is empty in a single-file app (IL3000), so it is not used.
    /// </summary>
    public static List<string> Command(HelperInvocation invocation, string copy, string? processPath, string? commandLine0, bool systemd)
    {
        if (processPath is null) throw new InvalidOperationException("The program's own path is not known.");
        var viaDotnet = Path.GetFileNameWithoutExtension(processPath).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
        var command = new List<string>();
        if (systemd) command.AddRange(["/usr/bin/systemd-run", "--quiet", "--collect", "--unit=" + invocation.ServiceName + "-upgrade-" + Path.GetFileName(copy)]);
        if (viaDotnet)
        {
            if (string.IsNullOrEmpty(commandLine0)) throw new InvalidOperationException("The entry assembly of the program is not known.");
            command.AddRange([processPath, Path.Combine(copy, Path.GetRelativePath(invocation.ProgramDirectory, Path.GetFullPath(commandLine0)))]);
        }
        else command.Add(Path.Combine(copy, Path.GetRelativePath(invocation.ProgramDirectory, processPath)));
        command.AddRange(invocation.ToArguments());
        return command;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var d in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, d)));
        foreach (var f in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(destination, Path.GetRelativePath(source, f)), true);
    }
}
