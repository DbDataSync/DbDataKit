using System.Text.Json;

namespace DbDataSync.State;

/// <summary>
/// One thing an operator asked of a replication that the runner owns (plan 28c E12): what the API would have put in Sync's work queue, carried as the <c>requests</c> input of a run of the drain (ED13: the
/// segments are the payload). <see cref="Kind"/> is a run kind (<c>BulkLoad</c>, <c>ReconcileDeletes</c>, <c>Verification</c>, <c>Primary</c>) or <c>Position</c> (seeds the position, intent and hold of a mapping that has none: what a cutover hands over) and <c>Resync</c>, which are no items: it sets the mapping's read intent to an initial load
/// (the API's resync) and queues the primary pass that carries it out. <see cref="Id"/> names the request: the drain applies a request of one id once, however often it is read (a retried run reads its input again). The API divides the segments (it can reach the source) and the drain queues them as rows of its own queue, the rows the API's own path writes.
/// </summary>
public sealed record DrainRequest(
    string Kind,
    string Mapping,
    string? Label = null,
    string? SegmentJson = null,
    string? ReaderKind = null,
    string? CacheKind = null,
    string? WriterKind = null,
    string? DeleteGuardJson = null,
    bool OneAtATime = true,
    string? BatchId = null,
    int? BatchSegments = null,
    long? EstimatedRows = null,
    string? EstimateCaveat = null,
    string? SourceKey = null,
    string? Position = null,
    DateTimeOffset? PositionTime = null,
    string? Intent = null,
    string? Hold = null,
    string? Id = null);

/// <summary>The <c>requests</c> input of a drain as text: what the API sends and the drain reads (the same code on both sides).</summary>
public static class DrainRequestText
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The kind of the work item a request travels as when a drain is up already: the runner's work endpoint puts it in the task's queue and the drain applies it in its next sweep.</summary>
    public const string ItemKind = "Request";

    /// <summary>One request as the payload of a work item (<see cref="Parse"/> reads it back with <see cref="ParseOne"/>).</summary>
    public static string SerializeOne(DrainRequest request) => JsonSerializer.Serialize(request, Json);

    public static DrainRequest ParseOne(string? payload) => Parse("[" + payload + "]") is [var one] ? one : throw new InvalidOperationException("The work item of a request has no request in its payload.");

    public static string Serialize(IReadOnlyList<DrainRequest> requests) => requests.Count == 0 ? "" : JsonSerializer.Serialize(requests, Json);

    /// <summary>The requests of the input text; empty text is none, and text that is not a list of requests fails naming the input.</summary>
    public static IReadOnlyList<DrainRequest> Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return [];
        try
        {
            return JsonSerializer.Deserialize<List<DrainRequest>>(text, Json) ?? [];
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The input `requests` is not a JSON list of requests: {ex.Message}", ex);
        }
    }
}

/// <summary>The name a replication has as a task of its package on a runner (lower-case letters, digits, <c>-</c> and <c>_</c>, starting with a letter or digit: what is not allowed becomes <c>-</c>).</summary>
public static class RunnerTaskNames
{
    public static string Of(string replication)
    {
        var name = System.Text.RegularExpressions.Regex.Replace(replication.ToLowerInvariant(), "[^a-z0-9_-]", "-").TrimStart('-', '_');
        return name.Length > 64 ? name[..64] : name;
    }
}
