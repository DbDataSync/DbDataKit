using DbDataKit.Dialect;

namespace DbDataSync.Drivers.Generic;

/// <summary>
/// What staging can tell about a value from its runtime type alone, per target engine: the types a target column of a given canonical kind refuses
/// whatever the value (a <see cref="Guid"/> into an integer column, a <see cref="DateTimeOffset"/> into <c>varbinary</c>).
/// <para>
/// This is the table in <c>architecture/staging-value-types.md</c> section 2 as code, and nothing more: it lists only types that were measured and
/// refused by every column type of the kind, so it never refuses a value the engine would take, and a type the table does not know passes (the provider
/// decides, as it always did). The leniency of the providers (<c>1.5</c> into <c>tinyint</c> stored as 2, strings parsed into numbers and dates) is
/// untouched: the owner decided (2026-10-30) that strictness stops at the type level.
/// </para>
/// <para>
/// A14b adds the second table (<c>StagingValueRules.Reinterpreted.cs</c>): the (kind, type) pairs the provider takes but stores as a different value (an
/// <c>int</c> into a PostgreSQL <c>date</c> as days since 2000). They are refused by type, whatever the value; no value is looked at.
/// </para>
/// </summary>
public static partial class StagingValueRules
{
    /// <summary>What the gate does with a (kind, runtime type) pair.</summary>
    public enum Outcome
    {
        /// <summary>Goes on to the writer and the provider.</summary>
        Pass,
        /// <summary>The engine refuses every value of the type for the kind.</summary>
        Refused,
        /// <summary>The engine takes the type but stores a different value than the source's (A14b).</summary>
        Reinterpreted,
    }

    /// <summary>True when <paramref name="valueType"/> is refused for a column of <paramref name="target"/>'s kind, by the engine or as a reinterpretation.</summary>
    public static bool Refuses(EngineId engine, CanonicalType target, Type valueType) => Verdict(engine, target, valueType) != Outcome.Pass;

    /// <summary>Two short list scans, once per column and runtime type (the gate remembers the last type it passed): no value is looked at.</summary>
    public static Outcome Verdict(EngineId engine, CanonicalType target, Type valueType)
    {
        var key = (target.Kind, target.TimeZone == true);
        var never = engine == EngineId.SqlServer ? SqlServerNever : engine == EngineId.Postgres ? PostgresNever : null;
        if (never is null) return Outcome.Pass;
        if (never.TryGetValue(key, out var refused) && Contains(refused, valueType)) return Outcome.Refused;
        var reinterpreted = engine == EngineId.SqlServer ? SqlServerReinterpreted : PostgresReinterpreted;
        return reinterpreted.TryGetValue(key, out var types) && Contains(types, valueType) ? Outcome.Reinterpreted : Outcome.Pass;
    }

    private static bool Contains(Type[] types, Type valueType)
    {
        foreach (var type in types)
            if (type == valueType || (type == typeof(Stream) && type.IsAssignableFrom(valueType)))
                return true;
        return false;
    }
}
