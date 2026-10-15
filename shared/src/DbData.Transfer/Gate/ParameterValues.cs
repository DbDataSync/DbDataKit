using System.Data;
using DbData.Dialect;

namespace DbData.Transfer;

/// <summary>
/// Adapts <see cref="ClrContract"/> values to what ADO.NET providers accept as parameter and bulk values. The contract keeps unsigned and 8-bit
/// integers exact; providers mostly have no such types, so they travel as the next wider signed type (or decimal for unsigned 64-bit).
/// </summary>
public static class ParameterValues
{
    /// <summary>The CLR type a value of <paramref name="type"/> has after <see cref="Adapt"/>.</summary>
    public static Type ClrTypeFor(EngineId engine, CanonicalType type)
    {
        var contract = ClrContract.TypeOf(type);
        if (engine == EngineId.DuckDb) return contract;
        if (contract == typeof(sbyte)) return typeof(short);
        if (contract == typeof(ushort)) return typeof(int);
        if (contract == typeof(uint)) return typeof(long);
        if (contract == typeof(ulong)) return typeof(decimal);
        if (IsTsql(engine))
        {
            if (contract == typeof(DateOnly)) return typeof(DateTime);
            if (contract == typeof(TimeOnly)) return typeof(TimeSpan);
        }
        return contract;
    }

    public static object? Adapt(EngineId engine, object? value)
    {
        if (value is null or DBNull) return null;
        if (engine == EngineId.DuckDb) return value;
        return value switch
        {
            sbyte v => (short)v,
            ushort v => (int)v,
            uint v => (long)v,
            ulong v => (decimal)v,
            DateOnly v when IsTsql(engine) => v.ToDateTime(TimeOnly.MinValue),
            TimeOnly v when IsTsql(engine) => v.ToTimeSpan(),
            _ => value,
        };
    }

    /// <summary>The ADO DbType to set, or null to let the provider infer it (DuckDB and Npgsql infer date and time kinds well).</summary>
    public static DbType? DbTypeFor(EngineId engine, CanonicalType t)
    {
        if (IsTsql(engine))
        {
            switch (t.Kind)
            {
                case CanonicalKind.Date: return DbType.Date;
                case CanonicalKind.Time: return DbType.Time;
                case CanonicalKind.Timestamp: return t.TimeZone == true ? DbType.DateTimeOffset : DbType.DateTime2;
            }
        }
        if (engine != EngineId.DuckDb)
        {
            switch (t.Kind)
            {
                case CanonicalKind.Int8: return DbType.Int16;
                case CanonicalKind.Int16 when t.Unsigned == true: return DbType.Int32;
                case CanonicalKind.Int32 when t.Unsigned == true: return DbType.Int64;
                case CanonicalKind.Int64 when t.Unsigned == true: return DbType.Decimal;
            }
        }
        return AdoWriteExecutor.DbTypeOf(t) is { } d && t.Kind is not (CanonicalKind.Date or CanonicalKind.Time or CanonicalKind.Timestamp) ? d : null;
    }

    private static bool IsTsql(EngineId e) => e == EngineId.SqlServer || e == EngineId.Fabric;
}
