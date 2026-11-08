using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Cli;
using DbDataKit.State;
using DbDataKit.Views;
using DbDataProxy.Server.Serving;

namespace DbDataProxy.Server.Hosting;

/// <summary>
/// <c>dbdataproxy views ...</c> (plan 32, DBR11b): the dashboards and reports the group serves, in the Proxy's state. <c>publish</c> checks a document as the registry does (the loader, then the engine's validator over the version of each semantic
/// model it queries that the group serves) and serves it on the production channel; every instance that runs the <c>views</c> module serves it when it next looks. It works with the server stopped or running. A model is published first
/// (<c>dbdatabuild</c>, or <c>PUT /api/v1/semantic-models/&lt;name&gt;</c>): a document names models by name and is pinned to the version the group serves now.
/// </summary>
public static partial class ViewsCommand
{
    private const string Where = "[--repo <data directory>]";

    public static Noun Noun() => new("views", "The dashboards and reports this proxy's group serves: publish a document, list what is served",
    [
        new Verb("publish", "Publish a dashboard or a report from its file (YAML or JSON): checked against the models the group serves, then served on the production channel", EffectClass.RepoFilesOnly,
            $"--file <document> [--kind dashboard|report, default the document's own] [--name <name, default the file's name>] [--expected <version>] [--note <text>] {Where}", PublishAsync,
            DataSchemas.Object([("kind", DataSchemas.String), ("name", DataSchemas.String), ("version", DataSchemas.String), ("created", DataSchemas.Boolean), ("unchanged", DataSchemas.Boolean), ("depends_on", DataSchemas.ListOf(DataSchemas.String))], "kind", "name", "version", "created", "unchanged", "depends_on")),
        new Verb("list", "The dashboards and reports served on the production channel, with their versions", EffectClass.OfflineOnly, $"[--kind dashboard|report] {Where}", ListAsync,
            DataSchemas.Object([("documents", DataSchemas.ListOf(DataSchemas.Object([("kind", DataSchemas.String), ("name", DataSchemas.String), ("version", DataSchemas.Integer)], "kind", "name", "version")))], "documents")),
    ]);

    [GeneratedRegex(@"^\s*""?kind""?\s*:\s*""?(dashboard|report)""?\s*,?\s*$", RegexOptions.Multiline)]
    private static partial Regex KindLine();

    private static ViewsPublishOutcome PublishTo(ProxyViewsPublishing publishing, string kind, string name, string text, int? expected, string? note) =>
        publishing.Publish(kind, name, text, Environment.UserName, expected, note);

    private static async Task<int> PublishAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "file", "kind", "name", "expected", "note", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("file") is not { Length: > 0 } file)
        {
            await context.Error.WriteLineAsync($"Usage: views publish --file <document> [--kind dashboard|report] [--name <name>] [--expected <version>] [--note <text>] {Where}");
            return CliExit.Usage;
        }
        var path = Path.GetFullPath(file);
        if (!File.Exists(path)) { await context.Error.WriteLineAsync($"{path} does not exist."); return CliExit.Failure; }
        var text = await File.ReadAllTextAsync(path);
        var kind = parsed.Get("kind") ?? (KindLine().Match(text) is { Success: true } m ? m.Groups[1].Value : null);
        if (kind is null) { await context.Error.WriteLineAsync("The document does not say `kind: dashboard` or `kind: report`; name it with --kind."); return CliExit.Usage; }
        int? expected = null;
        if (parsed.Get("expected") is { Length: > 0 } e)
        {
            if (!int.TryParse(e, out var n) || n < 0) { await context.Error.WriteLineAsync($"'{e}' is not a version number."); return CliExit.Usage; }
            expected = n;
        }
        var name = parsed.Get("name") ?? Path.GetFileNameWithoutExtension(path);
        var state = IdentityCommand.Open(parsed);
        var outcome = PublishTo(new ProxyViewsPublishing(new DefinitionStore(state), state), kind, name, text, expected, parsed.Get("note"));
        if (outcome.Status != 200)
        {
            await context.Error.WriteLineAsync((string?)outcome.Body["error"] ?? "The document was refused.");
            return CliExit.Failure;
        }
        var body = outcome.Body;
        await context.Out.WriteLineAsync($"Published {kind} {name} version {(string?)body["version"]}{((bool?)body["unchanged"] == true ? " (unchanged)" : "")}; it depends on {string.Join(", ", ((JsonArray)body["depends_on"]!).Select(d => (string?)d))}. It is served on the production channel.");
        context.Data["kind"] = kind;
        context.Data["name"] = name;
        context.Data["version"] = (string?)body["version"] ?? "";
        context.Data["created"] = (bool?)body["created"] ?? false;
        context.Data["unchanged"] = (bool?)body["unchanged"] ?? false;
        context.Data["depends_on"] = ((JsonArray)body["depends_on"]!).Select(d => (string?)d ?? "").ToList();
        return CliExit.Ok;
    }

    private static async Task<int> ListAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "kind", "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || (parsed.Get("kind") is { } k && !ViewDefinitions.Kinds.Contains(k)))
        {
            await context.Error.WriteLineAsync($"Usage: views list [--kind dashboard|report] {Where}");
            return CliExit.Usage;
        }
        var state = IdentityCommand.Open(parsed);
        var rows = new ProxyViewsPublishing(new DefinitionStore(state), state).List(parsed.Get("kind")).Where(h => h.Active is > 0).ToList();
        foreach (var h in rows) await context.Out.WriteLineAsync($"{h.Kind,-10} {h.Name,-32} version {h.Active}");
        if (rows.Count == 0) await context.Out.WriteLineAsync("No dashboard or report is served.");
        context.Data["documents"] = rows.Select(h => new Dictionary<string, object?> { ["kind"] = h.Kind, ["name"] = h.Name, ["version"] = h.Active }).ToList();
        return CliExit.Ok;
    }
}
