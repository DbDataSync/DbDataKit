using System.Data.Common;
using DbData.Dialect;

namespace DbData.Connections;

/// <summary>SQL rewritten to positional <c>?</c> markers plus the parameter name for each marker, in order.</summary>
public sealed record PositionalCommand(string Sql, IReadOnlyList<string> ParameterOrder)
{
    /// <summary>
    /// Adds one parameter per marker in order (a name used twice is bound twice, as positional drivers need). Names are case
    /// insensitive and may be given with or without their sigil. A missing value is an error that names the parameter, not its value.
    /// </summary>
    public void Bind(DbCommand command, IReadOnlyDictionary<string, object?> values)
    {
        var lookup = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in values) lookup[ParameterStyleAdapter.StripSigil(k)] = v;
        command.CommandText = Sql;
        command.Parameters.Clear();
        int i = 0;
        foreach (var name in ParameterOrder)
        {
            if (!lookup.TryGetValue(name, out var value))
                throw new ArgumentException($"No value was supplied for the parameter '{name}'.", nameof(values));
            var p = command.CreateParameter();
            p.ParameterName = "p" + (++i).ToString(System.Globalization.CultureInfo.InvariantCulture);
            p.Value = value ?? DBNull.Value;
            command.Parameters.Add(p);
        }
    }
}

/// <summary>
/// Rewrites <c>@name</c> (or <c>:name</c>, <c>$1</c>) markers to <c>?</c> for positional providers such as ODBC, using the Plan 05
/// lexer so markers inside strings, comments and quoted identifiers are left alone, and records the order to bind in. Fixes the
/// Sync ODBC gap and replaces Proxy's <c>SqlMarkers</c>.
/// </summary>
public static class ParameterStyleAdapter
{
    private static readonly Lazy<ISqlDialect> Ansi = new(() => DialectCatalog.Builtin.Get(EngineId.Ansi));

    /// <param name="source">The dialect the SQL is written in (decides quoting rules and the marker form).</param>
    /// <param name="target">A positional dialect; defaults to ANSI.</param>
    public static PositionalCommand ToPositional(string sql, ISqlDialect source, ISqlDialect? target = null)
    {
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(source);
        target ??= Ansi.Value;
        if (target.ParameterStyle != ParameterStyle.Positional) throw new ArgumentException("The target dialect is not positional.", nameof(target));
        var rewrite = SqlParameterRewriter.Rewrite(sql, source.Syntax, target);
        return new PositionalCommand(rewrite.Sql, rewrite.BindingOrder);
    }

    /// <summary><c>@name</c> in a SQL Server style source.</summary>
    public static PositionalCommand ToPositional(string sql) => ToPositional(sql, DialectCatalog.Builtin.Get(EngineId.SqlServer));

    public static string StripSigil(string name) => name.Length > 0 && name[0] is '@' or ':' or '$' ? name[1..] : name;
}
