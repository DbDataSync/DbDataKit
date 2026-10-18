using System.Text.Json;
using DbDataBuild.Sql.Native;

namespace DbDataBuild.Sql;

/// <summary>Outcome of one native call. Status 0 is success; otherwise <see cref="Error"/> holds the engine's message.</summary>
public sealed record PolyglotOutcome(int Status, string? Data, string? Error)
{
    public bool Ok => Status == 0;
}

public sealed record PolyglotValidation(bool Valid, string ErrorsJson, int Status, string? Error);

/// <summary>Maps DbDataBuild targets to polyglot dialect names. DuckDB is the canonical dialect.</summary>
public static class Dialects
{
    public const string Canonical = "duckdb";

    public static string ForTarget(string target) => target switch
    {
        "sqlserver" => "tsql",
        "fabric" => "fabric",
        "postgres" => "postgresql",
        "oracle" => "oracle",
        "spark" => "spark",
        "bigquery" => "bigquery",
        _ => throw new ArgumentException($"Unknown target `{target}`.", nameof(target)),
    };
}

/// <summary>Managed facade over polyglot-sql-ffi. Copies native strings, frees native memory, never throws on engine errors.</summary>
public static class Polyglot
{
    public static string PinnedCommit => PolyglotNative.PinnedCommit;

    public static bool IsAvailable()
    {
        try { _ = Version(); return true; }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException) { return false; }
    }

    /// <summary>Library version. The native string is static and must not be freed.</summary>
    public static string Version() => PolyglotNative.Read(PolyglotNative.Version()) ?? "";

    public static IReadOnlyList<string> DialectNames()
    {
        var p = PolyglotNative.DialectList();
        try
        {
            using var doc = JsonDocument.Parse(PolyglotNative.Read(p) ?? "[]");
            return doc.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList();
        }
        finally { PolyglotNative.FreeString(p); }
    }

    public static PolyglotOutcome Transpile(string sql, string from, string to, string optionsJson = "{}") =>
        Wrap(PolyglotNative.TranspileWithOptions(sql, from, to, optionsJson));

    /// <summary>Transpile a single statement and return its SQL, or the failing outcome.</summary>
    public static (PolyglotOutcome Outcome, string? Sql) TranspileOne(string sql, string from, string to, string optionsJson = "{}")
    {
        var o = Transpile(sql, from, to, optionsJson);
        if (!o.Ok) return (o, null);
        using var doc = JsonDocument.Parse(o.Data!);
        var items = doc.RootElement.EnumerateArray().Select(e => e.GetString()!).ToList();
        return items.Count == 1 ? (o, items[0]) : (new PolyglotOutcome(5, o.Data, $"Expected one statement, got {items.Count}."), null);
    }

    public static PolyglotOutcome Parse(string sql, string dialect) => Wrap(PolyglotNative.Parse(dialect == Dialects.Canonical ? DbDataBuild.Core.SqlParseHints.ForParser(sql) : sql, dialect));
    public static PolyglotOutcome Generate(string astJson, string dialect) => Wrap(PolyglotNative.Generate(astJson, dialect));
    public static PolyglotOutcome Format(string sql, string dialect) => Wrap(PolyglotNative.Format(sql, dialect));
    public static PolyglotOutcome OutputColumns(string sql, string dialect) => Wrap(PolyglotNative.OutputColumns(dialect == Dialects.Canonical ? DbDataBuild.Core.SqlParseHints.ForParser(sql) : sql, dialect));
    public static PolyglotOutcome AnalyzeQuery(string sql, string optionsJson = "{}") => Wrap(PolyglotNative.AnalyzeQuery(DbDataBuild.Core.SqlParseHints.ForParser(sql), optionsJson));

    public static PolyglotValidation Validate(string sql, string dialect, string optionsJson = "{}")
    {
        var r = PolyglotNative.ValidateWithOptions(sql, dialect, optionsJson);
        try { return new(r.Valid == 1, PolyglotNative.Read(r.ErrorsJson) ?? "[]", r.Status, PolyglotNative.Read(r.Error)); }
        finally { PolyglotNative.FreeValidationResult(r); }
    }

    private static PolyglotOutcome Wrap(PolyglotResultNative r)
    {
        try { return new(r.Status, PolyglotNative.Read(r.Data), PolyglotNative.Read(r.Error)); }
        finally { PolyglotNative.FreeResult(r); }
    }
}
