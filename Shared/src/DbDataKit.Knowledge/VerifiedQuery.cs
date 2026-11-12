using System.Text.Json.Nodes;

namespace DbDataKit.Knowledge;

/// <summary>Who verified a query, when and against which version of the model (plan 27a KN21): without it a query is a draft and is not given as verified.</summary>
public sealed record Verification(string By, string At, string ModelVersion);

/// <summary>
/// What the <c>kind: verified-query</c> of <c>knowledge/1</c> adds to an item (plan 27a KN21, plan 27 section 2.9): the <see cref="Questions"/> it answers (the text that is embedded and found by search), the <see cref="Model"/> it is about,
/// the pinned <see cref="Request"/> (the envelope <c>semantic query --request</c> takes: the request is the truth), the <see cref="SemanticSql"/> kept for reading, or a <see cref="SavedQuery"/> of the model's own catalog (named, with an optional
/// pinned version), and who <see cref="Verified"/> it. <see cref="Notes"/> is the text of the file, for people; it is not embedded.
/// </summary>
public sealed record VerifiedQuery(IReadOnlyList<string> Questions, string Model, JsonObject? Request, string? SemanticSql, string? SavedQuery, string? SavedQueryVersion, Verification Verified, string Notes);

/// <summary>Translates semantic SQL into the request envelope for a model; the errors say what was refused (the host supplies the real translator, <c>DbDataKit.Semantic</c>'s, so this library needs no DuckDB).</summary>
public delegate (JsonObject? Request, string? Error) SqlToRequest(string sql, string model);

/// <summary>The request a verified query runs (or the saved query it names as SQL), a note about how it was found, or the error.</summary>
public sealed record ResolvedQuery(JsonObject? Request, string? SavedQuerySql, string? Note, string? Error)
{
    public bool Ok => Error is null;
}

public static class VerifiedQueries
{
    public const string Kind = "verified-query";

    public const string SqlTranslates = "DBD-KNW-051";
    public const string SqlDisagrees = "DBD-KNW-052";

    /// <summary>
    /// The request a verified query runs. A query given as semantic SQL only is translated and the request is what is kept and run; one given with both must agree (the request is the truth, the SQL is for reading: a disagreement is refused, saying which
    /// is run). A saved query of the model's catalog is run as <c>SELECT * FROM name</c> and has no request of its own.
    /// </summary>
    public static ResolvedQuery Resolve(VerifiedQuery query, SqlToRequest? translate)
    {
        if (query.SavedQuery is { } saved) return new(null, $"SELECT * FROM {saved}", null, null);
        if (query.Request is not null && query.SemanticSql is null) return new(query.Request, null, null, null);
        if (query.SemanticSql is null) return new(null, null, null, "A verified query has a request, semantic SQL or a saved query.");
        if (translate is null)
            return query.Request is not null ? new(query.Request, null, "The semantic SQL was not compared with the request here (no translator).", null) : new(null, null, null, "The semantic SQL cannot be translated here (no translator).");
        var (translated, error) = translate(query.SemanticSql, query.Model);
        if (translated is null) return new(null, null, null, $"The semantic SQL does not translate: {error}");
        if (query.Request is null) return new(translated, null, "The request was made from the semantic SQL and is what runs.", null);
        return Same(translated, query.Request)
            ? new(query.Request, null, null, null)
            : new(query.Request, null, "The request ran; the semantic SQL is only kept for reading.", "The semantic SQL and the request disagree. The request is what runs and the SQL is only kept for reading, so the SQL says something the request does not: change one to match the other.");
    }

    /// <summary>Whether two request envelopes ask the same thing (the version of the spec and the order of keys do not matter).</summary>
    public static bool Same(JsonObject a, JsonObject b) => JsonNode.DeepEquals(Canonical(a), Canonical(b));

    private static JsonNode? Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                var result = new JsonObject();
                foreach (var key in o.Select(p => p.Key).Where(k => k != "spec_version").Order(StringComparer.Ordinal)) result[key] = Canonical(o[key]);
                return result;
            case JsonArray array:
                return new JsonArray([.. array.Select(Canonical)]);
            default:
                return node?.DeepClone();
        }
    }
}
