using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Cli;
using DbDataKit.Hosting.Local;
using DbDataGateway.Server.Configuration;

namespace DbDataGateway.Server.Hosting;

/// <summary>
/// <c>dbdatagateway access explain|coverage</c> (plan 22a GP15): why a person or group can or cannot read a model's or entity's rows and columns, and the values no rule row covers; both ask the running gateway's resolver, the one the reads use.
/// <c>dbdatagateway access mask-key-status|mask-key-rotate</c> (plan 22a GP13, owner decision 2026-11-12): the key of the hash mask of a column rule is the group's, made on first use. <c>rotate</c> replaces it, which deliberately changes every hash
/// (<c>POST /api/v1/access/mask-key/rotate</c>, audited, the result cache cleared), made over the local channel of the running gateway on this machine: the operating system decided who may reach the channel, so the caller counts as an administrator
/// and the audit line names the operating-system user. The key's value is never shown.
/// </summary>
public static class AccessCommand
{
    private const string Where = "[--repo <data directory>]";

    private const string Which = "--model <model or entity model> [--dataset <table or entity>]";

    public static Noun Noun() => new("access", "The access rules: why a person can or cannot see something, the values no rule covers, and the key of the hash mask",
    [
        new Verb("explain", "Why a person or group can or cannot read a model or entity: the names the rules see, the rules that apply and the rows of the rules table that matched, whether a given row is included and which rule decided, the column rules and the treatment that wins, the state of each rules table, and the warnings; the running gateway must have its local channel on",
            EffectClass.OfflineOnly, $"<person or group> {Which} [--kind person|group] [--row <column>=<value> [<column>=<value> ...]] {Where}",
            ExplainAsync, ExplainSchema),
        new Verb("coverage", "The values of a filtered dataset that no row of a rule's rules table covers, NULL counted, per rule (nobody a rule would include reads those rows); the running gateway must have its local channel on",
            EffectClass.OfflineOnly, $"{Which} {Where}", CoverageAsync, CoverageSchema),
        new Verb("mask-key-status", "Has the group made its key for hash masks yet (the value is never shown); the running gateway must have its local channel on", EffectClass.OfflineOnly, Where,
            (args, context) => CallAsync("GET", "/api/v1/access/mask-key", args, context), DataSchemas.Object([("exists", DataSchemas.Boolean)], "exists")),
        new Verb("mask-key-rotate", "Replace the group's key for hash masks with a new one: every hash mask changes, and the result cache is cleared; audited", EffectClass.TargetWrites, Where,
            (args, context) => CallAsync("POST", "/api/v1/access/mask-key/rotate", args, context), DataSchemas.Object([("rotated", DataSchemas.Boolean), ("cleared", DataSchemas.Integer)], "rotated", "cleared")),
    ]);

    private static readonly string Row = DataSchemas.Object([("dataset_column", DataSchemas.String), ("rules_column", DataSchemas.String)], "dataset_column", "rules_column");
    private const string Values = """{ "type": "object", "additionalProperties": { "type": ["string", "null"] } }""";
    private const string Nullable = """{ "type": ["string", "null"] }""";
    private const string NullableBoolean = """{ "type": ["boolean", "null"] }""";
    private const string NullableInteger = """{ "type": ["integer", "null"] }""";

    private static readonly string TableSchema = DataSchemas.Object([("table", DataSchemas.String), ("key", DataSchemas.String), ("state", DataSchemas.String), ("age_seconds", DataSchemas.Integer), ("max_age_seconds", DataSchemas.Integer),
        ("counted_from", DataSchemas.String), ("last_changed", Nullable)], "table", "key", "state", "age_seconds", "max_age_seconds", "counted_from");

    private static readonly string RuleSchema = DataSchemas.Object([("id", DataSchemas.String), ("effect", DataSchemas.String), ("table", DataSchemas.String), ("subject", DataSchemas.String), ("subject_kind", DataSchemas.String),
        ("columns", DataSchemas.ListOf(Row)), ("names", DataSchemas.StringList),
        ("rows", DataSchemas.ListOf(DataSchemas.Object([("subject", DataSchemas.String), ("values", Values)], "subject", "values"))), ("rows_truncated", DataSchemas.Boolean), ("holds", NullableBoolean), ("read_problem", Nullable)],
        "id", "effect", "table", "subject", "subject_kind", "columns", "names", "rows", "rows_truncated");

    private static readonly string DatasetSchema = DataSchemas.Object([("dataset", DataSchemas.String), ("match", DataSchemas.String), ("refusal", Nullable), ("summary", DataSchemas.String),
        ("groups", DataSchemas.ListOf(DataSchemas.Object([("id", DataSchemas.String), ("match", DataSchemas.String), ("included", NullableBoolean), ("rules", DataSchemas.ListOf(RuleSchema))], "id", "match", "rules"))),
        ("verdict", """{ "type": ["object", "null"], "additionalProperties": false, "properties": { "included": { "type": "boolean" }, "decided_by": { "type": "array", "items": { "type": "string" } }, "text": { "type": "string" }, "row": { "type": "object", "additionalProperties": { "type": ["string", "null"] } } } }"""),
        ("missing_columns", DataSchemas.StringList)], "dataset", "match", "summary", "groups", "missing_columns");

    private static readonly string ExplainSchema = DataSchemas.Object([("definition", DataSchemas.String), ("subject", DataSchemas.String), ("subject_kind", DataSchemas.String), ("names", DataSchemas.StringList), ("fingerprint", DataSchemas.String),
        ("datasets", DataSchemas.ListOf(DatasetSchema)),
        ("columns", DataSchemas.ListOf(DataSchemas.Object([("dataset", DataSchemas.String), ("column", DataSchemas.String), ("treatment", DataSchemas.String),
            ("rules", DataSchemas.ListOf(DataSchemas.Object([("subject", DataSchemas.String), ("subject_kind", DataSchemas.String), ("treatment", DataSchemas.String), ("mask", Nullable)], "subject", "subject_kind", "treatment")))], "dataset", "column", "treatment", "rules"))),
        ("tables", DataSchemas.ListOf(TableSchema)), ("warnings", DataSchemas.StringList)], "definition", "subject", "subject_kind", "names", "fingerprint", "datasets", "columns", "tables", "warnings");

    private static readonly string CoverageSchema = DataSchemas.Object([("definition", DataSchemas.String),
        ("rules", DataSchemas.ListOf(DataSchemas.Object([("dataset", DataSchemas.String), ("group", DataSchemas.String), ("rule", DataSchemas.String), ("effect", DataSchemas.String), ("table", DataSchemas.String), ("columns", DataSchemas.ListOf(Row)),
            ("uncovered", DataSchemas.Integer), ("values", DataSchemas.ListOf(DataSchemas.Object([("values", Values), ("rows", DataSchemas.Integer)], "values", "rows"))), ("truncated", DataSchemas.Boolean), ("problem", Nullable)],
            "dataset", "group", "rule", "effect", "table", "columns", "uncovered", "values", "truncated")))], "definition", "rules");

    private static async Task<int> ExplainAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "model", "dataset", "kind", "row" });
        var usage = $"Usage: access explain <person or group> {Which} [--kind person|group] [--row <column>=<value> [<column>=<value> ...]] {Where}";
        // the words after the subject that read column=value belong to --row (a value may not be a subject), so `--row region=EU channel=web` works
        var subject = parsed.Words.FirstOrDefault(w => !w.Contains('='));
        var extra = parsed.Words.Where(w => w.Contains('=')).ToList();
        if (parsed.Unknown.Count > 0 || subject is null || parsed.Words.Count(w => !w.Contains('=')) != 1 || parsed.Get("model") is not { Length: > 0 } model || (extra.Count > 0 && !parsed.Has("row")))
        {
            await context.Error.WriteLineAsync(usage);
            return CliExit.Usage;
        }

        var query = new List<string> { "subject=" + Uri.EscapeDataString(subject), "model=" + Uri.EscapeDataString(model) };
        if (parsed.Get("kind") is { Length: > 0 } kind) query.Add("kind=" + Uri.EscapeDataString(kind));
        if (parsed.Get("dataset") is { Length: > 0 } dataset) query.Add("dataset=" + Uri.EscapeDataString(dataset));
        foreach (var row in (parsed.Get("row") is { } first ? new[] { first } : []).Concat(extra)) query.Add("row=" + Uri.EscapeDataString(row));
        var body = await SendAsync("GET", "/api/v1/access/explain?" + string.Join("&", query), parsed, context);
        if (body is null) return CliExit.Failure;
        foreach (var (name, value) in body) context.Data[name] = value?.DeepClone();
        await context.Out.WriteLineAsync(ExplainText(body));
        return CliExit.Ok;
    }

    private static async Task<int> CoverageAsync(string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo", "model", "dataset" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0 || parsed.Get("model") is not { Length: > 0 } model)
        {
            await context.Error.WriteLineAsync($"Usage: access coverage {Which} {Where}");
            return CliExit.Usage;
        }

        var query = "model=" + Uri.EscapeDataString(model) + (parsed.Get("dataset") is { Length: > 0 } dataset ? "&dataset=" + Uri.EscapeDataString(dataset) : "");
        var body = await SendAsync("GET", "/api/v1/access/coverage?" + query, parsed, context);
        if (body is null) return CliExit.Failure;
        foreach (var (name, value) in body) context.Data[name] = value?.DeepClone();
        await context.Out.WriteLineAsync(CoverageText(body));
        return CliExit.Ok;
    }

    private static string ExplainText(JsonObject body)
    {
        var text = new System.Text.StringBuilder();
        text.AppendLine($"{(string?)body["subject"]} ({(string?)body["subject_kind"]}) on {(string?)body["definition"]}; the rules see the names: {string.Join(", ", body["names"]!.AsArray().Select(n => (string?)n))}");
        foreach (var dataset in body["datasets"]!.AsArray().OfType<JsonObject>())
        {
            text.AppendLine().AppendLine($"{(string?)dataset["dataset"]}: {(string?)dataset["summary"]}");
            foreach (var group in dataset["groups"]!.AsArray().OfType<JsonObject>())
                foreach (var rule in group["rules"]!.AsArray().OfType<JsonObject>())
                {
                    var rows = rule["rows"]!.AsArray();
                    text.AppendLine($"  {(string?)group["id"]}/{(string?)rule["id"]} ({(string?)rule["effect"]}, {(string?)rule["table"]}): {(rule["names"]!.AsArray().Count == 0 ? "no row names them" : $"{rows.Count} {(rows.Count == 1 ? "row" : "rows")} name them")}{((bool?)rule["holds"] is { } holds ? (holds ? ", holds for the row" : ", does not hold for the row") : "")}");
                    if ((string?)rule["read_problem"] is { } problem) text.AppendLine($"    {problem}");
                    foreach (var row in rows.OfType<JsonObject>())
                        text.AppendLine($"    {(string?)row["subject"]}: {string.Join(", ", row["values"]!.AsObject().Select(v => $"{v.Key} = {v.Value?.ToJsonString() ?? "NULL"}"))}");
                }

            if (dataset["missing_columns"]!.AsArray().Count > 0) text.AppendLine($"  The row needs the columns {string.Join(", ", dataset["missing_columns"]!.AsArray().Select(c => (string?)c))} to be judged.");
            if (dataset["verdict"] is JsonObject verdict) text.AppendLine("  " + (string?)verdict["text"]);
        }

        foreach (var column in body["columns"]!.AsArray().OfType<JsonObject>()) text.AppendLine($"Column {(string?)column["dataset"]}.{(string?)column["column"]}: {(string?)column["treatment"]}");
        foreach (var table in body["tables"]!.AsArray().OfType<JsonObject>())
            text.AppendLine($"Rules table {(string?)table["table"]}: {(string?)table["state"]}, {(long?)table["age_seconds"]} s old of at most {(long?)table["max_age_seconds"]} s, {((string?)table["last_changed"] is { } at ? "last refreshed " + at : "no refresh reported yet")}");
        foreach (var warning in body["warnings"]!.AsArray()) text.AppendLine("Warning: " + (string?)warning);
        return text.ToString().TrimEnd();
    }

    private static string CoverageText(JsonObject body)
    {
        var text = new System.Text.StringBuilder();
        foreach (var rule in body["rules"]!.AsArray().OfType<JsonObject>())
        {
            text.AppendLine($"{(string?)rule["dataset"]} / {(string?)rule["group"]}/{(string?)rule["rule"]} ({(string?)rule["effect"]}, {(string?)rule["table"]}): " + ((string?)rule["problem"] is { } problem ? "not checked: " + problem : $"{(int?)rule["uncovered"]} uncovered"));
            foreach (var value in rule["values"]!.AsArray().OfType<JsonObject>())
                text.AppendLine($"  {string.Join(", ", value["values"]!.AsObject().Select(v => $"{v.Key} = {v.Value?.ToJsonString() ?? "NULL"}"))}  ({(long?)value["rows"]} rows)");
            if ((bool?)rule["truncated"] == true) text.AppendLine("  ...");
        }

        return text.Length == 0 ? "No rule to check." : text.ToString().TrimEnd();
    }

    private static async Task<int> CallAsync(string method, string path, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "repo" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Usage: access mask-key-{(method == "GET" ? "status" : "rotate")} {Where}");
            return CliExit.Usage;
        }
        var body = await SendAsync(method, path, parsed, context);
        if (body is null) return CliExit.Failure;
        if (method == "GET")
        {
            var exists = (bool?)body["exists"] ?? false;
            context.Data["exists"] = exists;
            await context.Out.WriteLineAsync(exists ? "The group has its key for hash masks." : "The group has no key for hash masks yet; it is made the first time a hash mask is answered.");
        }
        else
        {
            var cleared = (int?)body["cleared"] ?? 0;
            context.Data["rotated"] = true;
            context.Data["cleared"] = cleared;
            await context.Out.WriteLineAsync($"The group's key for hash masks was replaced: every hash mask is different now. {cleared} cache entries were cleared on this instance; other instances use the new key within a minute.");
        }
        return CliExit.Ok;
    }

    /// <summary>One call to the running gateway over its local channel (the operating system decided who may reach it); null after writing why it failed.</summary>
    private static async Task<JsonObject?> SendAsync(string method, string path, CliArgs parsed, CliContext context)
    {
        string dataDirectory;
        ManagementOptions management;
        try
        {
            dataDirectory = parsed.Get("repo") is { Length: > 0 } repo ? Path.GetFullPath(repo) : GatewayConfigFile.ResolveDataDirectory();
            management = GatewayConfiguration.LoadOptions(dataDirectory).Management;
        }
        catch (GatewayConfigException ex) { await context.Error.WriteLineAsync(ex.Message); return null; }
        if (!management.LocalChannel)
        {
            await context.Error.WriteLineAsync("The local channel is off, so the command line cannot reach the running gateway. Set `management: { localChannel: true }` in server.yaml and restart the gateway.");
            return null;
        }
        var message = JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["method"] = method, ["path"] = path });
        JsonNode? answer;
        try
        {
            await using var channel = await LocalChannelClient.ConnectAsync(new LocalChannelOptions("dbdatagateway") { Directory = string.IsNullOrWhiteSpace(management.LocalChannelDirectory) ? null : management.LocalChannelDirectory }, ct: context.Cancellation);
            answer = JsonNode.Parse(await channel.SendAsync(message, context.Cancellation));
        }
        catch (UnauthorizedAccessException)
        {
            await context.Error.WriteLineAsync("The operating system does not let this user reach the gateway's local channel; run the command as the gateway's account or as an administrator.");
            return null;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or JsonException)
        {
            await context.Error.WriteLineAsync($"The running gateway did not answer on its local channel ({ex.Message}). Is `dbdatagateway server run` running with the local channel on?");
            return null;
        }
        var status = (int?)answer?["status"] ?? 0;
        var body = answer?["body"];
        if (status != 200)
        {
            await context.Error.WriteLineAsync($"The gateway refused ({status}): {(string?)body?["error"] ?? body?.ToJsonString() ?? "no reason given"}");
            return null;
        }
        return body as JsonObject ?? new JsonObject();
    }
}
