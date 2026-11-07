namespace DbDataSync.State;

/// <summary>
/// The declaration of Sync's position as a checkpoint of the progress store (plan 28c section 5.1, slice E15): the checkpoint <see cref="Name"/>, kept per <c>[mapping, source_table]</c>, with seven parts. The
/// declaration is what a replication set's <c>package/1</c> says in its <c>progress</c> section, so <see cref="ProgressMigration"/> migrates it when a later version renames or widens a part, and the
/// stored format is fixed here: the part names, their types and their defaults.
/// <list type="bullet">
/// <item><c>position</c> (text): the opaque position the last pass reached — a log sequence number as hex, a change-tracking version in decimal, a time as text.</item>
/// <item><c>position_time</c> (time): when the source says that position committed, where the reader could say.</item>
/// <item><c>intent</c> (text, default <c>Changes</c>): what the next pass is asked to do (<c>ReadIntent</c>).</item>
/// <item><c>hold</c> (text, default <c>None</c>): why the next pass is not to run (<c>ReadHold</c>).</item>
/// <item><c>pending_position</c> (text), <c>pending_position_time</c> (time), <c>pending_batch</c> (text): a position captured for an initial load that has not finished, waiting for the batch that loads it.</item>
/// </list>
/// </summary>
public static class WatermarkCheckpoint
{
    public const string Name = "watermark";

    public const string Position = "position";
    public const string PositionTime = "position_time";
    public const string Intent = "intent";
    public const string Hold = "hold";
    public const string PendingPosition = "pending_position";
    public const string PendingPositionTime = "pending_position_time";
    public const string PendingBatch = "pending_batch";

    /// <summary>The package a stand-alone Sync keeps its positions under, until a replication set is published as a package (slice E12): the progress store is keyed by package, channel and task.</summary>
    public const string Package = SyncState.ServiceId;

    /// <summary>The channel of a stand-alone Sync.</summary>
    public const string Channel = "local";

    /// <summary>The progress store key of a replication's positions: the replication is the task.</summary>
    public static ProgressKey KeyFor(string taskName) => new(Package, Channel, taskName);

    /// <summary>The checkpoint as a package declares it.</summary>
    public static DeclaredCheckpoint Declaration { get; } = new(
        Name,
        new Dictionary<string, DeclaredPart>(StringComparer.Ordinal)
        {
            [Position] = new(ProgressType.Text),
            [PositionTime] = new(ProgressType.Time),
            [Intent] = new(ProgressType.Text, Default: "Changes"),
            [Hold] = new(ProgressType.Text, Default: "None"),
            [PendingPosition] = new(ProgressType.Text),
            [PendingPositionTime] = new(ProgressType.Time),
            [PendingBatch] = new(ProgressType.Text),
        },
        Per: ["mapping", "source_table"]);

    /// <summary>The progress a replication set declares: the one checkpoint.</summary>
    public static ProgressDeclaration ProgressDeclaration { get; } = new([Declaration], []);

    /// <summary>The item key of a mapping's checkpoint: the two key parts joined by <c>|</c> (the progress store's convention), a <c>|</c> or <c>\</c> inside a part written <c>\|</c> or <c>\\</c> so two different pairs never make one key.</summary>
    public static string ItemKey(string mappingName, string sourceTable) => Escape(mappingName) + "|" + Escape(sourceTable);

    /// <summary>The mapping and the source table an item key names (the reverse of <see cref="ItemKey"/>).</summary>
    public static (string Mapping, string SourceTable) Split(string itemKey)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        for (var i = 0; i < itemKey.Length; i++)
        {
            if (itemKey[i] == '\\' && i + 1 < itemKey.Length) current.Append(itemKey[++i]);
            else if (itemKey[i] == '|') { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(itemKey[i]);
        }

        parts.Add(current.ToString());
        return parts.Count == 2 ? (parts[0], parts[1]) : throw new ArgumentException($"'{itemKey}' is not the key of a mapping's position.", nameof(itemKey));
    }

    private static string Escape(string part) => part.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("|", "\\|", StringComparison.Ordinal);
}
