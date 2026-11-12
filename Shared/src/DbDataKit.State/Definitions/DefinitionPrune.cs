using System.Data.Common;
using System.Text.Json;

namespace DbDataKit.State;

/// <summary>A version a prune keeps although it was a candidate, and why.</summary>
public sealed record PruneRetained(int Version, string Reason);

/// <summary>
/// What a prune does or would do to one definition (plan 30 PB05). <paramref name="Remove"/> are the versions removed, <paramref name="Retained"/> the candidates kept (active, staged or previous in a channel, or pinned by a kept
/// version), <paramref name="Artifacts"/> the hashes of files that go with them (no kept version names the file), <paramref name="Done"/> false for a dry run.
/// </summary>
public sealed record PrunePlan(string Kind, string Name, IReadOnlyList<int> Remove, IReadOnlyList<PruneRetained> Retained, IReadOnlyList<string> Artifacts, bool Done);

public sealed partial class DefinitionPublisher
{
    /// <summary>
    /// Removes the versions of a definition an administrator no longer wants (plan 30 decision 3), or says what it would remove (<paramref name="dryRun"/>). Name which to remove with <paramref name="versions"/>, or keep the
    /// <paramref name="keep"/> newest and remove the rest. Never removed: a version any channel has active, staged or as its previous version, and a version another version pins (<c>kind:name@version</c> in its
    /// <c>depends_on</c>). A file goes only when no kept version of any definition names its payload. Each removed version is recorded as a <c>prune</c> event.
    /// </summary>
    public PrunePlan Prune(string kind, string name, string by, int? keep = null, IReadOnlyList<int>? versions = null, bool dryRun = true)
    {
        DefinitionStore.Check(kind, name, DefinitionStore.Production);
        if ((keep is null) == (versions is null)) throw new ArgumentException("Say either keep (how many of the newest versions stay) or versions (which to remove), not both and not neither.");
        if (keep is < 0) throw new ArgumentException("keep is not negative.");
        var plan = _database.Retry(() =>
        {
            using var connection = _database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            var all = new List<int>();
            using (var command = _database.Command(Module, connection, transaction, "SELECT version FROM {table:definition_version} WHERE kind = $kind AND name = $name ORDER BY version;"))
            {
                command.Bind(_database, "kind", kind).Bind(_database, "name", name);
                using var reader = command.ExecuteReader();
                while (reader.Read()) all.Add(reader.Int32(0));
            }
            if (all.Count == 0) throw new DefinitionNotFoundException($"There is no {kind} '{name}'.");
            List<int> candidates;
            if (versions is not null)
            {
                var unknown = versions.Where(v => !all.Contains(v)).ToList();
                if (unknown.Count > 0) throw new DefinitionNotFoundException($"The {kind} '{name}' has no version {string.Join(", ", unknown)}.");
                candidates = [.. versions.Distinct().Order()];
            }
            else
            {
                candidates = [.. all.OrderDescending().Skip(keep!.Value).Order()];
            }

            var reasons = new Dictionary<int, string>();
            using (var command = _database.Command(Module, connection, transaction, "SELECT channel, version, previous_version, staged_version FROM {table:definition_serving} WHERE kind = $kind AND name = $name;"))
            {
                command.Bind(_database, "kind", kind).Bind(_database, "name", name);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var channel = reader.GetString(0);
                    reasons.TryAdd(reader.Int32(1), $"active in {channel}");
                    if (reader.NullableInt32(2) is { } previous) reasons.TryAdd(previous, $"the previous version in {channel}");
                    if (reader.NullableInt32(3) is { } staged) reasons.TryAdd(staged, $"staged in {channel}");
                }
            }
            // a version another version pins: kind:name@version in its depends_on
            var pin = $"{kind}:{name}@";
            using (var command = _database.Command(Module, connection, transaction, "SELECT kind, name, version, depends_on FROM {table:definition_version};"))
            {
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    foreach (var text in ParseDepends(reader.GetString(3)))
                    {
                        if (!text.StartsWith(pin, StringComparison.Ordinal) || !int.TryParse(text.AsSpan(pin.Length), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pinned)) continue;
                        reasons.TryAdd(pinned, $"{reader.GetString(0)} '{reader.GetString(1)}' version {reader.Int32(2)} depends on it");
                    }
                }
            }

            var retained = candidates.Where(reasons.ContainsKey).Select(v => new PruneRetained(v, reasons[v])).ToList();
            var remove = candidates.Where(v => !reasons.ContainsKey(v)).ToList();

            // the files: a payload goes only when every row that names it belongs to a removed version of this definition
            var gone = new Dictionary<Guid, string>();
            foreach (var version in remove)
                foreach (var file in ReadArtifacts(connection, transaction, kind, name, version)) gone.TryAdd(file.PayloadId, file.Sha256);
            var unreferenced = new List<Guid>();
            foreach (var payload in gone.Keys)
            {
                var shared = false;
                using var command = _database.Command(Module, connection, transaction, "SELECT kind, name, version FROM {table:definition_artifact} WHERE payload_id = $id;");
                command.Bind(_database, "id", payload);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                    if (reader.GetString(0) != kind || reader.GetString(1) != name || !remove.Contains(reader.Int32(2))) shared = true;
                if (!shared) unreferenced.Add(payload);
            }

            if (!dryRun && remove.Count > 0)
            {
                var now = _database.Time.GetUtcNow();
                foreach (var version in remove)
                {
                    foreach (var table in new[] { "definition_artifact", "definition_version" })
                    {
                        using var delete = _database.Command(Module, connection, transaction, $"DELETE FROM {{table:{table}}} WHERE kind = $kind AND name = $name AND version = $version;");
                        delete.Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "version", (long)version);
                        delete.ExecuteNonQuery();
                    }
                    Log(connection, transaction, "prune", kind, name, DefinitionStore.Production, version, by, now);
                }
                transaction.Commit();
            }

            return (new PrunePlan(kind, name, remove, retained, [.. unreferenced.Select(p => gone[p]).Distinct().Order()], !dryRun), unreferenced);
        });

        if (!dryRun && _payloads is not null)
            foreach (var payload in plan.unreferenced) _payloads.Delete(payload);
        return plan.Item1;
    }

    private IReadOnlyList<DefinitionArtifact> ReadArtifacts(DbConnection connection, DbTransaction transaction, string kind, string name, int version)
    {
        var found = new List<DefinitionArtifact>();
        using var command = _database.Command(Module, connection, transaction, "SELECT path, sha256, size, media_type, payload_id FROM {table:definition_artifact} WHERE kind = $kind AND name = $name AND version = $version;");
        command.Bind(_database, "kind", kind).Bind(_database, "name", name).Bind(_database, "version", (long)version);
        using var reader = command.ExecuteReader();
        while (reader.Read()) found.Add(new DefinitionArtifact(reader.GetString(0), reader.GetString(1), reader.Int64(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.Uuid(4)));
        return found;
    }

    private static IEnumerable<string> ParseDepends(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
