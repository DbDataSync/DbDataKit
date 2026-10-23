using System.Text.Json;

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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static Noun Create(IConfigSurface surface) => new("config", "The server's own configuration",
    [
        new Verb("check", "Report everything wrong with the configuration; exit 1 when anything is", EffectClass.OfflineOnly, "[--format json]", (a, c) => CheckAsync(surface, a, c)),
        new Verb("show", "The effective settings, with where each came from; secrets are redacted", EffectClass.OfflineOnly, "[--format json]", (a, c) => ShowAsync(surface, a, c)),
        new Verb("get", "One setting", EffectClass.OfflineOnly, "<key> [--format json]", (a, c) => GetAsync(surface, a, c)),
        new Verb("set", "Change one setting (a secret is set with 'secret set', never here)", EffectClass.RepoFilesOnly, "<key> <value>", (a, c) => SetAsync(surface, a, c)),
        new Verb("schema", "The JSON Schema of the configuration", EffectClass.OfflineOnly, "", (a, c) => SchemaAsync(surface, a, c)),
    ]);

    private static readonly IReadOnlySet<string> NoFlags = new HashSet<string>();
    private static readonly IReadOnlySet<string> FormatOption = new HashSet<string> { "format" };

    private static async Task<(CliArgs? Args, bool Json, int Exit)> Parse(string[] args, CliContext context, string usage, int words)
    {
        var parsed = CliArgs.Parse(args, NoFlags, FormatOption);
        var json = parsed.Get("format") is "json";
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != words || (parsed.Has("format") && !json))
        {
            await context.Error.WriteLineAsync($"Usage: {usage}");
            return (null, false, CliExit.Usage);
        }
        return (parsed, json, CliExit.Ok);
    }

    private static async Task<int> CheckAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (_, json, exit) = await Parse(args, context, "config check [--format json]", 0);
        if (exit != CliExit.Ok) return exit;
        var problems = surface.Check();
        if (json) await context.Out.WriteLineAsync(JsonSerializer.Serialize(new { ok = problems.Count == 0, problems }, Json));
        else if (problems.Count == 0) await context.Out.WriteLineAsync("ok");
        else foreach (var problem in problems) await context.Error.WriteLineAsync(problem);
        return problems.Count == 0 ? CliExit.Ok : CliExit.Failure;
    }

    private static async Task<int> ShowAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (_, json, exit) = await Parse(args, context, "config show [--format json]", 0);
        if (exit != CliExit.Ok) return exit;
        var entries = surface.Show();
        if (json) await context.Out.WriteLineAsync(JsonSerializer.Serialize(entries.Select(Redacted), Json));
        else foreach (var entry in entries) await context.Out.WriteLineAsync($"{entry.Key} = {Display(entry)}{(entry.Origin is null ? "" : $"  ({entry.Origin})")}");
        return CliExit.Ok;
    }

    private static async Task<int> GetAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (parsed, json, exit) = await Parse(args, context, "config get <key> [--format json]", 1);
        if (exit != CliExit.Ok) return exit;
        if (surface.Get(parsed!.Words[0]) is not { } entry)
        {
            await context.Error.WriteLineAsync($"There is no setting '{parsed.Words[0]}'. 'config show' lists them.");
            return CliExit.Failure;
        }
        await context.Out.WriteLineAsync(json ? JsonSerializer.Serialize(Redacted(entry), Json) : Display(entry));
        return CliExit.Ok;
    }

    private static async Task<int> SetAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (parsed, _, exit) = await Parse(args, context, "config set <key> <value>", 2);
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
        await context.Out.WriteLineAsync($"set {key}");
        return CliExit.Ok;
    }

    private static async Task<int> SchemaAsync(IConfigSurface surface, string[] args, CliContext context)
    {
        var (_, _, exit) = await Parse(args, context, "config schema", 0);
        if (exit != CliExit.Ok) return exit;
        await context.Out.WriteLineAsync(surface.Schema());
        return CliExit.Ok;
    }

    // a secret is shown as a mask whatever the surface returned: the contract is that it redacts, and this is the second lock
    private static string Display(ConfigEntry entry) => entry.IsSecret ? "***" : entry.Value ?? "";

    private static ConfigEntry Redacted(ConfigEntry entry) => entry.IsSecret ? entry with { Value = "***" } : entry;
}
