using System.Text.Json.Nodes;
using DbDataKit.DataModel;
using DbDataKit.Web.Trust;

namespace DbDataStore.Server;

/// <summary>A set of row changes waiting for a person (plan 15 section 3, Z07). <see cref="Changes"/> is the body as it was proposed, applied unchanged when approved.</summary>
public sealed record ChangeRequest(
    string Id, string Model, string Instance, string State, string ProposedBy, string? ProposedByName, bool ByAgent, DateTimeOffset ProposedAt,
    string? Description, JsonArray Changes, string? DecidedBy = null, DateTimeOffset? DecidedAt = null, string? Note = null)
{
    public const string Pending = "pending", Approved = "approved", Rejected = "rejected";

    public JsonObject ToJson(bool withChanges = true)
    {
        var node = new JsonObject
        {
            ["id"] = Id, ["model"] = Model, ["instance"] = Instance, ["state"] = State, ["proposed_by"] = ProposedBy, ["proposed_by_name"] = ProposedByName, ["by_agent"] = ByAgent,
            ["proposed_at"] = ProposedAt.ToString("O"), ["description"] = Description, ["change_count"] = Changes.Count,
            ["decided_by"] = DecidedBy, ["decided_at"] = DecidedAt?.ToString("O"), ["note"] = Note,
        };
        if (withChanges) node["changes"] = JsonNode.Parse(Changes.ToJsonString());
        return node;
    }
}

/// <summary>
/// Change requests, one small file each (<c>requests/&lt;model&gt;/&lt;instance&gt;/&lt;id&gt;.json</c> in the server's directory). Whether a write is a request is the model's say (<see cref="Needs"/>):
/// its <c>approval.agents</c> for an agent, its <c>approval.changes</c> for anyone else. Who may approve is <see cref="Approver"/>.
/// </summary>
public static class StoreChangeRequests
{
    public const string Folder = "requests";

    /// <summary>The subject of an agent's token starts with this. It is signed by the console, so a caller cannot claim it or drop it; service accounts (plan 16) will replace the prefix with a claim.</summary>
    public const string AgentPrefix = "agent:";

    public static bool IsAgent(ValidatedToken token) => token.SubjectId.StartsWith(AgentPrefix, StringComparison.Ordinal);

    /// <summary>True when this caller's write waits for approval: an agent's, unless the model lets agents write directly; anyone's, when the model asks for approval of changes.</summary>
    public static bool Needs(DataModel model, ValidatedToken caller) => DbDataKit.Store.ChangeApproval.Needs(model, IsAgent(caller));

    /// <summary>The role that approves a request on this model: an administrator for an owner's approval, an author otherwise.</summary>
    public static ServerRole Approver(DataModel model) => DbDataKit.Store.ChangeApproval.AdministratorDecides(model) ? ServerRole.Admin : ServerRole.Author;

    private static string FolderOf(string directory, string model, string instance) => Path.Combine(directory, Folder, model, instance);

    public static string FileOf(string directory, string model, string instance, string id) => Path.Combine(FolderOf(directory, model, instance), id + ".json");

    public static bool IsValidId(string id) => id.Length == 32 && id.All(char.IsAsciiHexDigitLower);

    public static ChangeRequest Propose(string directory, string model, string instance, ValidatedToken by, string? description, JsonArray changes, DateTimeOffset at)
    {
        var request = new ChangeRequest(Guid.NewGuid().ToString("N"), model, instance, ChangeRequest.Pending, by.SubjectId, by.SubjectName, IsAgent(by), at, description, changes);
        Save(directory, request);
        return request;
    }

    public static IReadOnlyList<ChangeRequest> List(string directory, string model, string instance, string? state = null)
    {
        var folder = FolderOf(directory, model, instance);
        if (!Directory.Exists(folder)) return [];
        return [.. Directory.EnumerateFiles(folder, "*.json").Select(f => Read(f)).OfType<ChangeRequest>().Where(r => state is null || r.State == state).OrderBy(r => r.ProposedAt).ThenBy(r => r.Id, StringComparer.Ordinal)];
    }

    public static ChangeRequest? Find(string directory, string model, string instance, string id) =>
        IsValidId(id) && File.Exists(FileOf(directory, model, instance, id)) ? Read(FileOf(directory, model, instance, id)) : null;

    public static ChangeRequest Decide(string directory, ChangeRequest request, string state, ValidatedToken by, string? note, DateTimeOffset at)
    {
        var decided = request with { State = state, DecidedBy = by.SubjectId, DecidedAt = at, Note = note };
        Save(directory, decided);
        return decided;
    }

    private static void Save(string directory, ChangeRequest request)
    {
        var file = FileOf(directory, request.Model, request.Instance, request.Id);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, request.ToJson().ToJsonString());
        File.Move(temporary, file, overwrite: true);
    }

    private static ChangeRequest? Read(string file)
    {
        try
        {
            var n = JsonNode.Parse(File.ReadAllText(file))!;
            return new ChangeRequest((string)n["id"]!, (string)n["model"]!, (string)n["instance"]!, (string)n["state"]!, (string)n["proposed_by"]!, (string?)n["proposed_by_name"], (bool)n["by_agent"]!,
                DateTimeOffset.Parse((string)n["proposed_at"]!), (string?)n["description"], (JsonArray)n["changes"]!,
                (string?)n["decided_by"], n["decided_at"] is { } at ? DateTimeOffset.Parse((string)at!) : null, (string?)n["note"]);
        }
        catch (Exception ex) when (ex is System.Text.Json.JsonException or IOException or InvalidCastException or NullReferenceException or FormatException) { return null; }
    }
}
