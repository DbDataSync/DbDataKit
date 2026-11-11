using DbDataKit.Cli;
using DbDataKit.Runner.Packaging;
using DbDataKit.Scripting;

namespace DbDataRunner.Server;

/// <summary>
/// <c>package build</c> and <c>package test</c> (plan 27a IX20, docs/integrations.md): make a package from an integration project and run its tests. A csx project is built here with Roslyn and the NuGet client and needs only the .NET
/// runtime; a compiled project needs the SDK, and these verbs say so when it is missing instead of failing in a build program. The Workspace server runs them as a child process.
/// </summary>
internal static class IntegrationVerbs
{
    public const string Options = "[--source <folder or v3 index address>]... [--cache <directory>] [--offline] [--locked] [--no-lock-write]";

    public const string BuildSummary = "Build an integration project into a package (package.yaml and files/) without the .NET SDK: the C# files of src/ are compiled with Roslyn, the packages of their #r \"nuget:\" lines are restored and pinned in package.lock; a compiled .NET project needs the SDK";
    public const string TestSummary = "Build an integration project and run the scripts of its tests/ folder against the built package with the kit's RunSimulator (a script passes when it finishes and fails when it throws); no SDK, nothing is published";

    public static readonly string BuildSchema = DataSchemas.Object([("package", DataSchemas.String), ("files", DataSchemas.Integer), ("tasks", DataSchemas.StringList), ("diagnostics", DataSchemas.ListOf(DataSchemas.Object([("severity", DataSchemas.String), ("code", DataSchemas.String), ("message", DataSchemas.String), ("file", DataSchemas.String), ("line", DataSchemas.Integer)], "severity", "code", "message")))], "package", "files", "tasks");
    public static readonly string TestSchema = DataSchemas.Object([("tests", DataSchemas.Integer), ("failed", DataSchemas.Integer), ("results", DataSchemas.ListOf(DataSchemas.Object([("file", DataSchemas.String), ("passed", DataSchemas.Boolean), ("message", DataSchemas.String)], "file", "passed")))], "tests", "failed");

    private static readonly IReadOnlySet<string> Flags = new HashSet<string> { "offline", "locked", "no-lock-write" };
    private static readonly IReadOnlySet<string> Valued = new HashSet<string> { "out", "source", "cache" };

    private static (PackageBuildOptions? Options, string? Problem) OptionsOf(string[] args, Func<string, string?>? environment, out CliArgs parsed)
    {
        // --source may repeat: CliArgs keeps the last, so the repeats are read from the raw arguments
        parsed = CliArgs.Parse(args, Flags, Valued);
        var sources = new List<PackageSource>();
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--source") sources.Add(new PackageSource(args[i + 1]));
        foreach (var arg in args.Where(a => a.StartsWith("--source=", StringComparison.Ordinal))) sources.Add(new PackageSource(arg["--source=".Length..]));
        var cache = parsed.Get("cache") ?? Path.Combine(RunnerSettings.DefaultDirectory(environment), "build-cache");
        return (new PackageBuildOptions
        {
            CacheDirectory = cache,
            Sources = sources.Count > 0 ? sources : [new PackageSource("https://api.nuget.org/v3/index.json")],
            Offline = parsed.Has("offline"),
            RequirePinned = parsed.Has("locked"),
            WriteLock = !parsed.Has("no-lock-write"),
        }, null);
    }

    private static async Task<int> WriteDiagnosticsAsync(CliContext context, IEnumerable<BuildDiagnostic> diagnostics)
    {
        var list = diagnostics.ToList();
        foreach (var d in list) await (d.IsError ? context.Error : context.Out).WriteLineAsync($"{(d.IsError ? "" : d.Severity + ": ")}{d}");
        context.Data["diagnostics"] = list.Select(d => new Dictionary<string, object?> { ["severity"] = d.Severity, ["code"] = d.Code, ["message"] = d.Message, ["file"] = d.File, ["line"] = d.Line }).ToList();
        return list.Count;
    }

    /// <summary>The folder is a csx project, or a sentence why not (including the SDK's absence for a compiled one).</summary>
    private static string? Refusal(string project)
    {
        if (!Directory.Exists(project)) return $"'{project}' is not a folder.";
        return IntegrationProject.Detect(project) switch
        {
            IntegrationLanguage.Csx => null,
            IntegrationLanguage.CompiledProject => DotnetSdk.Find() is null ? DotnetSdk.Missing : "A compiled .NET project is built with 'dotnet publish' by the Workspace's build step; this command builds csx projects (C# files under src/).",
            _ => "The project has no code: put C# files in src/*.csx (or a .csproj in src/ for a compiled project).",
        };
    }

    public static async Task<int> BuildAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var (options, _) = OptionsOf(args, environment, out var parsed);
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Get("out") is not { } output)
        {
            await context.Error.WriteLineAsync($"Usage: package build <project directory> --out <directory> {Options}");
            return CliExit.Usage;
        }

        if (Refusal(parsed.Words[0]) is { } refusal)
        {
            await context.Error.WriteLineAsync(refusal);
            return CliExit.Failure;
        }

        var result = await CsxPackageBuilder.BuildAsync(parsed.Words[0], output, options!, context.Cancellation);
        await WriteDiagnosticsAsync(context, result.Diagnostics);
        if (!result.Succeeded)
        {
            await context.Error.WriteLineAsync(result.Message);
            return CliExit.Failure;
        }

        await context.Out.WriteLineAsync(result.Message + $" Written to {Path.GetFullPath(output)}.");
        context.Data["package"] = result.Package;
        context.Data["files"] = result.Files!.Count;
        context.Data["tasks"] = result.Tasks!.ToList();
        return CliExit.Ok;
    }

    public static async Task<int> TestAsync(string[] args, CliContext context, Func<string, string?>? environment)
    {
        var (options, _) = OptionsOf(args, environment, out var parsed);
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Has("out"))
        {
            await context.Error.WriteLineAsync($"Usage: package test <project directory> {Options}");
            return CliExit.Usage;
        }

        if (Refusal(parsed.Words[0]) is { } refusal)
        {
            await context.Error.WriteLineAsync(refusal);
            return CliExit.Failure;
        }

        var result = await IntegrationTester.TestAsync(parsed.Words[0], options!, context.Cancellation);
        await WriteDiagnosticsAsync(context, result.Diagnostics.Where(d => d.Code != "DBD-INT-TEST"));
        foreach (var t in result.Tests) await (t.Passed ? context.Out : context.Error).WriteLineAsync($"{(t.Passed ? "passed" : "FAILED")} {t.File}{(t.Message is null ? "" : ": " + t.Message)}");
        context.Data["tests"] = result.Tests.Count;
        context.Data["failed"] = result.Tests.Count(t => !t.Passed);
        context.Data["results"] = result.Tests.Select(t => new Dictionary<string, object?> { ["file"] = t.File, ["passed"] = t.Passed, ["message"] = t.Message }).ToList();
        await (result.Succeeded ? context.Out : context.Error).WriteLineAsync(result.Message);
        return result.Succeeded ? CliExit.Ok : CliExit.Failure;
    }
}
