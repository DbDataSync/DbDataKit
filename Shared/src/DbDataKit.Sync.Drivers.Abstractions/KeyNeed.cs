namespace DbDataKit.Sync.Drivers.Abstractions;

/// <summary>
/// Which key a reader or writer matches rows on, and so refuses a mapping without: what a screen and an offline check can ask of a Kind before
/// any pass runs. Declared by the reader or writer beside the code that refuses (<see cref="IChangeReader.NeedsKey"/>,
/// <see cref="IChangeWriter.NeedsKey"/>).
/// </summary>
public enum KeyNeed
{
    /// <summary>Reads or writes whole rows or whole scopes and matches nothing on a key.</summary>
    None,

    /// <summary>The source table's primary key: what a change feed reports a row by (change tracking, a trigger's shadow table, a logical slot's
    /// replica identity), or what a key sweep reads.</summary>
    SourceKey,

    /// <summary>The target table's primary key, with every key column mapped: what an update, a delete or a MERGE matches on.</summary>
    TargetKey,

    /// <summary>The columns that identify a row across its versions: the writer's <c>naturalKey</c> option, or the source's primary key in the
    /// target's names.</summary>
    NaturalKey,
}
