using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Server.State;

/// <summary>
/// The management audit on the shared <c>audit</c> module (plan 29a Q13, Q16): one entry per change made through the management API (who, which console, what, the outcome; never a value). The
/// module's own columns carry what it can say in common (the person, the console it came through, the verb <c>management.&lt;command&gt;</c>, the target, the outcome) and the whole entry is kept in
/// <c>detail</c>, so reading it back gives exactly what was written. Services that share a database share one audit; this one reads only the management entries.
/// </summary>
public sealed class StateAuditLog(StateDatabase state) : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public const string LegacyFileName = "management-audit.log";

    private const string VerbPrefix = "management.";

    private readonly AuditLog _audit = new(state, GatewayState.ServiceId);

    public void Append(DbDataKit.Web.Trust.AuditEntry entry) => state.Retry(() => _audit.Record(new AuditEvent(
        "person", entry.UserId, VerbPrefix + Word(entry.Command), Word(entry.Outcome),
        ViaKind: "console", ViaId: entry.ConsoleId, TargetKind: entry.Target is null ? null : "management", TargetId: entry.Target,
        Detail: JsonSerializer.Serialize(entry, Json))));

    public IReadOnlyList<DbDataKit.Web.Trust.AuditEntry> Read(int limit = 100)
    {
        var newest = new List<DbDataKit.Web.Trust.AuditEntry>();
        foreach (var record in _audit.Read(new AuditQuery(ServiceId: GatewayState.ServiceId, VerbPrefix: VerbPrefix, Limit: limit)))
            if (record.Entry.Detail is { } detail && JsonSerializer.Deserialize<DbDataKit.Web.Trust.AuditEntry>(detail, Json) is { } entry) newest.Add(entry);
        newest.Reverse();
        return newest;
    }

    /// <summary>A management command or outcome as the audit's lower-case word: anything that is not a letter or digit becomes an underscore.</summary>
    private static string Word(string text)
    {
        var word = new string([.. text.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '_')]).Trim('_');
        return word.Length == 0 || !char.IsAsciiLetter(word[0]) ? "x" + word : word;
    }

    /// <summary>Takes the entries of the earlier file-based audit into the state, once, and sets the file aside.</summary>
    public int ImportFile(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, LegacyFileName);
        if (!File.Exists(path)) return 0;
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            DbDataKit.Web.Trust.AuditEntry? entry;
            try { entry = JsonSerializer.Deserialize<DbDataKit.Web.Trust.AuditEntry>(line, Json); }
            catch (JsonException) { continue; }
            if (entry is null) continue;
            Append(entry);
            count++;
        }
        File.Move(path, path + ".imported", overwrite: true);
        return count;
    }
}
