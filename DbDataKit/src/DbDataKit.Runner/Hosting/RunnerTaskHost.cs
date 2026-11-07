namespace DbDataKit.Runner;

/// <summary>
/// Finds the task host, the process a runner starts for a .NET package. The build publishes it into <c>taskhost/</c> beside the program (and the publish output carries it the same way), with its own dependencies and runtime
/// configuration, because it runs on plain .NET and not on the ASP.NET shared framework the server runs on. A host copied loose beside the program (the layout of an install made by hand) is found too.
/// </summary>
public static class RunnerTaskHost
{
    public const string Folder = "taskhost";
    public const string Name = "dbdatakit-taskhost";

    /// <summary>The directory the program is in.</summary>
    public static string ProgramDirectory => Path.GetDirectoryName(typeof(RunnerTaskHost).Assembly.Location) is { Length: > 0 } here ? here : AppContext.BaseDirectory;

    /// <summary>The task host beside <paramref name="programDirectory"/>, or null. The <c>.dll</c> (started through <c>dotnet</c>) is preferred to the apphost: it needs no more than the runtime the program itself runs on.</summary>
    public static string? Find(string? programDirectory = null)
    {
        programDirectory ??= ProgramDirectory;
        foreach (var directory in new[] { Path.Combine(programDirectory, Folder), programDirectory })
        {
            var dll = Path.Combine(directory, Name + ".dll");
            if (File.Exists(dll)) return dll;
            var apphost = Path.Combine(directory, OperatingSystem.IsWindows() ? Name + ".exe" : Name);
            if (File.Exists(apphost)) return apphost;
        }

        return null;
    }

    /// <summary>The configured task host, or the one beside the program; refuses with the places looked in when there is none.</summary>
    public static string Require(string? configured = null, string? programDirectory = null)
    {
        if (configured is { Length: > 0 })
            return File.Exists(configured) ? Path.GetFullPath(configured) : throw new InvalidOperationException($"The task host '{configured}' does not exist.");
        programDirectory ??= ProgramDirectory;
        return Find(programDirectory)
            ?? throw new InvalidOperationException(
                $"The task host ({Name}.dll) was not found in {Path.Combine(programDirectory, Folder)} or {programDirectory}. It is published with the program; reinstall the runner, or build it with 'dotnet build' from the DbDataRunner folder.");
    }
}
