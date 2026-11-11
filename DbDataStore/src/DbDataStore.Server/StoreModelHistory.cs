using System.Text.Json.Nodes;
using DbDataKit.Web.Trust;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace DbDataStore.Server;

/// <summary>One save or removal of a model's text: the version it left (the last one, for a removal), who did it and when.</summary>
public sealed record ModelHistoryEntry(string Version, DateTimeOffset At, string? By, string? ByName, string Kind)
{
    public const string Created = "created", Saved = "saved", Removed = "removed";

    public JsonObject ToJson(string? current) => new() { ["version"] = Version, ["at"] = At.ToString("O"), ["by"] = By, ["by_name"] = ByName, ["kind"] = Kind, ["current"] = Version == current };
}

/// <summary>
/// The history of a model's text (plan 15 ST01, UI07): every text that is saved is kept under its version (<see cref="StoreModels.Snapshot"/>); this keeps who saved it and when, one line per save in
/// <c>models/versions/&lt;model&gt;/journal.jsonl</c>. A text kept before the journal existed is listed with the time its file was written and no author. Reading is for a viewer. There is no restore route:
/// a restore is an ordinary save of an earlier text quoting the version read, so it is checked, versioned and journalled like any other.
/// </summary>
public static class StoreModelHistory
{
    private static string JournalOf(string directory, string model) => Path.Combine(directory, StoreModels.Folder, "versions", model, "journal.jsonl");

    /// <summary>Appends one line. A failure to write the journal never fails a save: the text itself is already kept.</summary>
    public static void Record(string directory, string model, ModelHistoryEntry entry)
    {
        try
        {
            var file = JournalOf(directory, model);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            var line = new JsonObject { ["version"] = entry.Version, ["at"] = entry.At.ToString("O"), ["by"] = entry.By, ["by_name"] = entry.ByName, ["kind"] = entry.Kind }.ToJsonString();
            File.AppendAllText(file, line + "\n", new System.Text.UTF8Encoding(false));
        }
        catch (IOException) { }
    }

    /// <summary>
    /// Keeps a text that has just been saved under its version and journals it, unless it is the text the model already had (<paramref name="previous"/>, null for a new model): a save of the same text
    /// changes nothing and is not a new entry. Returns the version.
    /// </summary>
    public static string RecordSave(string directory, string model, string text, string? previous, string? by, string? byName)
    {
        var version = StoreModels.Snapshot(directory, model, text);
        if (previous is null || StoreModels.VersionOf(previous) != version)
            Record(directory, model, new ModelHistoryEntry(version, DateTimeOffset.UtcNow, by, byName, previous is null ? ModelHistoryEntry.Created : ModelHistoryEntry.Saved));
        return version;
    }

    /// <summary>The model's history, newest first: the journal, then the kept texts it does not name (older than the journal).</summary>
    public static IReadOnlyList<ModelHistoryEntry> List(string directory, string model)
    {
        var entries = new List<ModelHistoryEntry>();
        var journal = JournalOf(directory, model);
        if (File.Exists(journal))
        {
            foreach (var line in File.ReadAllLines(journal))
            {
                try
                {
                    var n = JsonNode.Parse(line)!;
                    entries.Add(new ModelHistoryEntry((string)n["version"]!, DateTimeOffset.Parse((string)n["at"]!), (string?)n["by"], (string?)n["by_name"], (string?)n["kind"] ?? ModelHistoryEntry.Saved));
                }
                catch (Exception ex) when (ex is System.Text.Json.JsonException or InvalidCastException or NullReferenceException or FormatException) { }
            }
        }
        var folder = Path.GetDirectoryName(journal)!;
        if (System.IO.Directory.Exists(folder))
        {
            var named = entries.Select(e => e.Version).ToHashSet(StringComparer.Ordinal);
            foreach (var file in System.IO.Directory.EnumerateFiles(folder, "*.yaml"))
            {
                var version = Path.GetFileNameWithoutExtension(file);
                if (!named.Contains(version)) entries.Add(new ModelHistoryEntry(version, new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero), null, null, ModelHistoryEntry.Saved));
            }
        }
        // a removal is listed as the event it was, not as a text; the list the screen shows is the texts, so removals come with the version they removed
        return [.. entries.OrderByDescending(e => e.At).ThenBy(e => e.Version, StringComparer.Ordinal)];
    }

    private static IResult Denied(AuthorizationDecision decision) =>
        Results.Json(new { error = $"This needs the {decision.Needed.ToString().ToLowerInvariant()} role." }, statusCode: StatusCodes.Status403Forbidden);

    public static void Map(IEndpointRouteBuilder app, string directory)
    {
        app.MapGet("/api/v1/models/{name}/history", (string name, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            if (!StoreModels.IsValidName(name)) return Results.NotFound(new { error = $"There is no model '{name}'." });
            var exists = File.Exists(StoreModels.FileOf(directory, name));
            var entries = List(directory, name);
            if (!exists && entries.Count == 0) return Results.NotFound(new { error = $"There is no model '{name}'." });
            var current = exists ? StoreModels.VersionOf(File.ReadAllText(StoreModels.FileOf(directory, name))) : null;
            return Results.Json(new JsonObject { ["name"] = name, ["exists"] = exists, ["current"] = current, ["versions"] = new JsonArray(entries.Select(e => (JsonNode)e.ToJson(current)).ToArray()) });
        });

        app.MapGet("/api/v1/models/{name}/history/{version}", (string name, string version, HttpContext context) =>
        {
            var decision = ManagementAuthorization.Authorize(context.ManagementToken(), ServerRole.Viewer);
            if (!decision.Allowed) return Denied(decision);
            if (!StoreModels.IsValidName(name) || StoreModels.TextOfVersion(directory, name, version) is not { } text) return Results.NotFound(new { error = "There is no such version of this model." });
            return Results.Json(new JsonObject { ["name"] = name, ["version"] = version, ["text"] = text });
        });
    }
}
