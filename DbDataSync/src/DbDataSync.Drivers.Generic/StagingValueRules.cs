using DbDataKit.Dialect;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// What staging can tell about a value from its runtime type alone, per target engine: the types a target column of a given canonical kind refuses
/// whatever the value (a <see cref="Guid"/> into an integer column, a <see cref="DateTimeOffset"/> into <c>varbinary</c>).
/// <para>
/// This is the table in <c>architecture/staging-value-types.md</c> section 2 as code, and nothing more: it lists only types that were measured and
/// refused by every column type of the kind, so it never refuses a value the engine would take, and a type the table does not know passes (the provider
/// decides, as it always did). The leniency of the providers (<c>1.5</c> into <c>tinyint</c> stored as 2, strings parsed into numbers and dates) is
/// untouched: whether any of it should become strict is the owner's open question, not this class's.
/// </para>
/// </summary>
public static partial class StagingValueRules
{
    /// <summary>True when the table says <paramref name="engine"/> refuses every value of <paramref name="valueType"/> into a column of <paramref name="target"/>'s kind.</summary>
    public static bool Refuses(EngineId engine, CanonicalType target, Type valueType)
    {
        var table = engine == EngineId.SqlServer ? SqlServerNever : engine == EngineId.Postgres ? PostgresNever : null;
        if (table is null || !table.TryGetValue((target.Kind, target.TimeZone == true), out var never)) return false;
        foreach (var type in never)
            if (type == valueType || (type == typeof(Stream) && type.IsAssignableFrom(valueType)))
                return true;
        return false;
    }
}
