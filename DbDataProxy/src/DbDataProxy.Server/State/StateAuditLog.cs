using System.Globalization;
using System.Text.Json;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataProxy.Server.State;

/// <summary>
/// The management audit in the proxy's state (plan 20 O02), so every instance of a group writes to one log and any of them reads it: who (the person a console vouched for, and which console), what and how it ended, never a
/// value. Entries are kept in the order they were written.
/// </summary>
public sealed class StateAuditLog(StateDatabase state) : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The file the audit was written to before it moved into the state; its entries are read in once (<see cref="ImportFile"/>).</summary>
    public const string LegacyFileName = "management-audit.log";

    public void Append(AuditEntry entry) => state.Retry(() =>
    {
        using var connection = state.OpenConnection();
        using var cmd = state.Command(connection, "INSERT INTO ManagementAudit (AtUtc, Entry) VALUES ($at, $entry);");
        cmd.Bind(state, "at", entry.At.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        cmd.Bind(state, "entry", JsonSerializer.Serialize(entry, Json));
        cmd.ExecuteNonQuery();
    });

    public IReadOnlyList<AuditEntry> Read(int limit = 100) => state.Retry(() =>
    {
        using var connection = state.OpenConnection();
        using var cmd = state.Command(connection, "SELECT Entry FROM ManagementAudit ORDER BY Id DESC;");
        using var reader = cmd.ExecuteReader();
        var newest = new List<AuditEntry>();
        while (newest.Count < limit && reader.Read())
            if (JsonSerializer.Deserialize<AuditEntry>(reader.GetString(0), Json) is { } entry) newest.Add(entry);
        newest.Reverse();
        return (IReadOnlyList<AuditEntry>)newest;
    });

    /// <summary>
    /// Reads the entries of the file the audit used to be written to into the state, in their order, and renames the file to <c>.imported</c> so it is read once. Returns how many there were; 0 when there is no file.
    /// </summary>
    public int ImportFile(string dataDirectory)
    {
        var path = Path.Combine(dataDirectory, LegacyFileName);
        if (!File.Exists(path)) return 0;
        var count = 0;
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0) continue;
            AuditEntry? entry;
            try { entry = JsonSerializer.Deserialize<AuditEntry>(line, Json); }
            catch (JsonException) { continue; }
            if (entry is null) continue;
            Append(entry);
            count++;
        }
        File.Move(path, path + ".imported", overwrite: true);
        return count;
    }
}
