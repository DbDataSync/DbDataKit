using System.Text.Json.Nodes;
using DbDataKit.Agents;
using DbDataKit.Cli;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Knowledge.Cli;

/// <summary>What the index commands ask of a host (plan 27a KN17).</summary>
public interface IKnowledgeIndexAdmin
{
    /// <summary>The model, counts and progress per channel (every channel the index holds when <paramref name="channel"/> is null).</summary>
    IReadOnlyList<KnowledgeIndexStatus> Status(string? channel);

    /// <summary>Whether the person asking may rebuild (the administrator role).</summary>
    bool MayRebuild { get; }

    /// <summary>Makes the vectors of a channel again; the number of chunks left to embed.</summary>
    Task<int> RebuildAsync(string channel, CancellationToken cancellation);
}

/// <summary>Where a knowledge project came from for <c>knowledge check</c>: the project, and the name agents give it in <c>knowledge.from</c>.</summary>
public sealed record CheckTarget(KnowledgeProject Project, string SourceName);

/// <summary>
/// What a host supplies to the <c>knowledge</c> noun (plan 27a KN16, KN17). A verb whose piece is missing is not offered, so a host with no index has no <c>search</c> and no <c>index</c>.
/// </summary>
public sealed record KnowledgeHost
{
    /// <summary>The projects the host reads by name (<c>install</c>, <c>project</c>); <c>check</c> and <c>orphans</c> read them all when no path is given.</summary>
    public Func<IReadOnlyList<CheckTarget>> Projects { get; init; } = () => [];

    /// <summary>A project in a folder given on the command line (a git folder), or null when the host reads none other than its own.</summary>
    public Func<string, CheckTarget>? FromFolder { get; init; }

    public Func<ISubjectCatalog?> Catalog { get; init; } = () => null;

    public Func<string, bool>? OwnerExists { get; init; }

    /// <summary>The agents whose budget a check measures against.</summary>
    public Func<IReadOnlyList<AgentBudgetInput>>? Agents { get; init; }

    /// <summary>What an agent would be given for what is in play (subject keys), or null when there is no agent of that name.</summary>
    public Func<string, IReadOnlyList<string>, ContextReport?>? Context { get; init; }

    /// <summary>The catalogs of the published semantic models, for <c>undescribed</c>.</summary>
    public Func<CancellationToken, Task<IReadOnlyList<JsonNode>>>? SemanticCatalogs { get; init; }

    public IKnowledgeIndexAdmin? Index { get; init; }

    public IKnowledgeReader? Reader { get; init; }

    /// <summary>The channel the read commands use when none is given.</summary>
    public string DefaultChannel { get; init; } = "prod";

    /// <summary>Publishes a checked project folder to a channel; the result says the version and whether anything changed. Null: the host publishes no knowledge.</summary>
    public Func<CheckTarget, string, string, CancellationToken, Task<(int Version, bool Changed)>>? Publish { get; init; }
}

/// <summary>
/// <c>knowledge check|context|orphans|undescribed|index|search|show|about|publish</c> (plan 27a KN10, KN16, KN17). The read commands replace <c>catalog search</c> and <c>catalog show</c> (plan 27 decision 42).
/// </summary>
public static class KnowledgeNoun
{
    private static readonly string DiagnosticSchema = DataSchemas.Object(
        [("code", DataSchemas.String), ("severity", DataSchemas.String), ("file", DataSchemas.String), ("line", DataSchemas.Integer), ("column", DataSchemas.Integer), ("message", DataSchemas.String), ("fix", DataSchemas.String)],
        "code", "severity", "file", "line", "message");

    private static readonly string ReachSchema = DataSchemas.Object(
        [("agent", DataSchemas.String), ("adds", DataSchemas.Integer), ("used", DataSchemas.Integer), ("budget", DataSchemas.Integer), ("pushes_over", DataSchemas.Boolean), ("items", DataSchemas.StringList)],
        "agent", "adds", "used", "budget", "pushes_over", "items");

    private static readonly string CheckSchema = DataSchemas.Object(
        [("ok", DataSchemas.Boolean), ("projects", DataSchemas.ListOf(DataSchemas.Object([("source", DataSchemas.String), ("root", DataSchemas.String), ("items", DataSchemas.Integer)], "source", "items"))),
            ("diagnostics", DataSchemas.ListOf(DiagnosticSchema)), ("reaches", DataSchemas.ListOf(ReachSchema))], "ok", "projects", "diagnostics", "reaches");

    private static readonly string PartSchema = DataSchemas.Object(
        [("layer", DataSchemas.String), ("id", DataSchemas.String), ("version", DataSchemas.String), ("tokens", DataSchemas.Integer), ("share_of_budget", """{ "type": "number" }""")], "layer", "id", "tokens");

    private static readonly string ContextSchema = DataSchemas.Object(
        [("agent", DataSchemas.String), ("parts", DataSchemas.ListOf(PartSchema)), ("total_tokens", DataSchemas.Integer), ("window", DataSchemas.Integer), ("budget_tokens", DataSchemas.Integer),
            ("over_budget", DataSchemas.Boolean), ("basis", DataSchemas.String), ("in_play", DataSchemas.StringList)], "agent", "parts", "total_tokens", "budget_tokens", "over_budget");

    private static readonly string OrphansSchema = DataSchemas.Object(
        [("orphans", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("file", DataSchemas.String), ("owner", DataSchemas.String), ("subjects", DataSchemas.StringList)], "id", "file", "subjects")))], "orphans");

    private static readonly string UndescribedSchema = DataSchemas.Object(
        [("models", DataSchemas.ListOf(DataSchemas.Object([("model", DataSchemas.String), ("missing", DataSchemas.ListOf(DataSchemas.Object([("kind", DataSchemas.String), ("name", DataSchemas.String)], "kind", "name"))), ("summary", DataSchemas.String)], "model", "missing", "summary")))], "models");

    // status gives the channels; rebuild gives the channel and what is left to embed
    private static readonly string StatusSchema = DataSchemas.Object(
        [("channel", DataSchemas.String), ("remaining", DataSchemas.Integer), ("channels", DataSchemas.ListOf(DataSchemas.Object([("channel", DataSchemas.String), ("items", DataSchemas.Integer), ("chunks", DataSchemas.Integer), ("mode", DataSchemas.String), ("embedded", DataSchemas.Integer), ("model", DataSchemas.String), ("building", DataSchemas.String)], "channel", "items", "chunks", "mode")))]);


    private static readonly string HitSchema = DataSchemas.Object(
        [("id", DataSchemas.String), ("title", DataSchemas.String), ("heading_path", DataSchemas.StringList), ("excerpt", DataSchemas.String), ("source", DataSchemas.String), ("version", DataSchemas.String), ("score", """{ "type": "number" }"""), ("chunks", DataSchemas.Integer)],
        "id", "title", "heading_path", "excerpt", "source", "version", "chunks");

    private static readonly string SearchSchema = DataSchemas.Object(
        [("question", DataSchemas.String), ("channel", DataSchemas.String), ("used", DataSchemas.String), ("note", DataSchemas.String), ("hits", DataSchemas.ListOf(HitSchema))], "question", "channel", "used", "hits");

    private static readonly string ShowSchema = DataSchemas.Object(
        [("id", DataSchemas.String), ("title", DataSchemas.String), ("role", DataSchemas.String), ("owner", DataSchemas.String), ("source", DataSchemas.String), ("version", DataSchemas.String), ("about", DataSchemas.StringList),
            ("section", DataSchemas.String), ("sections", DataSchemas.StringList), ("text", DataSchemas.String)], "id", "title", "role", "source", "version", "about", "sections", "text");

    private static readonly string AboutSchema = DataSchemas.Object(
        [("subject", DataSchemas.String), ("items", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("title", DataSchemas.String), ("role", DataSchemas.String), ("source", DataSchemas.String), ("version", DataSchemas.String)], "id", "title", "role")))], "subject", "items");

    private static readonly string PublishSchema = DataSchemas.Object([("name", DataSchemas.String), ("channel", DataSchemas.String), ("version", DataSchemas.Integer), ("changed", DataSchemas.Boolean)], "name", "channel", "version", "changed");

    public static Noun Create(KnowledgeHost host)
    {
        var verbs = new List<Verb>
        {
            new Verb("check", "Check knowledge: format, subjects, owners and the budgets of the agents each item reaches", EffectClass.OfflineOnly, "[<folder>]", (args, context) => CheckAsync(host, args, context), CheckSchema),
            new Verb("orphans", "Items whose subjects are all gone", EffectClass.OfflineOnly, "", (args, context) => OrphansAsync(host, args, context), OrphansSchema),
        };
        if (host.Context is not null)
            verbs.Add(new Verb("context", "What an agent would be given, in order, with each part's tokens against its budget", EffectClass.OfflineOnly, "<agent> [--in-play <subject> ...]", (args, context) => ContextAsync(host, args, context), ContextSchema));
        if (host.SemanticCatalogs is not null)
            verbs.Add(new Verb("undescribed", "Measures, dimensions and attributes that have no description", EffectClass.OfflineOnly, "", (args, context) => UndescribedAsync(host, args, context), UndescribedSchema));
        if (host.Index is not null)
            verbs.Add(new Verb("index", "The index: status per channel, or rebuild a channel's vectors (administrators)", EffectClass.TrackingTablesOnly, "status|rebuild [--channel <channel>]", (args, context) => IndexAsync(host, args, context), StatusSchema, Traits.ReadsDefinitions | Traits.WritesData));
        if (host.Reader is not null)
        {
            verbs.Add(new Verb("search", "Find knowledge by its words or its meaning, as the caller sees it", EffectClass.OfflineOnly, "<text> [--about <subject>] [--kind reference|instruction|guardrail] [--channel <channel>]", (args, context) => SearchAsync(host, args, context), SearchSchema));
            verbs.Add(new Verb("show", "One item, or one section of it", EffectClass.OfflineOnly, "<id> [--section <heading>] [--version <version>] [--channel <channel>]", (args, context) => ShowAsync(host, args, context), ShowSchema));
            verbs.Add(new Verb("about", "Items about a subject (a model, a measure, a project), as the caller sees them", EffectClass.OfflineOnly, "<subject> [--channel <channel>]", (args, context) => AboutAsync(host, args, context), AboutSchema));
        }
        if (host.Publish is not null)
            verbs.Add(new Verb("publish", "Check a knowledge project folder and publish it as one version to a channel", EffectClass.RepoFilesOnly, "<folder> [--to <channel>] [--name <name>]", (args, context) => PublishAsync(host, args, context), PublishSchema));
        return new Noun("knowledge", "What the install knows about itself: check, search, show and publish knowledge for people and agents", [.. verbs]);
    }

    // ---- check ----

    private static async Task<int> CheckAsync(KnowledgeHost host, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>());
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 1) return await Usage(context, "knowledge check [<folder>]");
        IReadOnlyList<CheckTarget> targets;
        if (parsed.Words.Count == 1)
        {
            if (host.FromFolder is null) return await Fail(context, "This host reads no folder given on the command line.");
            targets = [host.FromFolder(parsed.Words[0])];
        }
        else targets = host.Projects();
        if (targets.Count == 0) return await Fail(context, "There is no knowledge project to check. Name a folder.");

        var agents = host.Agents?.Invoke() ?? [];
        var all = new List<ConfigDiagnostic>();
        var reaches = new List<AgentReach>();
        var projects = new List<object>();
        foreach (var target in targets)
        {
            var report = KnowledgeChecks.Check(target.Project, target.SourceName, host.Catalog(), host.OwnerExists, agents);
            all.AddRange(report.Diagnostics);
            reaches.AddRange(report.Reaches);
            projects.Add(new Dictionary<string, object?> { ["source"] = target.SourceName, ["root"] = target.Project.Root, ["items"] = target.Project.Items.Count });
        }
        var ok = !all.HasErrors();
        context.Data["ok"] = ok;
        context.Data["projects"] = projects;
        context.Data["diagnostics"] = all.Select(DiagnosticDocument).ToList();
        context.Data["reaches"] = reaches.Select(r => new Dictionary<string, object?> { ["agent"] = r.Agent, ["adds"] = r.Adds, ["used"] = r.Used, ["budget"] = r.Budget, ["pushes_over"] = r.PushesOver, ["items"] = r.Items }).ToList();
        foreach (var d in all) await (d.Severity == ConfigSeverity.Error ? context.Error : context.Out).WriteLineAsync(d.ToString());
        foreach (var r in reaches) await context.Out.WriteLineAsync($"{r.Agent}: the project adds {r.Adds} tokens to {r.Used} of {r.Budget}{(r.PushesOver ? ", over its budget" : "")}");
        await context.Out.WriteLineAsync(ok ? $"{targets.Sum(t => t.Project.Items.Count)} item(s) checked, {all.Count(d => d.Severity == ConfigSeverity.Warning)} warning(s)." : $"{all.Count(d => d.Severity == ConfigSeverity.Error)} error(s).");
        return ok ? CliExit.Ok : CliExit.Failure;
    }

    private static Dictionary<string, object?> WithOwner(this Dictionary<string, object?> document, string? owner)
    {
        if (owner is not null) document["owner"] = owner;
        return document;
    }

    private static Dictionary<string, object?> DiagnosticDocument(ConfigDiagnostic d)
    {
        var doc = new Dictionary<string, object?> { ["code"] = d.Code, ["severity"] = d.Severity.ToString().ToLowerInvariant(), ["file"] = d.Path, ["line"] = d.Line, ["column"] = d.Column, ["message"] = d.Message };
        if (d.Fix is not null) doc["fix"] = d.Fix;
        return doc;
    }

    // ---- context ----

    private static async Task<int> ContextAsync(KnowledgeHost host, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args.Where(a => a != "--in-play"), new HashSet<string>(), new HashSet<string>());
        if (parsed.Unknown.Count > 0 || parsed.Words.Count < 1) return await Usage(context, "knowledge context <agent> [--in-play <subject> ...]");
        var inPlay = parsed.Words.Skip(1).ToList();
        if (inPlay.FirstOrDefault(s => KnowledgeSubject.TryParse(s) is null) is { } bad) return await Fail(context, $"'{bad}' is not a subject. Write it as type:name, for example semantic-model:Sales or semantic-model:Sales/measure:revenue.");
        if (host.Context!(parsed.Words[0], inPlay) is not { } report) return await Fail(context, $"There is no agent named '{parsed.Words[0]}' that reads.");
        context.Data["agent"] = report.Agent;
        context.Data["parts"] = report.Shares.Select(p => new Dictionary<string, object?> { ["layer"] = p.Layer, ["id"] = p.Id, ["version"] = p.Version, ["tokens"] = p.Tokens, ["share_of_budget"] = Math.Round(p.ShareOfBudget, 4) }).ToList();
        context.Data["total_tokens"] = report.TotalTokens;
        context.Data["window"] = report.Window;
        context.Data["budget_tokens"] = report.BudgetTokens;
        context.Data["over_budget"] = report.OverBudget;
        context.Data["basis"] = report.Basis;
        context.Data["in_play"] = report.InPlay.ToList();
        await context.Out.WriteLineAsync($"{report.Agent} is given, in this order:");
        foreach (var p in report.Shares) await context.Out.WriteLineAsync($"  {p.Layer,-18} {p.Id,-40} {p.Tokens,6} tokens {p.ShareOfBudget:P0} of the budget");
        await context.Out.WriteLineAsync($"{report.TotalTokens} tokens against a budget of {report.BudgetTokens} (window {report.Window}; counted with {report.Basis}){(report.OverBudget ? ", OVER" : "")}.");
        return CliExit.Ok;
    }

    // ---- orphans ----

    private static async Task<int> OrphansAsync(KnowledgeHost host, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>());
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) return await Usage(context, "knowledge orphans");
        if (host.Catalog() is not { } catalog) return await Fail(context, "This host has no catalog of what exists, so nothing can be said to be gone.");
        var orphans = host.Projects().SelectMany(p => SubjectResolver.Orphans(p.Project.Items, catalog)).ToList();
        context.Data["orphans"] = orphans.Select(i => new Dictionary<string, object?> { ["id"] = i.Id, ["file"] = i.File, ["subjects"] = i.About.Select(a => a.Key).ToList() }.WithOwner(i.Owner)).ToList();
        foreach (var i in orphans) await context.Out.WriteLineAsync($"{i.File}: about {string.Join(", ", i.About.Select(a => a.Key))}, all gone{(i.Owner is null ? "" : $" (owner {i.Owner})")}");
        await context.Out.WriteLineAsync($"{orphans.Count} orphaned item(s). An orphaned item reaches nobody until it is pointed at another object or retired.");
        return CliExit.Ok;
    }

    // ---- undescribed ----

    private static async Task<int> UndescribedAsync(KnowledgeHost host, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string>());
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) return await Usage(context, "knowledge undescribed");
        var catalogs = await host.SemanticCatalogs!(context.Cancellation);
        var models = new List<object>();
        foreach (var catalog in catalogs)
        {
            var report = GeneratedKnowledge.Undescribed(catalog);
            if (report.Missing.Count == 0) continue;
            models.Add(new Dictionary<string, object?> { ["model"] = report.Model, ["missing"] = report.Missing.Select(m => new Dictionary<string, object?> { ["kind"] = m.Kind, ["name"] = m.Name }).ToList(), ["summary"] = report.Sentence() });
            await context.Out.WriteLineAsync(report.Sentence());
            foreach (var (kind, name) in report.Missing) await context.Out.WriteLineAsync($"  {kind} {name}");
        }
        context.Data["models"] = models;
        if (models.Count == 0) await context.Out.WriteLineAsync("Everything in the published models has a description.");
        return CliExit.Ok;
    }

    // ---- index ----

    private static async Task<int> IndexAsync(KnowledgeHost host, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "channel" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || parsed.Words[0] is not ("status" or "rebuild")) return await Usage(context, "knowledge index status|rebuild [--channel <channel>]");
        var index = host.Index!;
        if (parsed.Words[0] == "status")
        {
            var rows = index.Status(parsed.Get("channel"));
            context.Data["channels"] = rows.Select(s =>
            {
                var row = new Dictionary<string, object?> { ["channel"] = s.Channel, ["items"] = s.Items, ["chunks"] = s.Chunks, ["mode"] = s.Mode.ToString().ToLowerInvariant(), ["embedded"] = s.Vectors?.Embedded ?? 0 };
                if (s.Vectors?.Ready?.ModelId is { } model) row["model"] = model;
                if (s.Vectors?.Building?.ModelId is { } building) row["building"] = building;
                return row;
            }).ToList();
            foreach (var s in rows)
                await context.Out.WriteLineAsync($"{s.Channel}: {s.Items} items, {s.Chunks} chunks, found by {(s.Vectors?.Ready is { } ready ? $"words and meaning ({ready.ModelId}, {ready.Dimension} dimensions, {s.Vectors.Embedded} of {s.Vectors.Chunks} chunks embedded)" : "words")}"
                    + (s.Vectors?.Building is { } b ? $"; building {b.ModelId}: {s.Vectors.BuildingEmbedded} of {s.Vectors.Chunks}" : "") + (s.Vectors is { Failed: > 0 } v ? $"; {v.Failed} chunk(s) the model refused" : ""));
            if (rows.Count == 0) await context.Out.WriteLineAsync("No channel has an index yet.");
            return CliExit.Ok;
        }
        if (!index.MayRebuild) return await Fail(context, "Rebuilding an index needs the administrator role.");
        var channel = parsed.Get("channel") ?? host.DefaultChannel;
        var remaining = await index.RebuildAsync(channel, context.Cancellation);
        context.Data["channel"] = channel;
        context.Data["remaining"] = remaining;
        await context.Out.WriteLineAsync($"{channel}: vectors rebuilt, {remaining} chunk(s) left to embed.");
        return CliExit.Ok;
    }

    // ---- search, show, about ----

    private static async Task<int> SearchAsync(KnowledgeHost host, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "about", "kind", "channel" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count == 0) return await Usage(context, "knowledge search <text> [--about <subject>] [--kind reference|instruction|guardrail] [--channel <channel>]");
        if (parsed.Get("kind") is { } kind && kind is not ("reference" or "instruction" or "guardrail")) return await Fail(context, "--kind is reference, instruction or guardrail.");
        var channel = parsed.Get("channel") ?? host.DefaultChannel;
        var question = string.Join(' ', parsed.Words);
        var found = await host.Reader!.SearchAsync(question, channel, parsed.Get("about"), parsed.Get("kind"), context.Cancellation);
        context.Data["question"] = question;
        context.Data["channel"] = channel;
        context.Data["used"] = found.Used.ToString().ToLowerInvariant();
        if (found.Note is not null) context.Data["note"] = found.Note;
        context.Data["hits"] = found.Hits.Select(h => new Dictionary<string, object?>
        {
            ["id"] = h.ItemId, ["title"] = h.Title, ["heading_path"] = h.HeadingPath.ToList(), ["excerpt"] = h.Excerpt, ["source"] = h.Source, ["version"] = h.Version, ["score"] = Math.Round(h.Score, 4), ["chunks"] = h.Chunks,
        }).ToList();
        if (found.Note is not null) await context.Out.WriteLineAsync(found.Note);
        foreach (var h in found.Hits)
        {
            await context.Out.WriteLineAsync($"{h.ItemId}  {h.Title}{(h.HeadingPath.Count > 0 ? " > " + string.Join(" > ", h.HeadingPath) : "")}  ({h.Source} {h.Version})");
            await context.Out.WriteLineAsync("  " + h.Excerpt.Replace("\n", "\n  "));
        }
        if (found.Hits.Count == 0) await context.Out.WriteLineAsync("Nothing found that you may see.");
        return CliExit.Ok;
    }

    private static async Task<int> ShowAsync(KnowledgeHost host, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "section", "version", "channel" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) return await Usage(context, "knowledge show <id> [--section <heading>] [--version <version>] [--channel <channel>]");
        var channel = parsed.Get("channel") ?? host.DefaultChannel;
        if (host.Reader!.Show(parsed.Words[0], channel, parsed.Get("section"), parsed.Get("version")) is not { } shown)
            return await Fail(context, $"There is no item '{parsed.Words[0]}'{(parsed.Get("section") is { } s ? $" with a section '{s}'" : "")} in {channel} that you may see.");
        context.Data["id"] = shown.Id;
        context.Data["title"] = shown.Title;
        context.Data["role"] = shown.Role;
        if (shown.Owner is not null) context.Data["owner"] = shown.Owner;
        context.Data["source"] = shown.Source;
        context.Data["version"] = shown.Version;
        context.Data["about"] = shown.About.ToList();
        if (shown.Section is not null) context.Data["section"] = shown.Section;
        context.Data["sections"] = shown.Sections.ToList();
        context.Data["text"] = shown.Text;
        await context.Out.WriteLineAsync($"{shown.Title} ({shown.Role}, {shown.Source} {shown.Version})");
        await context.Out.WriteLineAsync(shown.Text);
        return CliExit.Ok;
    }

    private static async Task<int> AboutAsync(KnowledgeHost host, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "channel" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) return await Usage(context, "knowledge about <subject> [--channel <channel>]");
        if (KnowledgeSubject.TryParse(parsed.Words[0]) is not { } subject) return await Fail(context, $"'{parsed.Words[0]}' is not a subject. Write it as type:name, for example semantic-model:Sales.");
        var items = host.Reader!.About(subject.Key, parsed.Get("channel") ?? host.DefaultChannel);
        context.Data["subject"] = subject.Key;
        context.Data["items"] = items.Select(i => new Dictionary<string, object?> { ["id"] = i.Id, ["title"] = i.Title, ["role"] = i.Role, ["source"] = i.Source, ["version"] = i.Version }).ToList();
        foreach (var i in items) await context.Out.WriteLineAsync($"{i.Id}  {i.Title} ({i.Role}, {i.Source} {i.Version})");
        if (items.Count == 0) await context.Out.WriteLineAsync($"Nothing is known about {subject.Key} that you may see.");
        return CliExit.Ok;
    }

    // ---- publish ----

    private static async Task<int> PublishAsync(KnowledgeHost host, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "to", "name" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1 || host.FromFolder is null) return await Usage(context, "knowledge publish <folder> [--to <channel>] [--name <name>]");
        var target = host.FromFolder(parsed.Words[0]);
        var channel = parsed.Get("to") ?? host.DefaultChannel;
        var name = parsed.Get("name") ?? target.SourceName;
        try
        {
            var (version, changed) = await host.Publish!(target, name, channel, context.Cancellation);
            context.Data["name"] = name;
            context.Data["channel"] = channel;
            context.Data["version"] = version;
            context.Data["changed"] = changed;
            await context.Out.WriteLineAsync(changed ? $"Published {name} v{version} to {channel}." : $"{name} is unchanged: v{version} is already what {channel} has.");
            return CliExit.Ok;
        }
        catch (KnowledgePublishRefusedException refused)
        {
            foreach (var d in refused.Problems) await context.Error.WriteLineAsync(d.ToString());
            await context.Error.WriteLineAsync("Nothing was published.");
            return CliExit.Failure;
        }
    }

    private static async Task<int> Usage(CliContext context, string usage)
    {
        await context.Error.WriteLineAsync($"Usage: {usage}");
        return CliExit.Usage;
    }

    private static async Task<int> Fail(CliContext context, string message)
    {
        await context.Error.WriteLineAsync(message);
        return CliExit.Failure;
    }
}
