namespace DbDataKit.Dialect;

// Compiled dialects: subclasses of the data-driven base that override only what data cannot express.

/// <summary>SQL Server: #temp and ##global temp table names are plain names.</summary>
public class SqlServerDialect : DataDrivenDialect
{
    internal SqlServerDialect(ResolvedDialect spec) : base(spec) { }

    public override bool IsSafeUnquoted(string name)
    {
        int hashes = 0;
        while (hashes < name.Length && hashes < 2 && name[hashes] == '#') hashes++;
        return hashes > 0 ? name.Length > hashes && base.IsSafeUnquoted(name[hashes..]) : base.IsSafeUnquoted(name);
    }
}

/// <summary>Microsoft Fabric warehouse: T-SQL surface with a smaller type and capability set (all in data).</summary>
public sealed class FabricDialect : SqlServerDialect
{
    internal FabricDialect(ResolvedDialect spec) : base(spec) { }
}

public sealed class MySqlDialect : DataDrivenDialect
{
    internal MySqlDialect(ResolvedDialect spec) : base(spec) { }

    protected override string? QuoteRestriction(string name)
        => name.EndsWith(' ') ? "MySQL identifiers cannot end with a space" : null;
}

public sealed class OracleDialect : DataDrivenDialect
{
    internal OracleDialect(ResolvedDialect spec) : base(spec) { }

    // Oracle has no way to escape a double quote inside a quoted identifier (doubling does not work).
    protected override string? QuoteRestriction(string name)
        => name.Contains('"') ? "Oracle identifiers cannot contain a double quote" : null;
}
