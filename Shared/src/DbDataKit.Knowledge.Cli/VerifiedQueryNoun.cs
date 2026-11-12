using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.State;

namespace DbDataKit.Knowledge.Cli;

/// <summary>A verified query as a person or an agent sees it (plan 27a KN21): the questions it answers, the pinned request, who verified it against which version of the model, and whether it still holds.</summary>
public sealed record VerifiedQueryView(string Id, string Title, string Model, IReadOnlyList<string> Questions, string? Request, string? SavedQuery, string? SavedVersion, string? SemanticSql,
    string VerifiedBy, string VerifiedAt, string ModelVersion, string Status, string? StatusNote, string Source, string Version)
{
    public bool NeedsReverification => Status == VerifiedStatus.NeedsReverification;
}

/// <summary>What a served model answered: columns, rows, whether there were more and the server's notes.</summary>
public sealed record VerifiedAnswer(IReadOnlyList<(string Name, string Type)> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, bool Truncated, IReadOnlyDictionary<string, string> Notes);

/// <summary>What <c>query run-verified</c> asks of a host: the verified queries the caller may see, and a way to run a statement on a model <b>as the caller</b> (the caller's own rules apply; the command adds none).</summary>
public interface IVerifiedQueryReader
{
    /// <summary>The query, or null when there is none or the caller may not see it (the two are not told apart).</summary>
    VerifiedQueryView? Find(string id, string channel);

    IReadOnlyList<VerifiedQueryView> List(string channel, string? model);

    /// <summary>Runs a statement (a request in JSON, or semantic SQL) on a model, as the caller.</summary>
    Task<VerifiedAnswer> RunAsync(string model, string statement, int maxRows, CancellationToken cancellation);

    /// <summary>The version the model is at now, or null when the host does not know it.</summary>
    string? CurrentModelVersion(string model);
}

/// <summary>The verified queries of a channel's index as the caller sees them (KN21): an item the caller may not see is not found.</summary>
public sealed class StateVerifiedQueryReader(Func<string, StateKnowledgeIndex> index, IKnowledgeCaller caller, ISubjectCatalog catalog, Func<string, string, int, CancellationToken, Task<VerifiedAnswer>> run, Func<string, string?>? modelVersion = null) : IVerifiedQueryReader
{
    public VerifiedQueryView? Find(string id, string channel)
    {
        var store = index(channel).Store;
        if (store.Item(channel, id) is not { } stored || !KnowledgeAccess.Visible(StateKnowledgeIndex.ItemOf(stored), caller, catalog)) return null;
        return store.Verified(channel, id) is { } verified ? View(store, channel, stored, verified) : null;
    }

    public IReadOnlyList<VerifiedQueryView> List(string channel, string? model)
    {
        var store = index(channel).Store;
        var views = new List<VerifiedQueryView>();
        foreach (var (itemId, verified) in store.VerifiedQueries(channel, model))
            if (store.Item(channel, itemId) is { } stored && KnowledgeAccess.Visible(StateKnowledgeIndex.ItemOf(stored), caller, catalog)) views.Add(View(store, channel, stored, verified));
        return views;
    }

    public Task<VerifiedAnswer> RunAsync(string model, string statement, int maxRows, CancellationToken cancellation) => run(model, statement, maxRows, cancellation);

    public string? CurrentModelVersion(string model) => modelVersion?.Invoke(model);

    // the questions are the item's chunks, one per line as "- question"
    private static VerifiedQueryView View(KnowledgeStore store, string channel, StoredItem stored, StoredVerified verified)
    {
        var questions = store.Chunks(channel, stored.Id).SelectMany(c => c.Text.Split('\n')).Select(l => l.Trim()).Where(l => l.StartsWith("- ", StringComparison.Ordinal)).Select(l => l[2..]).ToList();
        return new VerifiedQueryView(stored.Id, stored.Title, verified.Model, questions, verified.Request, verified.SavedQuery, verified.SavedVersion, verified.SemanticSql, verified.VerifiedBy, verified.VerifiedAt, verified.ModelVersion, verified.Status, verified.StatusNote, stored.Source, stored.Version);
    }
}

/// <summary>
/// <c>query run-verified &lt;id&gt; [--param name=value ...]</c> and <c>query list-verified [--model]</c> (plan 27a KN21). A verified query is run <b>as the caller</b>: two people who run the same query get what each may see. The pinned request is what runs
/// (the request is the truth, the semantic SQL is for reading); a query marked <c>needs-reverification</c> (KN22) is still found and listed but is not run as verified. The answer says which query ran, at which version, and who verified it, so a turn can record it.
/// </summary>
public static class VerifiedQueryNoun
{
    public const int DefaultMaxRows = 100;

    private static readonly string ViewSchema = DataSchemas.Object(
        [("id", DataSchemas.String), ("title", DataSchemas.String), ("model", DataSchemas.String), ("questions", DataSchemas.StringList), ("verified_by", DataSchemas.String), ("verified_at", DataSchemas.String),
            ("model_version", DataSchemas.String), ("status", DataSchemas.String), ("status_note", DataSchemas.String), ("source", DataSchemas.String), ("version", DataSchemas.String)],
        "id", "title", "model", "questions", "verified_by", "verified_at", "model_version", "status");

    private static readonly string ListSchema = DataSchemas.Object([("queries", DataSchemas.ListOf(ViewSchema))], "queries");

    private static readonly string RunSchema = DataSchemas.Object(
    [
        ("verified_query", ViewSchema),
        ("model", DataSchemas.String),
        ("columns", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("type", DataSchemas.String)], "name", "type"))),
        ("rows", DataSchemas.ListOf("""{ "type": "array" }""")),
        ("row_count", DataSchemas.Integer),
        ("truncated", DataSchemas.Boolean),
        ("note", DataSchemas.String),
        ("notes", """{ "type": "object", "additionalProperties": { "type": "string" } }"""),
    ], "verified_query", "model", "columns", "rows", "row_count", "truncated");

    public static Noun Create(IVerifiedQueryReader reader, string defaultChannel = "prod") =>
        new("query", "Questions the install has verified answers for: list them, run one as yourself",
        [
            new Verb("list-verified", "The verified queries you may see, for a model if given, and whether each still holds", EffectClass.OfflineOnly, "[--model <model>] [--channel <channel>]", (args, context) => ListAsync(reader, defaultChannel, args, context), ListSchema),
            new Verb("run-verified", "Run a verified query as yourself, with parameter values if it takes any; your own access rules apply", EffectClass.OfflineOnly, "<id> [--param <name>=<value> ...] [--max-rows N] [--channel <channel>]",
                (args, context) => RunAsync(reader, defaultChannel, args, context), RunSchema, Traits.ReadsDefinitions | Traits.ReadsData),
        ]);

    private static Dictionary<string, object?> Document(VerifiedQueryView v)
    {
        var d = new Dictionary<string, object?>
        {
            ["id"] = v.Id, ["title"] = v.Title, ["model"] = v.Model, ["questions"] = v.Questions.ToList(), ["verified_by"] = v.VerifiedBy, ["verified_at"] = v.VerifiedAt, ["model_version"] = v.ModelVersion, ["status"] = v.Status,
            ["source"] = v.Source, ["version"] = v.Version,
        };
        if (v.StatusNote is not null) d["status_note"] = v.StatusNote;
        return d;
    }

    private static async Task<int> ListAsync(IVerifiedQueryReader reader, string defaultChannel, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "model", "channel" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0) return await Usage(context, "query list-verified [--model <model>] [--channel <channel>]");
        var list = reader.List(parsed.Get("channel") ?? defaultChannel, parsed.Get("model"));
        context.Data["queries"] = list.Select(Document).ToList();
        foreach (var v in list)
            await context.Out.WriteLineAsync($"{v.Id}  {v.Title} ({v.Model}; verified by {v.VerifiedBy} on {v.VerifiedAt} against v{v.ModelVersion})" + (v.NeedsReverification ? $"  NEEDS RE-VERIFICATION: {v.StatusNote}" : ""));
        if (list.Count == 0) await context.Out.WriteLineAsync("No verified query that you may see.");
        return CliExit.Ok;
    }

    private static async Task<int> RunAsync(IVerifiedQueryReader reader, string defaultChannel, string[] args, CliContext context)
    {
        // --param may repeat: CliArgs keeps the last value of an option, so the parameters are taken from the arguments before parsing
        var (parameterArgs, rest) = SplitParameters(args);
        var parsed = CliArgs.Parse(rest, new HashSet<string>(), new HashSet<string> { "max-rows", "channel" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != 1) return await Usage(context, "query run-verified <id> [--param <name>=<value> ...] [--max-rows N] [--channel <channel>]");
        var maxRows = DefaultMaxRows;
        if (parsed.Get("max-rows") is { } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out maxRows) || maxRows < 1 || maxRows > 10_000)) return await Fail(context, "--max-rows is a whole number from 1 to 10000.");
        var values = new JsonObject();
        foreach (var arg in parameterArgs)
        {
            var at = arg.IndexOf('=', StringComparison.Ordinal);
            if (at < 1) return await Fail(context, $"--param is name=value; '{arg}' is not.");
            values[arg[..at]] = Value(arg[(at + 1)..]);
        }

        var channel = parsed.Get("channel") ?? defaultChannel;
        if (reader.Find(parsed.Words[0], channel) is not { } query) return await Fail(context, $"There is no verified query '{parsed.Words[0]}' in {channel} that you may see.");
        if (query.NeedsReverification)
            return await Fail(context, $"'{query.Id}' needs re-verification and is not run as verified: {query.StatusNote} Ask its owner to verify it again.");

        string statement;
        try { statement = Statement(query, values); }
        catch (VerifiedQueryException problem) { return await Fail(context, problem.Message); }

        var notes = new List<string>();
        if (reader.CurrentModelVersion(query.Model) is { } now && now != query.ModelVersion)
            notes.Add($"It was verified against version {query.ModelVersion} of {query.Model}, which is now at version {now}.");
        if (query.SemanticSql is not null && query.Request is not null) notes.Add("The pinned request ran; the semantic SQL is kept for reading.");

        VerifiedAnswer answer;
        try { answer = await reader.RunAsync(query.Model, statement, maxRows, context.Cancellation); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return await Fail(context, ex.InnerException?.Message ?? ex.Message); }

        context.Out.WriteLine(string.Join(" | ", answer.Columns.Select(c => c.Name)));
        foreach (var row in answer.Rows) context.Out.WriteLine(string.Join(" | ", row.Select(v => v is null ? "NULL" : Convert.ToString(v, CultureInfo.InvariantCulture))));
        context.Out.WriteLine($"({answer.Rows.Count} rows{(answer.Truncated ? ", more were left out" : "")}; verified query {query.Id} v{query.Version}, verified by {query.VerifiedBy} on {query.VerifiedAt})");
        foreach (var note in notes) context.Out.WriteLine(note);
        // the document names the query and its version, so the record of a turn (the tool call and what it answered) says which verified query was used
        context.Data["verified_query"] = Document(query);
        context.Data["model"] = query.Model;
        context.Data["columns"] = answer.Columns.Select(c => new Dictionary<string, object?> { ["name"] = c.Name, ["type"] = c.Type }).ToList();
        context.Data["rows"] = answer.Rows.Select(r => (object?)r.ToList()).ToList();
        context.Data["row_count"] = answer.Rows.Count;
        context.Data["truncated"] = answer.Truncated;
        if (notes.Count > 0) context.Data["note"] = string.Join(" ", notes);
        if (answer.Notes.Count > 0) context.Data["notes"] = answer.Notes.ToDictionary(n => n.Key, n => (object?)n.Value);
        return CliExit.Ok;
    }

    private sealed class VerifiedQueryException(string message) : Exception(message);

    /// <summary>The statement a verified query runs: its pinned request with the values as <c>parameters</c>, or <c>SELECT * FROM saved(name := value)</c> for a saved query of the model.</summary>
    internal static string Statement(VerifiedQueryView query, JsonObject values)
    {
        if (query.SavedQuery is { } saved)
        {
            var arguments = string.Join(", ", values.Select(v => $"{v.Key} := {Literal(v.Value)}"));
            return $"SELECT * FROM {saved}" + (arguments.Length > 0 ? $"({arguments})" : "");
        }
        if (query.Request is null) throw new VerifiedQueryException($"'{query.Id}' has no request to run.");
        var request = JsonNode.Parse(query.Request)!.AsObject();
        var declared = Parameters(request).ToList();
        foreach (var given in values.Select(v => v.Key).Where(k => !declared.Contains(k, StringComparer.Ordinal)))
            throw new VerifiedQueryException(declared.Count == 0 ? $"'{query.Id}' takes no parameters; '{given}' is not one." : $"'{query.Id}' has no parameter '{given}'. It takes: {string.Join(", ", declared)}.");
        var existing = request["parameters"] as JsonObject;
        var missing = declared.Where(name => !values.ContainsKey(name) && existing?[name] is null).ToList();
        if (missing.Count > 0) throw new VerifiedQueryException($"'{query.Id}' needs a value for: {string.Join(", ", missing)}. Give them with --param name=value.");
        if (values.Count > 0)
        {
            var merged = existing is null ? new JsonObject() : (JsonObject)existing.DeepClone();
            foreach (var v in values) merged[v.Key] = v.Value?.DeepClone();
            request["parameters"] = merged;
        }
        return request.ToJsonString();
    }

    /// <summary>The names of the parameters a request uses: every <c>{"param": name}</c> in it, in order.</summary>
    internal static IEnumerable<string> Parameters(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject { Count: 1 } o when o["param"] is JsonValue p && p.TryGetValue<string>(out var name):
                yield return name;
                break;
            case JsonObject o:
                foreach (var (key, child) in o)
                    if (key != "parameters")
                        foreach (var found in Parameters(child)) yield return found;
                break;
            case JsonArray a:
                foreach (var child in a)
                    foreach (var found in Parameters(child)) yield return found;
                break;
        }
    }

    private static JsonNode? Value(string text)
    {
        try { return JsonNode.Parse(text) is { } parsed && parsed is JsonValue or JsonArray ? parsed : JsonValue.Create(text); }
        catch (JsonException) { return JsonValue.Create(text); }
    }

    private static string Literal(JsonNode? value) => value switch
    {
        null => "NULL",
        JsonValue v when v.TryGetValue<string>(out var s) => "'" + s.Replace("'", "''", StringComparison.Ordinal) + "'",
        _ => value.ToJsonString(),
    };

    private static (List<string> Parameters, string[] Others) SplitParameters(string[] args)
    {
        var parameters = new List<string>();
        var rest = new List<string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--param" && i + 1 < args.Length) parameters.Add(args[++i]);
            else if (args[i].StartsWith("--param=", StringComparison.Ordinal)) parameters.Add(args[i]["--param=".Length..]);
            else rest.Add(args[i]);
        }
        return (parameters, [.. rest]);
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
