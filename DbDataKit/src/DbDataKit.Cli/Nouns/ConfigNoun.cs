namespace DbDataKit.Cli;

/// <summary>One setting as the server sees it. The surface redacts a secret before it is returned: <see cref="Value"/> of a secret is never the secret.</summary>
public sealed record ConfigEntry(string Key, string? Value, string? Origin = null, bool IsSecret = false);

public sealed record ConfigSetResult(bool Ok, string? Problem = null)
{
    public static ConfigSetResult Success { get; } = new(true);
}

/// <summary>What a product offers to the shared <c>config</c> noun: its effective settings, one setting, a change, a check, and the schema.</summary>
public interface IConfigSurface
{
    IReadOnlyList<ConfigEntry> Show();

    ConfigEntry? Get(string key);

    /// <summary>Validates and writes one setting. A refusal explains itself without repeating the value.</summary>
    ConfigSetResult Set(string key, string value);

    /// <summary>Everything wrong with the configuration as it is now; empty when it is sound.</summary>
    IReadOnlyList<string> Check();

    /// <summary>The JSON Schema of the configuration, as text.</summary>
    string Schema();
}

/// <summary><c>config check|show|get|set|schema</c>: the server's own configuration (no <c>migrate</c>: an old file is refused, not converted).</summary>
public static class ConfigNoun
{
    public static Noun Create(IConfigSurface surface) => new("config", "The server's own configuration",
    [
        new Verb("check", "Report everything wrong with the configuration; exit 1 when anything is", EffectClass.OfflineOnly, "", (a, c) => CheckAsync(surface, a, c),
            DataSchemas.Object([("problems", DataSchemas.StringList)], "problems")),
        new Verb("show", "The effective settings, with where each came from; secrets are redacted", EffectClass.OfflineOnly, "", (a, c) => ShowAsync(surface, a, c),
            DataSchemas.Object([("settings", DataSchemas.ListOf(DataSchemas.Object([("key", DataSchemas.String), ("value", DataSchemas.String), ("origin", DataSchemas.String), ("is_secret", DataSchemas.Boolean)], "key", "is_secret")))], "settings")),
        new Verb("get", "One setting", EffectClass.OfflineOnly, "<key>", (a, c) => GetAsync(surface, a, c), DataSchemas.Object([("key", DataSchemas.String), ("value", DataSchemas.String), ("origin", DataSchemas.String), ("is_secret", DataSchemas.Boolean)], "key", "is_secret")),
        new Verb("set", "Change one setting (a secret is set with 'secret set', never here)", EffectClass.RepoFilesOnly, "<key> <value>", (a, c) => SetAsync(surface, a, c),
            DataSchemas.Object([("key", DataSchemas.String)], "key")),
        new Verb("schema", "The JSON Schema of the configuration", EffectClass.OfflineOnly, "", (a, c) => SchemaAsync(surface, a, c),
            DataSchemas.Object([("schema", DataSchemas.String)], "schema")),
    ]);

    private static readonly IReadOnlySet<string> NoFlags = new HashSet<string>();
    private static async Task<(CliArgs? Args, int Exit)> Parse(string[] args, CliContext context, string usage, int words)
    {
        var parsed = CliArgs.Parse(args, NoFlags, NoFlags);
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != words)
        {
            await context.Error.WriteLineAsync($"Usage: {usage}");
            return (null, CliExit.Usage);
        }
        return (parsed, CliExit.Ok);
    }

    private static async Task<int> CheckAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (_, exit) = await Parse(args, context, "config check", 0);
        if (exit != CliExit.Ok) return exit;
        var problems = surface.Check();
        context.Data["problems"] = problems;
        if (problems.Count == 0) await context.Out.WriteLineAsync("ok");
        else foreach (var problem in problems) await context.Error.WriteLineAsync(problem);
        return problems.Count == 0 ? CliExit.Ok : CliExit.Failure;
    }

    private static async Task<int> ShowAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (_, exit) = await Parse(args, context, "config show", 0);
        if (exit != CliExit.Ok) return exit;
        var entries = surface.Show();
        context.Data["settings"] = entries.Select(Redacted).ToList();
        foreach (var entry in entries) await context.Out.WriteLineAsync($"{entry.Key} = {Display(entry)}{(entry.Origin is null ? "" : $"  ({entry.Origin})")}");
        return CliExit.Ok;
    }

    private static async Task<int> GetAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (parsed, exit) = await Parse(args, context, "config get <key>", 1);
        if (exit != CliExit.Ok) return exit;
        if (surface.Get(parsed!.Words[0]) is not { } entry)
        {
            await context.Error.WriteLineAsync($"There is no setting '{parsed.Words[0]}'. 'config show' lists them.");
            return CliExit.Failure;
        }
        var shown = Redacted(entry);
        context.Data["key"] = shown.Key;
        context.Data["value"] = shown.Value;
        context.Data["origin"] = shown.Origin;
        context.Data["is_secret"] = shown.IsSecret;
        await context.Out.WriteLineAsync(Display(entry));
        return CliExit.Ok;
    }

    private static async Task<int> SetAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (parsed, exit) = await Parse(args, context, "config set <key> <value>", 2);
        if (exit != CliExit.Ok) return exit;
        var key = parsed!.Words[0];
        if (surface.Get(key) is { IsSecret: true })
        {
            await context.Error.WriteLineAsync($"'{key}' holds a secret. Set it with 'secret set', which takes the value from a prompt or standard input.");
            return CliExit.Usage;
        }
        var result = surface.Set(key, parsed.Words[1]);
        if (!result.Ok)
        {
            await context.Error.WriteLineAsync(result.Problem ?? $"'{key}' was not changed.");
            return CliExit.Failure;
        }
        context.Data["key"] = key;
        await context.Out.WriteLineAsync($"set {key}");
        return CliExit.Ok;
    }

    private static async Task<int> SchemaAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (_, exit) = await Parse(args, context, "config schema", 0);
        if (exit != CliExit.Ok) return exit;
        context.Data["schema"] = surface.Schema();
        await context.Out.WriteLineAsync(surface.Schema());
        return CliExit.Ok;
    }

    // a secret is shown as a mask whatever the surface returned: the contract is that it redacts, and this is the second lock
    private static string Display(ConfigEntry entry) => entry.IsSecret ? "***" : entry.Value ?? "";

    private static ConfigEntry Redacted(ConfigEntry entry) => entry.IsSecret ? entry with { Value = "***" } : entry;
}
