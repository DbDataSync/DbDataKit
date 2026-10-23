using System.Reflection;
using DbDataSync.Api.Configuration;

namespace DbDataSync.Api.Services;

/// <summary>
/// The program and leading arguments that start a child process of this application: a worker for one replication, or the
/// connection-scoped library validation. A child gets its own process so a crash or a stuck driver stays out of the API, and it
/// is started as <c>dbdatasync internal worker ...</c> or <c>dbdatasync config library validate ...</c>, that is, as this same
/// executable again, rather than as <c>dotnet exec &lt;file&gt;</c>. That needs no <c>dotnet</c> on the path and so lets Sync be
/// published self-contained (plan 13, D11 and slice F14).
/// <para>
/// This executable is <c>DbDataSync.Cli</c> whether it runs under the update launcher (the launcher's own file is then the process,
/// and it forwards the arguments to the CLI in the current slot), as its own published file, or as <c>dotnet DbDataSync.Cli.dll</c>.
/// When the entry assembly is something else (a test host, or <c>DbDataSync.Api.dll</c> run directly in development), the
/// configured <see cref="ApiOptions.TaskRunnerDllPath"/> and <see cref="ApiOptions.CliDllPath"/> say which files to run, and without them
/// the files beside this assembly are used, as before.
/// </para>
/// </summary>
internal static class ChildCommand
{
    private const string CliAssemblyName = "DbDataSync.Cli";

    public static (string FileName, string[] LeadingArguments) ForWorker(ApiOptions options) =>
        options.TaskRunnerDllPath is { } dll ? Dotnet(dll)
        : Self() is { } self ? (self.FileName, [.. self.LeadingArguments, "internal", "worker"])
        : Dotnet(ApiOptions.ResolveDefaultTaskRunnerDllPath());

    public static (string FileName, string[] LeadingArguments) ForCli(ApiOptions options) =>
        options.CliDllPath is { } dll ? Dotnet(dll)
        : Self() is { } self ? self
        : Dotnet(ApiOptions.ResolveDefaultCliDllPath());

    /// <summary>This application's own command, or null when the running program is not <c>DbDataSync.Cli</c> and so cannot be started again with its arguments.</summary>
    private static (string FileName, string[] LeadingArguments)? Self()
    {
        var entry = Assembly.GetEntryAssembly();
        return Self(Environment.ProcessPath, entry?.GetName().Name, entry?.Location);
    }

    internal static (string FileName, string[] LeadingArguments)? Self(string? processPath, string? entryAssemblyName, string? entryAssemblyLocation)
    {
        if (string.IsNullOrEmpty(processPath) || entryAssemblyName != CliAssemblyName)
            return null;

        // Run as `dotnet DbDataSync.Cli.dll ...` the process is the dotnet host and the CLI is a file it was given; anything else is
        // the executable itself (the launcher or a published file).
        var program = processPath[(processPath.LastIndexOfAny(['/', '\\']) + 1)..];
        return string.Equals(Path.GetFileNameWithoutExtension(program), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? (processPath, ["exec", entryAssemblyLocation ?? ""])
            : (processPath, []);
    }

    private static (string FileName, string[] LeadingArguments) Dotnet(string dllPath) => ("dotnet", ["exec", dllPath]);
}
