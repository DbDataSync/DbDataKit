using DbDataKit.Cli;
using System.Globalization;
using System.Text.Json.Nodes;

namespace DbDataKit.Semantic.Cli;

/// <summary>
/// The <c>semantic</c> noun (plan 19, J15): <c>describe</c> what a served model offers, <c>query</c> it, <c>explain</c> what a query would run and <c>members</c> of an attribute. Written once; a product mounts it with the way it connects.
/// Every verb answers a document whose <c>data</c> is closed and described by a schema, so an agent can rely on its shape.
/// </summary>
public static class SemanticNoun
{
    public const int DefaultMaxRows = 100;
    public const int MaxRowsCap = 10_000;

    private static readonly string TableSchema = DataSchemas.Object(
    [
        ("model", DataSchemas.String),
        ("columns", DataSchemas.ListOf(DataSchemas.Object([("name", DataSchemas.String), ("type", DataSchemas.String)], "name", "type"))),
        ("rows", DataSchemas.ListOf("""{ "type": "array" }""")),
        ("row_count", DataSchemas.Integer),
        ("truncated", DataSchemas.Boolean),
        ("notes", """{ "type": "object", "additionalProperties": { "type": "string" } }"""),
    ], "model", "columns", "rows", "row_count", "truncated");

    /// <param name="connect">How the product opens a session.</param>
    /// <param name="connectionOptions">The options with a value that <paramref name="connect"/> reads (<c>address</c>, <c>user</c>, ...).</param>
    /// <param name="connectionFlags">The options without a value that it reads (<c>insecure</c>).</param>
    /// <param name="connectionUsage">The part of each verb's usage that names them, such as <c>--address URL --user U</c>.</param>
    public static Noun Create(SemanticConnect connect, IEnumerable<string> connectionOptions, IEnumerable<string> connectionFlags, string connectionUsage)
    {
        var valued = new HashSet<string>(connectionOptions) { "sql", "request", "max-rows", "search", "limit" };
        var flags = new HashSet<string>(connectionFlags);
        return new Noun("semantic", "A served semantic model: what it offers, a query on it, what a query would run, the values of an attribute",
        [
            new Verb("describe", "What the model offers: its dimensions, attributes, measures, hierarchies and saved queries", EffectClass.OfflineOnly,
                $"<model> {connectionUsage}", (args, context) => Run(args, context, valued, flags, connect, 1, "describe", (words, parsed) => ("""{"kind": "describe"}""", words[0], null)), TableSchema),
            new Verb("query", "Run a semantic query: semantic SQL (--sql) or a request (--request, JSON); the answer is read as the caller, at most --max-rows rows", EffectClass.OfflineOnly,
                $"<model> (--sql \"...\" | --request '{{...}}') [--max-rows N] {connectionUsage}", (args, context) => Run(args, context, valued, flags, connect, 1, "query", Query), TableSchema),
            new Verb("explain", "What a query would run on the sources, one row per scan, without running it", EffectClass.OfflineOnly,
                $"<model> (--sql \"...\" | --request '{{...}}') {connectionUsage}", (args, context) => Run(args, context, valued, flags, connect, 1, "explain", Explain), TableSchema),
            new Verb("members", "The values an attribute takes, to choose a parameter's value from", EffectClass.OfflineOnly,
                $"<model> <attribute> [--search TEXT] [--limit N] {connectionUsage}", (args, context) => Run(args, context, valued, flags, connect, 2, "members", Members), TableSchema),
        ]);
    }

    private static (string Statement, string Model, string? Problem) Query(IReadOnlyList<string> words, CliArgs parsed)
    {
        if (parsed.Get("sql") is { Length: > 0 } sql) return parsed.Get("request") is null ? (sql, words[0], null) : ("", words[0], "give --sql or --request, not both");
        return parsed.Get("request") is { Length: > 0 } request ? (request, words[0], null) : ("", words[0], "give --sql or --request");
    }

    private static (string Statement, string Model, string? Problem) Explain(IReadOnlyList<string> words, CliArgs parsed)
    {
        if (parsed.Get("sql") is { Length: > 0 } sql) return parsed.Get("request") is null ? ("EXPLAIN " + sql, words[0], null) : ("", words[0], "give --sql or --request, not both");
        if (parsed.Get("request") is not { Length: > 0 } request) return ("", words[0], "give --sql or --request");
        try
        {
            var document = JsonNode.Parse(request) as JsonObject ?? throw new FormatException();
            document["kind"] = "explain";
            return (document.ToJsonString(), words[0], null);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or FormatException) { return ("", words[0], "--request is not a JSON object"); }
    }

    private static (string Statement, string Model, string? Problem) Members(IReadOnlyList<string> words, CliArgs parsed)
    {
        var document = new JsonObject { ["kind"] = "members", ["attribute"] = words[1] };
        if (parsed.Get("search") is { Length: > 0 } search) document["search"] = search;
        if (parsed.Get("limit") is { } limit)
        {
            if (!int.TryParse(limit, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n < 1) return ("", words[0], "--limit is a whole number of at least 1");
            document["limit"] = n;
        }
        return (document.ToJsonString(), words[0], null);
    }

    private static async Task<int> Run(string[] args, CliContext context, HashSet<string> valued, HashSet<string> flags, SemanticConnect connect, int words, string verb,
        Func<IReadOnlyList<string>, CliArgs, (string Statement, string Model, string? Problem)> statement)
    {
        var parsed = CliArgs.Parse(args, flags, valued);
        if (parsed.Unknown.Count > 0 || parsed.Words.Count != words)
        {
            await context.Error.WriteLineAsync($"Usage: semantic {verb} {(words == 2 ? "<model> <attribute>" : "<model>")} ... (see the verb's usage)");
            return CliExit.Usage;
        }
        var maxRows = DefaultMaxRows;
        if (parsed.Get("max-rows") is { } text && (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out maxRows) || maxRows < 1 || maxRows > MaxRowsCap))
        {
            await context.Error.WriteLineAsync($"--max-rows is a whole number from 1 to {MaxRowsCap}.");
            return CliExit.Usage;
        }
        var (text2, model, problem) = statement(parsed.Words, parsed);
        if (problem is not null) { await context.Error.WriteLineAsync($"semantic {verb}: {problem}."); return CliExit.Usage; }

        try
        {
            await using var session = await connect(parsed, context);
            var table = await session.RunAsync(model, text2, maxRows, context.Cancellation);
            Write(context, model, table);
            return CliExit.Ok;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await context.Error.WriteLineAsync(ex.InnerException?.Message ?? ex.Message);
            return CliExit.Failure;
        }
    }

    private static void Write(CliContext context, string model, SemanticTable table)
    {
        context.Out.WriteLine(string.Join(" | ", table.Columns.Select(c => c.Name)));
        foreach (var row in table.Rows) context.Out.WriteLine(string.Join(" | ", row.Select(v => v is null ? "NULL" : Convert.ToString(v, CultureInfo.InvariantCulture))));
        context.Out.WriteLine($"({table.Rows.Count} rows{(table.Truncated ? ", more were left out" : "")})");
        context.Data["model"] = model;
        context.Data["columns"] = table.Columns.Select(c => new Dictionary<string, object?> { ["name"] = c.Name, ["type"] = c.Type }).ToList();
        context.Data["rows"] = table.Rows.Select(r => (object?)r.ToList()).ToList();
        context.Data["row_count"] = table.Rows.Count;
        context.Data["truncated"] = table.Truncated;
        if (table.Notes.Count > 0) context.Data["notes"] = table.Notes.ToDictionary(n => n.Key, n => (object?)n.Value);
    }
}
