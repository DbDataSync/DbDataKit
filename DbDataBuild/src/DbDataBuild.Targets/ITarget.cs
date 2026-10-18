using DbDataBuild.Core;
using DbDataBuild.Models;

namespace DbDataBuild.Targets;

/// <summary>A parameter a rendered load script expects, bound through the driver and never interpolated into the text.</summary>
/// <param name="Source">"runtime" (supplied by a person) or "resolver" (read from the target by a committed query).</param>
/// <param name="Constraint">For example `max_span 400 days`, or `overridable`.</param>
public sealed record RenderedParameter(string Name, string Type, string Source, string? Constraint);

/// <param name="Text">The script body. Executed exactly as committed, in one transaction that the script itself opens and closes.</param>
/// <param name="ResolverText">The committed query that determines the resolver-sourced parameter, or null.</param>
public sealed record RenderedScript(string Text, string? ResolverText, IReadOnlyList<RenderedParameter> Parameters);

/// <param name="TransformedBodyPrefix">The transpiled `WITH ddb_body AS (...)` text that ends where the loader's own final SELECT begins.</param>
public sealed record LoadRequest(
    string Model, IReadOnlyList<ColumnDefinition> Columns, LoadOperation Operation,
    string TransformedBodyPrefix, string? WatermarkColumnType, string? RangeColumnType);

/// <summary>Per-target statement assembly for the closed library of load strategies (DESIGN.md 6.6).</summary>
public interface ILoader
{
    RenderedScript Render(LoadRequest request);
}

/// <summary>One engine a model can be built for (DESIGN.md section 8). Offline pieces only so far.</summary>
public interface ITarget
{
    string Name { get; }

    /// <summary>The polyglot dialect the body is transpiled to.</summary>
    string Dialect { get; }

    /// <summary>Statement assembly for the load strategies on this engine.</summary>
    ILoader Loader { get; }

    /// <summary>DDL statements and the logical-to-native type table for this engine. Collations come from the project's string-comparison profile.</summary>
    Ddl.DdlGenerator CreateDdl(ProjectConfig config);

    /// <summary>
    /// Offline syntax validation of a rendered script (ScriptDOM for T-SQL targets, polyglot for PostgreSQL). <paramref name="version"/> is the
    /// engine's configured major version; when it is unknown the newest grammar is used, because the matrix already warns that the version is unconfirmed.
    /// </summary>
    IReadOnlyList<Diagnostic> Validate(string sql, string file, int? version = null);
}
