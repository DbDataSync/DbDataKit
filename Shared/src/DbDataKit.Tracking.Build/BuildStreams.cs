using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DbDataKit.State.Tracking;

namespace DbDataKit.Tracking.Build;

/// <summary>How a tracking table changes, which decides how the copy of it is kept (plan 28c section 5.2).</summary>
public enum BuildStreamKind
{
    /// <summary>Rows are only ever added: the follower's checkpoint (ordering value, row key) is enough.</summary>
    Append,

    /// <summary>
    /// A row is written when a step starts and updated when it ends (<c>ddl_log</c>, <c>run_log</c>). The follower copies it as soon as it is there, marked <b>unsettled</b> while its status is <c>started</c>; the copy then compares the row with the
    /// target by its hash until the row settles, so the row reaches the copy with its final status although it is behind the checkpoint.
    /// </summary>
    Updated,
}

/// <summary>
/// One tracking table as a stream the follower copies (plan 28c section 5.2): the table, the column whose value only grows (always a time, as UTC ticks), the row key, and what the copy keeps of it. The <see cref="Stream"/> is what
/// <see cref="TrackingFollower"/> works with; the rest is what only Build knows.
/// </summary>
/// <param name="Table">The tracking table on the target.</param>
/// <param name="OrderColumn">The column of <paramref name="Table"/> that orders the stream (a time that only grows); for <c>refresh_plan_member</c> it is the time of the plan's first <c>migration_log</c> row.</param>
/// <param name="KeyColumns">The columns the row key is made of, in order; each is written as text, a time as its ticks, a number zero-padded so the keys sort the way the rows do.</param>
/// <param name="RunLike">The history of a run (owner decision 2026-11-07, plan 28c QE9): aged out of the copy after <see cref="BuildTrackingModule.RunLikeDays"/>. The others are never aged out.</param>
public sealed record BuildStream(string Table, string OrderColumn, IReadOnlyList<string> KeyColumns, BuildStreamKind Kind, bool RunLike)
{
    public string Name => Table;

    /// <summary>The columns a document holds: every column of the table except <c>connection</c> (the identity says which), in the table's order.</summary>
    public IReadOnlyList<string> DocumentColumns { get; } = [.. TrackingSchema.Table(Table).Columns.Where(c => c.Name != "connection").Select(c => c.Name)];

    /// <summary>The stream as the follower sees it. The row key is named as the plan writes it (<c>object_name|first_seen_utc</c>).</summary>
    public TrackingStream Stream => new(Name, [.. DocumentColumns.Append(RowKeyName).Append(OrderColumn).Distinct()], OrderColumn, RowKeyName);

    public string RowKeyName => string.Join('|', KeyColumns);
}

/// <summary>
/// The streams of Build's tracking tables and the one format of a copied row (plan 28c E16, owner decision 2026-11-07: stored formats are fixed now, nothing is converted later). The twelve tracking tables are ten streams here and two
/// <b>snapshots</b> (<see cref="Snapshots"/>): <c>object_info</c> and <c>metadata_dependency</c> are rewritten per subject, so they are not copied row by row but rebuilt in the copy from the copy's own metadata documents.
/// <para>
/// A <b>document</b> is a JSON object with one property for each column of the table (except <c>connection</c>), in the table's order, every column present and null where the row has none. Times are UTC text with seven digits
/// (<c>2026-11-07T10:11:12.1230000Z</c>), a GUID is lower case with hyphens, a number is a JSON number, a hash is trimmed of the padding of a <c>char</c> column; text is text, a JSON column (the metadata document) is its text. The ordering value of a
/// row is the ticks of its time column; the row key is the key columns joined with <c>|</c>. A test pins the streams, so a change here is a deliberate edit of that test and of this format.
/// </para>
/// </summary>
public static class BuildStreams
{
    /// <summary>The ten streams, in the order the copier runs them: the ones the others are read with first.</summary>
    public static IReadOnlyList<BuildStream> All { get; } =
    [
        new("tracking_version", "applied_utc", ["version"], BuildStreamKind.Append, RunLike: false),
        new("schema_version", "first_seen_utc", ["object_name", "first_seen_utc"], BuildStreamKind.Append, RunLike: false),
        new("migration_log", "applied_utc", ["plan_id", "applied_utc"], BuildStreamKind.Append, RunLike: false),
        new("block_log", "created_utc", ["block_id"], BuildStreamKind.Append, RunLike: false),
        new("metadata_document", "recorded_utc", ["subject_kind", "subject", "recorded_utc"], BuildStreamKind.Append, RunLike: false),
        new("plan_store", "stored_utc", ["plan_id"], BuildStreamKind.Append, RunLike: true),
        new("refresh_plan_member", "applied_utc", ["plan_id", "position"], BuildStreamKind.Append, RunLike: true),
        new("operation_interval", "recorded_utc", ["interval_id"], BuildStreamKind.Append, RunLike: true),
        new("ddl_log", "executed_utc", ["ddl_id"], BuildStreamKind.Updated, RunLike: true),
        new("run_log", "started_utc", ["run_id", "step_id"], BuildStreamKind.Updated, RunLike: true),
    ];

    /// <summary>The two tables the copy rebuilds from its documents rather than copies.</summary>
    public static IReadOnlyList<string> Snapshots { get; } = ["object_info", "metadata_dependency"];

    public static BuildStream Get(string name) => All.FirstOrDefault(s => s.Name == name) ?? throw new ArgumentException($"'{name}' is not a stream of Build's tracking tables.", nameof(name));

    /// <summary>The tables of <see cref="TrackingSchema"/> as streams and snapshots together: every one of them is kept by the copy.</summary>
    public static IEnumerable<string> Tables => All.Select(s => s.Name).Concat(Snapshots);

    // ---------- documents ----------

    /// <summary>The document of a row: <paramref name="values"/> are the values of <see cref="BuildStream.DocumentColumns"/>, as the target's driver returns them.</summary>
    public static string Document(BuildStream stream, IReadOnlyList<object?> values)
    {
        var table = TrackingSchema.Table(stream.Table);
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            for (var i = 0; i < stream.DocumentColumns.Count; i++)
            {
                var column = table.Columns.First(c => c.Name == stream.DocumentColumns[i]);
                writer.WritePropertyName(column.Name);
                Write(writer, column.Type, values[i]);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>A document of a snapshot row (<c>object_info</c>, <c>metadata_dependency</c>): the columns are given by name, in the table's order.</summary>
    public static string SnapshotDocument(string table, IReadOnlyDictionary<string, object?> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var column in TrackingSchema.Table(table).Columns.Where(c => c.Name != "connection"))
            {
                writer.WritePropertyName(column.Name);
                Write(writer, column.Type, values.GetValueOrDefault(column.Name));
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Write(Utf8JsonWriter writer, TrackingType type, object? value)
    {
        if (value is null or DBNull) { writer.WriteNullValue(); return; }
        switch (type)
        {
            case TrackingType.TimestampUtc: writer.WriteStringValue(TimeText(Convert.ToDateTime(value, CultureInfo.InvariantCulture))); break;
            case TrackingType.Guid: writer.WriteStringValue((value is Guid g ? g : Guid.Parse(value.ToString()!)).ToString("D", CultureInfo.InvariantCulture)); break;
            case TrackingType.BigInt: writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case TrackingType.Int: writer.WriteNumberValue(Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case TrackingType.Hash: writer.WriteStringValue(value.ToString()!.Trim()); break;
            default: writer.WriteStringValue(value.ToString()); break;
        }
    }

    /// <summary>A time of a tracking column as text, to the tick.</summary>
    public static string TimeText(DateTime time) => new DateTime(time.Ticks, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>The ordering value of a time column.</summary>
    public static long Order(object? time) => Convert.ToDateTime(time, CultureInfo.InvariantCulture).Ticks;

    /// <summary>The ordering value of a document of a stream whose time is a column of the table (not <c>refresh_plan_member</c>, an append stream that is never read back this way).</summary>
    public static long OrderOf(BuildStream stream, string document)
    {
        using var parsed = JsonDocument.Parse(document);
        return DateTime.Parse(parsed.RootElement.GetProperty(stream.OrderColumn).GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).Ticks;
    }

    /// <summary>The time an ordering value stands for (the target's columns are zone-less UTC).</summary>
    public static DateTime TimeOf(long order) => new(Math.Clamp(order, DateTime.MinValue.Ticks, DateTime.MaxValue.Ticks), DateTimeKind.Unspecified);

    // ---------- keys ----------

    /// <summary>One part of a row key: a time as its ticks (20 digits wide, so keys of one table sort the way their times do), a number zero-padded, everything else as text.</summary>
    public static string KeyPart(BuildStream stream, string column, object? value)
    {
        if (value is null or DBNull) return "";
        // refresh_plan_member is ordered by a time it has no column for (the plan's first migration_log row); its key parts are plan_id and position
        var type = TrackingSchema.Table(stream.Table).Columns.FirstOrDefault(c => c.Name == column)?.Type ?? TrackingType.Name;
        return type switch
        {
            TrackingType.TimestampUtc => Order(value).ToString("D19", CultureInfo.InvariantCulture),
            TrackingType.Int or TrackingType.BigInt => Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString("D10", CultureInfo.InvariantCulture),
            TrackingType.Guid => (value is Guid g ? g : Guid.Parse(value.ToString()!)).ToString("D", CultureInfo.InvariantCulture),
            _ => value.ToString()!.Trim(),
        };
    }

    // ---------- settled rows, hashes ----------

    /// <summary>True while the row's step has not ended: <c>ddl_log</c> and <c>run_log</c> rows are written <c>started</c> and updated (never true for another stream).</summary>
    public static bool IsUnsettled(BuildStream stream, string document)
    {
        if (stream.Kind != BuildStreamKind.Updated) return false;
        using var parsed = JsonDocument.Parse(document);
        return parsed.RootElement.TryGetProperty("status", out var status) && status.ValueKind == JsonValueKind.String && status.GetString() == "started";
    }

    /// <summary>The hash of a row's document: what two sides are compared by where a row is updated (and what the copy keeps beside each row).</summary>
    public static string RowHash(string document) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(document))).ToLowerInvariant();

    /// <summary>The ordering value of a stored document (the time column of its stream), so the copy can put a row where the target had it.</summary>
    public static DateTimeOffset OccurredAt(long order) => new(TimeOf(order), TimeSpan.Zero);
}
