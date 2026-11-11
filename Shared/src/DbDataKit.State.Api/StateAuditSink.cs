using System.Text.Json;
using DbDataKit.Web.Trust;

namespace DbDataKit.State.Api;

/// <summary>
/// The management audit of a product whose state has the shared <c>audit</c> module (the console, the Gateway): an entry of the state endpoints goes into it as <c>state.maintenance</c> with the person as actor, the console it came through,
/// the outcome and the whole <see cref="AuditEntry"/> as detail (who, what, the reason; never a secret). Read through <see cref="AuditLog"/> like the rest of the audit.
/// </summary>
public sealed class StateAuditSink(StateDatabase state, string serviceId) : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly AuditLog _audit = new(state, serviceId);

    public void Append(AuditEntry entry) => state.Retry(() => _audit.Record(new AuditEvent(
        "person", entry.UserId, Verb(entry.Command), Word(entry.Outcome),
        ViaKind: entry.ConsoleId == "session" ? "console" : "api", ViaId: entry.ConsoleId, TargetKind: entry.Target is null ? null : "state", TargetId: entry.Target,
        Detail: JsonSerializer.Serialize(entry, Json))));

    public IReadOnlyList<AuditEntry> Read(int limit = 100)
    {
        var newest = new List<AuditEntry>();
        foreach (var record in _audit.Read(new AuditQuery(ServiceId: serviceId, VerbPrefix: "state.", Limit: limit)))
            if (record.Entry.Detail is { } detail && JsonSerializer.Deserialize<AuditEntry>(detail, Json) is { } entry) newest.Add(entry);
        newest.Reverse();
        return newest;
    }

    // the audit's verb is noun.verb in lower case; a command that is not is made one
    private static string Verb(string command) => string.Join('.', command.Split('.').Select(Word));

    private static string Word(string text)
    {
        var word = new string([.. text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]).Trim('_');
        return word.Length == 0 || !char.IsAsciiLetter(word[0]) ? "x" + word : word;
    }
}
