using DbDataBuild.Core;
using DbDataBuild.Models;
using DbDataBuild.Sql;
using DbDataBuild.Targets.Loaders;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace DbDataBuild.Targets;

/// <summary>T-SQL targets share ScriptDOM validation; the parser version differs.</summary>
public abstract class TSqlTarget(string name, string dialect, Func<int?, TSqlParser> parser) : ITarget
{
    public string Name => name;
    public string Dialect => dialect;
    public ILoader Loader { get; } = new TSqlLoader();
    public Ddl.DdlGenerator CreateDdl(ProjectConfig config) => new Ddl.TSqlDdl(name, config);

    public IReadOnlyList<Diagnostic> Validate(string sql, string file, int? version = null)
    {
        parser(version).Parse(new StringReader(sql), out var errors);
        return errors.Select(e => new Diagnostic(DiagnosticCatalog.RenderedScriptInvalid, new(file, e.Line, e.Column), $"The T-SQL parser reported: {e.Message}")).ToList();
    }
}

/// <summary>The grammar follows the configured engine major version (SQL Server 2017 is 14, 2019 is 15, 2022 is 16, 2025 is 17); unknown means the newest.</summary>
public sealed class SqlServerTarget() : TSqlTarget("sqlserver", "tsql", version => version switch
{
    <= 14 => new TSql140Parser(initialQuotedIdentifiers: true),
    15 => new TSql150Parser(initialQuotedIdentifiers: true),
    16 => new TSql160Parser(initialQuotedIdentifiers: true),
    _ => new TSql170Parser(initialQuotedIdentifiers: true),
});

public sealed class FabricTarget() : TSqlTarget("fabric", "fabric", _ => new TSql170Parser(initialQuotedIdentifiers: true));

public sealed class PostgresTarget : ITarget
{
    public string Name => "postgres";
    public string Dialect => "postgresql";
    public ILoader Loader { get; } = new PostgresLoader();
    public Ddl.DdlGenerator CreateDdl(ProjectConfig config) => new Ddl.PostgresDdl(config);

    public IReadOnlyList<Diagnostic> Validate(string sql, string file, int? version = null)
    {
        var v = Polyglot.Validate(sql, Dialect);
        if (v.Valid) return [];
        return [new Diagnostic(DiagnosticCatalog.RenderedScriptInvalid, new(file, 0, 0), $"The PostgreSQL parser reported: {(v.Error ?? v.ErrorsJson)}")];
    }
}

/// <summary>The engines the tool knows. Looked up by the names used in `connections:`.</summary>
public static class TargetRegistry
{
    private static readonly IReadOnlyDictionary<string, ITarget> Targets = new Dictionary<string, ITarget>
    {
        ["sqlserver"] = new SqlServerTarget(),
        ["fabric"] = new FabricTarget(),
        ["postgres"] = new PostgresTarget(),
    };

    public static ITarget Get(string name) => Targets.TryGetValue(name, out var t) ? t : throw new ArgumentException($"Unknown target `{name}`.", nameof(name));

    public static IReadOnlyCollection<ITarget> All => Targets.Values.ToList();
}
