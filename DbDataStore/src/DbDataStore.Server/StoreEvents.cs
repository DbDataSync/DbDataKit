using System.Text.Json.Nodes;
using DbDataKit.Store;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataStore.Server;

/// <summary>An applied change: which model and entities, who, and the change id (the change request's id when it waited, else the id of the write). <see cref="Sequence"/> only grows; a reader keeps the last one it saw.</summary>
public sealed record ChangeEvent(long Sequence, string ChangeId, string Model, string Instance, IReadOnlyList<string> Entities, int Changes, string By, string? ByName, string? Request, DateTimeOffset At)
{
    public JsonObject ToJson() => new()
    {
        ["seq"] = Sequence, ["change_id"] = ChangeId, ["model"] = Model, ["instance"] = Instance, ["entities"] = new JsonArray(Entities.Select(e => (JsonNode)JsonValue.Create(e)!).ToArray()),
        ["changes"] = Changes, ["by"] = By, ["by_name"] = ByName, ["request"] = Request, ["at"] = At.ToString("O"),
    };
}

/// <summary>
/// The change events of a Store (plan 15 ST03): one line per applied change in <c>events.jsonl</c> of the server's directory, written after the rows are committed (a direct write, or the approval of a change request;
/// a refused or rejected one leaves nothing). A consumer (a Sync or Build run that should follow a change, a scheduler's "on change" trigger) reads <c>GET /api/v1/events?after=&lt;sequence&gt;</c> and keeps the last
/// sequence it saw. The file is cut to its newest half when it passes <see cref="MaxBytes"/>, so a reader that is a long way behind misses events and sees <c>first</c> past what it asked for. Models published to a serving group
/// are written there, not here: their events are the group's (not built).
/// </summary>
public static class StoreEvents
{
    public const string FileName = "events.jsonl";
    public const long MaxBytes = 4 * 1024 * 1024;
    private static readonly object Gate = new();

    private static string FileOf(string directory) => Path.Combine(directory, FileName);

    /// <summary>Records an applied change. A failure to write never fails the write that was already committed.</summary>
    public static void Record(string directory, string model, string instance, IReadOnlyList<RowChange> changes, ValidatedToken by, string? request, DateTimeOffset at, string? changeId = null)
    {
        try
        {
            lock (Gate)
            {
                var file = FileOf(directory);
                var last = Read(directory, 0, int.MaxValue).LastOrDefault()?.Sequence ?? 0;
                var ev = new ChangeEvent(last + 1, request ?? changeId ?? Guid.CreateVersion7().ToString("N"), model, instance, [.. changes.Select(c => c.Entity).Distinct(StringComparer.Ordinal)], changes.Count, by.SubjectId, by.SubjectName, request, at);
                Directory.CreateDirectory(directory);
                if (File.Exists(file) && new FileInfo(file).Length > MaxBytes) Trim(file);
                File.AppendAllText(file, ev.ToJson().ToJsonString() + "\n", new System.Text.UTF8Encoding(false));
            }
        }
        catch (IOException) { }
    }

    private static void Trim(string file)
    {
        var lines = File.ReadAllLines(file);
        var keep = lines.Skip(lines.Length / 2).ToArray();
        var temporary = file + ".tmp";
        File.WriteAllLines(temporary, keep, new System.Text.UTF8Encoding(false));
        File.Move(temporary, file, true);
    }

    /// <summary>The events after a sequence, oldest first, at most <paramref name="limit"/>, optionally of one model.</summary>
    public static IReadOnlyList<ChangeEvent> Read(string directory, long after, int limit, string? model = null)
    {
        var file = FileOf(directory);
        if (!File.Exists(file)) return [];
        var events = new List<ChangeEvent>();
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                var n = JsonNode.Parse(line)!;
                var seq = (long)n["seq"]!;
                if (seq <= after || (model is not null && (string)n["model"]! != model)) continue;
                events.Add(new ChangeEvent(seq, (string)n["change_id"]!, (string)n["model"]!, (string)n["instance"]!, [.. n["entities"]!.AsArray().Select(e => (string)e!)], (int)n["changes"]!, (string)n["by"]!, (string?)n["by_name"], (string?)n["request"], DateTimeOffset.Parse((string)n["at"]!)));
                if (events.Count >= limit) break;
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidCastException or NullReferenceException or FormatException) { }
        }
        return events;
    }

    public static void Map(IEndpointRouteBuilder app, string directory)
    {
        app.MapGet("/api/v1/events", (HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);
            var query = context.Request.Query;
            long after = 0;
            if (query["after"].ToString() is { Length: > 0 } a && !long.TryParse(a, out after)) return Results.BadRequest(new { error = "after is the sequence of the last event you saw." });
            var limit = 100;
            if (query["limit"].ToString() is { Length: > 0 } l && (!int.TryParse(l, out limit) || limit is < 1 or > 1000)) return Results.BadRequest(new { error = "limit is 1 to 1000." });
            var model = query["model"].ToString() is { Length: > 0 } m ? m : null;
            var events = Read(directory, after, limit, model);
            var first = Read(directory, 0, 1).FirstOrDefault()?.Sequence;
            return Results.Json(new JsonObject { ["events"] = new JsonArray(events.Select(e => (JsonNode)e.ToJson()).ToArray()), ["last"] = events.Count > 0 ? events[^1].Sequence : after, ["first"] = first });
        });
    }
}
