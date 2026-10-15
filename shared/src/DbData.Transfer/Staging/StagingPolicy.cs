using System.Security.Cryptography;
using System.Text;
using DbData.Dialect;

namespace DbData.Transfer;

/// <summary>Where and how staging tables are named and kept.</summary>
public sealed record StagingPolicy
{
    /// <summary>Schema that holds staging tables (Build: the tracking schema). Null = the target's schema.</summary>
    public string? Schema { get; init; }
    public string Prefix { get; init; } = "stg_";
    /// <summary>Longest identifier in UTF-8 bytes. Null = the dialect's limit (63 when the dialect has none). Build uses 60 (the PostgreSQL limit minus headroom).</summary>
    public int? MaxIdentifierBytes { get; init; }
    /// <summary>Append <c>_r&lt;8 hex&gt;</c> derived from the run id so concurrent runs on one target do not share a table. Off for resumable (deterministic) staging.</summary>
    public bool RunScoped { get; init; }
    public StagingLifecycle Lifecycle { get; init; } = StagingLifecycle.DropOnSuccess;
    /// <summary>Use a temp table where the dialect has them (no DDL rights needed on a schema).</summary>
    public bool PreferTemporary { get; init; }

    /// <summary>The policy that reproduces Build's names exactly: tracking schema, <c>stg_&lt;schema&gt;__&lt;table&gt;</c>, 60 bytes, deterministic, retained for resume.</summary>
    public static StagingPolicy BuildCompatible(string trackingSchema) => new() { Schema = trackingSchema, MaxIdentifierBytes = 60, Lifecycle = StagingLifecycle.DropOnSuccess };
}

public static class StagingNamer
{
    /// <summary>Deterministic name for the staging table of <paramref name="target"/>; hash-shortened to the identifier limit.</summary>
    public static TableName Name(StagingPolicy policy, ISqlDialect dialect, TableName target, string? runId = null, bool temporary = false)
    {
        var limit = policy.MaxIdentifierBytes ?? (dialect.Identifiers.MaxLength > 0 ? dialect.Identifiers.MaxLength : 63);
        var model = target.Schema is null ? target.Table : target.Schema + "." + target.Table;
        var plain = policy.Prefix + model.Replace(".", "__", StringComparison.Ordinal);
        var suffix = policy.RunScoped && !string.IsNullOrEmpty(runId) ? "_r" + Hex(runId, 8) : "";
        var hashPrefix = temporary && dialect.Id == EngineId.SqlServer ? "#" : "";
        var budget = limit - hashPrefix.Length;

        string name;
        if (Encoding.UTF8.GetByteCount(plain + suffix) <= budget) name = plain + suffix;
        else
        {
            var keep = budget - 9 - suffix.Length;           // "_" + 8 hex
            if (keep < 1) throw new ArgumentException($"identifier limit {limit} leaves no room for a name", nameof(policy));
            name = CutBytes(plain, keep) + "_" + Hex(model, 8) + suffix;
        }
        name = hashPrefix + name;
        if (temporary) return new TableName(null, name);
        return new TableName(policy.Schema ?? target.Schema, name);
    }

    private static string Hex(string text, int chars) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..chars].ToLowerInvariant();

    private static string CutBytes(string s, int maxBytes)
    {
        var sb = new StringBuilder();
        var bytes = 0;
        foreach (var rune in s.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (bytes + n > maxBytes) break;
            sb.Append(rune.ToString());
            bytes += n;
        }
        return sb.ToString();
    }

    /// <summary>True when a staging table should be dropped now.</summary>
    public static bool ShouldDrop(StagingLifecycle lifecycle, bool succeeded) => lifecycle switch
    {
        StagingLifecycle.DropAlways => true,
        StagingLifecycle.DropOnSuccess => succeeded,
        _ => false,
    };
}

/// <summary>DDL for staging tables, per dialect facts. All identifiers are quoted through the dialect; column types come from the dialect's type table.</summary>
public static class StagingDdl
{
    public static bool CanBeTemporary(ISqlDialect d) => d.Caps.HasFlag(EngineCaps.TempTables);

    public static SqlStatement Create(ISqlDialect d, StagingTable t)
    {
        var cols = string.Join(", ", t.Schema.Columns.Select(c =>
        {
            var type = d.ColumnType(c.Type);
            if (!type.IsMapped) throw new TransferException($"column `{c.Name}`: {c.Type} has no native type on {d.Id}");
            return $"{d.Quote(c.Name)} {type.Result}";            // staging columns are nullable: the target enforces NOT NULL when applied
        }));
        var name = d.QuoteQualified(t.Name.Parts);
        string text;
        if (!t.IsTemporary) text = $"CREATE TABLE {name} ({cols})";
        else if (d.Id == EngineId.SqlServer || d.Id == EngineId.Fabric) text = $"CREATE TABLE {name} ({cols})";                // the # prefix makes it temporary
        else if (d.Id == EngineId.Oracle) text = $"CREATE GLOBAL TEMPORARY TABLE {name} ({cols}) ON COMMIT PRESERVE ROWS";
        else if (d.Id == EngineId.MySql) text = $"CREATE TEMPORARY TABLE {name} ({cols})";
        else text = $"CREATE TEMP TABLE {name} ({cols})";
        return new SqlStatement("staging:create", text, [], new WriteIntent("staging:create", WriteKind.Ddl, t.Name.ToString()));
    }

    public static SqlStatement Drop(ISqlDialect d, StagingTable t)
    {
        var name = d.QuoteQualified(t.Name.Parts);
        var text = d.Id == EngineId.Oracle ? $"DROP TABLE {name}" : $"DROP TABLE IF EXISTS {name}";
        return new SqlStatement("staging:drop", text, [], new WriteIntent("staging:drop", WriteKind.Ddl, t.Name.ToString(), WriteRisk.Risky));
    }

    /// <summary>Empties a retained staging table before a re-run on the same deterministic name.</summary>
    public static SqlStatement Clear(ISqlDialect d, StagingTable t) =>
        new("staging:clear", $"DELETE FROM {d.QuoteQualified(t.Name.Parts)}", [], new WriteIntent("staging:clear", WriteKind.Delete, t.Name.ToString()));
}
