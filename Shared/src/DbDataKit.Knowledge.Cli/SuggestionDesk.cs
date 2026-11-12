using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Yaml;
using DbDataKit.State;

namespace DbDataKit.Knowledge.Cli;

/// <summary>Who is suggesting, as the host knows it: the signed-in person, the groups they are in, the agent when one suggests for them, and whether a person has confirmed an answer an agent wants to verify (KN23).</summary>
public interface ISuggestionCaller
{
    string Person { get; }

    /// <summary>The agent that is suggesting for the person, or null when the person does.</summary>
    string? Agent { get; }

    /// <summary>The groups of the identity home the person is in (they own the items of those groups).</summary>
    IReadOnlyCollection<string> Groups { get; }

    /// <summary>Whether a person has said, in the conversation, that the answer to this request is right (the key is <see cref="SuggestionDesk.AnswerKey"/>).</summary>
    bool AnswerConfirmed(string answerKey);
}

/// <summary>What making a suggestion came to: the record, or why there is none (the reader's diagnostics for a text that does not read, or a sentence).</summary>
public sealed record SuggestionOutcome(SuggestionRow? Row, IReadOnlyList<DiagnosticView> Problems, string? Refused)
{
    public bool Ok => Row is not null;

    public static SuggestionOutcome Made(SuggestionRow row) => new(row, [], null);

    public static SuggestionOutcome Refuse(string why) => new(null, [], why);
}

/// <summary>What the <c>knowledge suggest</c>, <c>suggestions</c> and <c>verify</c> commands ask of a host (plan 27a KN19, KN23): suggestions are written at the edge, as the caller.</summary>
public interface ISuggestionDesk
{
    /// <summary>True when an agent is the one asking: it gives the text inline and never names a file.</summary>
    bool ForAgent { get; }

    /// <summary>Records a suggestion for an item (a change), or a new one in <paramref name="project"/>; the text is read strictly first.</summary>
    SuggestionOutcome Suggest(string itemId, bool isNew, string? project, string text, string reason);

    /// <summary>Makes a suggestion of a verified query from an answer: the question, the model, the request as it was run and the values given; a preview of what it returned goes with it (KN23).</summary>
    Task<SuggestionOutcome> VerifyAsync(string question, string model, string requestJson, JsonObject? parameters, CancellationToken cancellation);

    /// <summary>The suggestions the caller may see: their own and those for items their groups own.</summary>
    IReadOnlyList<SuggestionRow> List(string? state);

    /// <summary>One suggestion the caller may see, or null (not there and not theirs are the same answer).</summary>
    SuggestionRow? Show(string id);

    /// <summary>Whether the item the suggestion was based on has moved on since.</summary>
    bool Moved(SuggestionRow row);

    /// <summary>Withdraws the caller's own suggestion while it is undecided.</summary>
    bool Withdraw(string id);
}

/// <summary>
/// Suggestions at the edge over a state database (plan 27a KN19): the proposed text is read by the strict reader before it is kept (a text that does not read is refused with file and line, so no owner reviews a broken file), the item it changes
/// must be one the caller may see (not found otherwise), the suggestion carries the hash of the text it was based on, and it is stored in the group's state whether or not any Workspace server runs. A person sees their own suggestions and the ones
/// for items their groups own, and nothing else. A suggestion from an agent is marked <c>agent:name</c>; whether the agent may make it at all is the tool's prompting (the traits of <c>knowledge suggest</c>), not decided here.
/// </summary>
public sealed class SuggestionDesk(SuggestionStore store, StateKnowledgeIndex index, ISuggestionCaller caller, IKnowledgeCaller access, ISubjectCatalog catalog, IVerifiedQueryReader? runner = null, Func<string, string?>? modelOwner = null, TimeProvider? time = null) : ISuggestionDesk
{
    public const int MaximumTextBytes = 200_000;
    public const int MaximumReason = 1000;
    public const int PreviewRows = 5;

    private static readonly Regex NewId = new("^[a-z0-9][a-z0-9/_.-]*\\.md$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public bool ForAgent => caller.Agent is not null;

    private string Via => caller.Agent is { } agent ? $"agent:{agent}" : "person";

    /// <summary>What a person confirms in a conversation: the sha256 of the question and the request as run (key order does not matter), so confirming one answer confirms that answer.</summary>
    public static string AnswerKey(string question, JsonObject request)
    {
        var text = question.Trim() + "\n" + (VerifiedQueriesCanonical(request));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }

    private static string VerifiedQueriesCanonical(JsonObject request)
    {
        // the same canonical form as comparing a request with its SQL: keys in order, no spec_version
        static JsonNode? Canon(JsonNode? node) => node switch
        {
            JsonObject o => new JsonObject(o.Where(p => p.Key != "spec_version").OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new KeyValuePair<string, JsonNode?>(p.Key, Canon(p.Value)))),
            JsonArray a => new JsonArray([.. a.Select(Canon)]),
            _ => node?.DeepClone(),
        };
        return Canon(request)!.ToJsonString();
    }

    public SuggestionOutcome Suggest(string itemId, bool isNew, string? project, string text, string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) return SuggestionOutcome.Refuse("Say why: a suggestion carries the reason for the person who decides it.");
        if (reason.Length > MaximumReason) return SuggestionOutcome.Refuse($"The reason is at most {MaximumReason} characters.");
        if (string.IsNullOrWhiteSpace(text)) return SuggestionOutcome.Refuse("There is no proposed text: give the whole file as it should read.");
        if (Encoding.UTF8.GetByteCount(text) > MaximumTextBytes) return SuggestionOutcome.Refuse($"The proposed text is more than {MaximumTextBytes / 1000} KB.");
        if (itemId.StartsWith(GeneratedKnowledge.IdPrefix, StringComparison.Ordinal))
            return SuggestionOutcome.Refuse($"'{itemId}' is generated from a definition and changes with it. Suggest a description on the definition's own project instead.");

        StoredItem? current = null;
        if (isNew)
        {
            if (!NewId.IsMatch(itemId)) return SuggestionOutcome.Refuse($"'{itemId}' is not a place for a new item: lower case words, folders with /, ending in .md (for example guides/refunds.md).");
            if (string.IsNullOrWhiteSpace(project)) return SuggestionOutcome.Refuse("A new item needs the project it goes in: --project <name>.");
            if (index.Store.Item(index.Channel, itemId) is not null) return SuggestionOutcome.Refuse($"'{itemId}' already exists. Suggest a change to it instead.");
        }
        else
        {
            current = Visible(itemId);
            if (current is null) return SuggestionOutcome.Refuse($"There is no item '{itemId}' that you may see. (To propose a new one, use --new.)");
            if (current.Hash == KnowledgeItemLoader.HashOf(text)) return SuggestionOutcome.Refuse("The proposed text is the item as it is now: there is nothing to suggest.");
            project = current.Source;
        }

        var read = KnowledgeItemLoader.Load(text, itemId, itemId);
        if (!read.Ok) return new SuggestionOutcome(null, [.. read.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(DiagnosticView.Of)], "The proposed text does not read as a knowledge item, so nothing was kept.");
        var item = read.Item!;
        var kind = item.Verified is not null ? SuggestionKinds.VerifiedQuery : isNew ? SuggestionKinds.New : SuggestionKinds.Change;
        var owner = current?.Owner ?? item.Owner;
        return SuggestionOutcome.Made(store.Add(project!, itemId, kind, item.Title, text, current?.Hash, reason.Trim(), caller.Person, Via, owner));
    }

    private StoredItem? Visible(string itemId) =>
        index.Store.Item(index.Channel, itemId) is { } stored && KnowledgeAccess.Visible(StateKnowledgeIndex.ItemOf(stored), access, catalog) ? stored : null;

    public async Task<SuggestionOutcome> VerifyAsync(string question, string model, string requestJson, JsonObject? parameters, CancellationToken cancellation)
    {
        if (string.IsNullOrWhiteSpace(question) || question.Length > KnowledgeItemLoader.MaximumQuestion || question.Any(char.IsControl)) return SuggestionOutcome.Refuse($"The question is one line of at most {KnowledgeItemLoader.MaximumQuestion} characters.");
        if (string.IsNullOrWhiteSpace(model)) return SuggestionOutcome.Refuse("Name the semantic model the query is asked of: --model <model>.");
        JsonObject request;
        try { request = JsonNode.Parse(requestJson) as JsonObject ?? throw new FormatException(); }
        catch (Exception ex) when (ex is JsonException or FormatException) { return SuggestionOutcome.Refuse("The request is not a JSON document."); }
        if (request["kind"]?.GetValue<string>() != "query" || request["query"] is not JsonObject query || query["measures"] is not JsonArray { Count: > 0 })
            return SuggestionOutcome.Refuse("The request has `kind: query` and a `query` with at least one entry in `measures`: the document the answer was run with.");

        // an agent may verify an answer only after a person has said it is right; a person verifying their own answer is that person
        var key = AnswerKey(question, request);
        if (caller.Agent is not null && !caller.AnswerConfirmed(key))
            return SuggestionOutcome.Refuse("A person has not confirmed this answer. Ask the person whether it is right; when they confirm it in the conversation, verify it again.");
        if (runner is null) return SuggestionOutcome.Refuse("This host cannot run a query, so it cannot show the result a verified query would give. Run it where the model is served.");

        // what the person saw: the request run as them (the values given go in as parameters), a few rows of it
        var asked = (JsonObject)request.DeepClone();
        if (parameters is { Count: > 0 }) asked["parameters"] = parameters.DeepClone();
        VerifiedAnswer answer;
        try { answer = await runner.RunAsync(model, asked.ToJsonString(), PreviewRows, cancellation).ConfigureAwait(false); }
        catch (Exception ex) when (ex is not OperationCanceledException) { return SuggestionOutcome.Refuse($"The query does not run for you, so it cannot be verified: {ex.InnerException?.Message ?? ex.Message}"); }

        var version = runner.CurrentModelVersion(model) ?? "unknown";
        var today = (time ?? TimeProvider.System).GetUtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var id = FreeId(question);
        var text = Text(question, model, request, caller.Person, today, version, modelOwner?.Invoke(model), parameters);
        var read = KnowledgeItemLoader.Load(text, id, id);
        if (!read.Ok) return new SuggestionOutcome(null, [.. read.Diagnostics.Where(d => d.Severity == ConfigSeverity.Error).Select(DiagnosticView.Of)], "The verified query made from this answer does not read as a knowledge item.");

        var preview = new JsonObject
        {
            ["model"] = model,
            ["model_version"] = version,
            ["parameters"] = parameters?.DeepClone() ?? new JsonObject(),
            ["columns"] = new JsonArray([.. answer.Columns.Select(c => (JsonNode)new JsonObject { ["name"] = c.Name, ["type"] = c.Type })]),
            ["rows"] = new JsonArray([.. answer.Rows.Take(PreviewRows).Select(r => (JsonNode)new JsonArray([.. r.Select(v => v is null ? null : JsonValue.Create(Convert.ToString(v, CultureInfo.InvariantCulture)))]))]),
            ["row_count"] = answer.Rows.Count,
            ["truncated"] = answer.Truncated,
        };
        var reason = $"Verify this answer to \"{question.Trim()}\"{(caller.Agent is { } a ? $" (made by {a}, confirmed by {caller.Person})" : "")}.";
        return SuggestionOutcome.Made(store.Add("project", id, SuggestionKinds.VerifiedQuery, read.Item!.Title, text, null, reason, caller.Person, Via, read.Item.Owner, preview.ToJsonString()));
    }

    // the item a verified query becomes: written as the front matter of KN21, the request as JSON text in a single-quoted scalar, who verified it today against the model's version now
    private static string Text(string question, string model, JsonObject request, string person, string today, string version, string? owner, JsonObject? parameters)
    {
        static string Quote(string s) => JsonSerializer.Serialize(s);
        var sb = new StringBuilder("---\nknowledge: 1\nkind: verified-query\n");
        sb.Append("title: ").Append(Quote(question.Trim())).Append('\n');
        if (owner is { Length: > 0 }) sb.Append("owner: ").Append(Quote(owner)).Append('\n');
        sb.Append("model: ").Append(Quote(model.Trim())).Append("\nquestions:\n  - ").Append(Quote(question.Trim())).Append('\n');
        sb.Append("request: '").Append(request.ToJsonString().Replace("'", "''", StringComparison.Ordinal)).Append("'\n");
        sb.Append("verified: { by: ").Append(Quote(person)).Append(", at: ").Append(today).Append(", model-version: ").Append(Quote(version)).Append(" }\n---\n");
        sb.Append("Verified from an answer.");
        if (parameters is { Count: > 0 }) sb.Append(" It was run with ").Append(string.Join(", ", parameters.Select(p => $"{p.Key} = {p.Value?.ToJsonString()}"))).Append('.');
        sb.Append('\n');
        return sb.ToString();
    }

    private string FreeId(string question)
    {
        var slug = Regex.Replace(question.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        slug = slug.Length > 60 ? slug[..60].TrimEnd('-') : slug;
        var id = $"queries/{(slug.Length == 0 ? "question" : slug)}.md";
        var n = 2;
        var taken = store.Visible(caller.Person, caller.Groups).Select(s => s.ItemId).ToHashSet(StringComparer.Ordinal);
        var stem = id[..^3];
        while (index.Store.Item(index.Channel, id) is not null || taken.Contains(id)) id = $"{stem}-{n++}.md";
        return id;
    }

    public IReadOnlyList<SuggestionRow> List(string? state) => store.Visible(caller.Person, caller.Groups, state);

    public SuggestionRow? Show(string id) => store.Get(id) is { } row && (row.SuggestedBy == caller.Person || (row.Owner is not null && caller.Groups.Contains(row.Owner))) ? row : null;

    public bool Moved(SuggestionRow row) => row.BaseVersion is not null && (index.Store.Item(index.Channel, row.ItemId) is not { } now || now.Hash != row.BaseVersion);

    public bool Withdraw(string id) => store.Withdraw(id, caller.Person);
}
