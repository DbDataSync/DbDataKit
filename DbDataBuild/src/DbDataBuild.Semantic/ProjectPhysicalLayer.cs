using System.Text.RegularExpressions;
using DbDataBuild.Models;
using DbDataKit.Semantic.Authoring;

namespace DbDataBuild.Semantic;

/// <summary>
/// The physical layer of a project's semantic models: its models are the tables, its connections the sources. Nothing is written by hand: a model's columns and logical types are its definition's, its key is
/// its grain, and its physical table on a connection is the model's name (`schema_name.table_name`), which is where the tool builds (or maps) it.
/// </summary>
public sealed partial class ProjectPhysicalLayer : IPhysicalLayer
{
    /// <summary>What a semantic file says for "the project's default connection", so a template does not hard-code an engine.</summary>
    public const string DefaultSource = "default";

    private readonly IReadOnlyDictionary<string, ProjectModel> models;
    private readonly ProjectConfig config;

    /// <summary>Layer over the project's models (built and mapped) and its configuration.</summary>
    public ProjectPhysicalLayer(IReadOnlyList<ProjectModel> models, ProjectConfig config)
    {
        this.models = models.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        this.config = config;
    }

    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex SourceName();

    /// <summary>The connection a source of a semantic file stands for: the connection of that name, or for `default` the project's one default connection. Null when it is neither.</summary>
    public string? ConnectionOf(string source)
    {
        if (config.Connections.ContainsKey(source)) return source;
        return source == DefaultSource && config.DefaultConnections.Count == 1 ? config.DefaultConnections[0] : null;
    }

    /// <summary>
    /// `sqlserver` for any connection the tool builds on. The catalog format only has emitters for SQL Server and Analysis Services (plan 19 J05 adds PostgreSQL), so the kind is what the catalog
    /// validation needs to accept the source; the connection's real engine is reported by `project show semantic`, and a package for a PostgreSQL connection waits for the emitter.
    /// </summary>
    public string? SourceKind(string source) =>
        ConnectionOf(source) is { } connection && config.Connections[connection].Kind.IsBuildTarget && SourceName().IsMatch(source) ? "sqlserver" : null;

    /// <inheritdoc />
    public bool KnowsModel(string model) => models.ContainsKey(model);

    /// <inheritdoc />
    public PhysicalTable? Table(string model, string source)
    {
        if (!models.TryGetValue(model, out var found) || ConnectionOf(source) is not { } connection || !found.Connections.Contains(connection, StringComparer.Ordinal)) return null;
        return new PhysicalTable(found.Name, found.Columns.Select(c => new PhysicalColumn(c.Name, CatalogType(c.Type))).ToList(), found.Grain);
    }

    /// <summary>A model's column type as the catalog format names it (`VARCHAR(20)` is `String`, `DECIMAL(18, 2)` is `Decimal`); a type with no equivalent is `Opaque`.</summary>
    public static string CatalogType(string type)
    {
        var t = type.Trim().ToUpperInvariant();
        var name = t.Contains('(') ? t[..t.IndexOf('(')].Trim() : t;
        if (name is "TIMESTAMPTZ" || name.StartsWith("TIMESTAMP WITH TIME ZONE", StringComparison.Ordinal)) return "DateTimeTz";
        return name switch
        {
            "VARCHAR" or "CHAR" or "TEXT" or "STRING" or "NVARCHAR" or "NCHAR" or "BPCHAR" or "UUID" => "String",
            "TINYINT" or "SMALLINT" or "INTEGER" or "INT" or "BIGINT" or "HUGEINT" or "UTINYINT" or "USMALLINT" or "UINTEGER" or "UBIGINT" or "INT2" or "INT4" or "INT8" => "Integer",
            "DECIMAL" or "NUMERIC" => "Decimal",
            "DOUBLE" or "FLOAT" or "REAL" or "FLOAT4" or "FLOAT8" => "Float",
            "BOOLEAN" or "BOOL" => "Boolean",
            "DATE" => "Date",
            "TIME" => "Time",
            "TIMESTAMP" or "DATETIME" => "DateTime",
            _ => "Opaque",
        };
    }
}
