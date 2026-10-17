namespace DbDataKit.Config.Yaml;

public enum ConfigSeverity { Error, Warning, Note }

/// <summary>
/// One reported problem. <see cref="Path"/> is the file the problem is in (as a product's source location);
/// <see cref="KeyPath"/> is the dotted key path when known (<c>a.b[0].c</c>). Line and column are 1-based, 0 when unknown.
/// </summary>
public sealed record ConfigDiagnostic(string Code, ConfigSeverity Severity, string Path, int Line, int Column, string Message, string? Fix = null)
{
    public string? KeyPath { get; init; }

    public override string ToString() => $"{(Line > 0 ? $"{Path}:{Line}:{Column}" : Path)}: {Severity.ToString().ToLowerInvariant()} {Code}: {Message}" + (Fix != null ? $" ({Fix})" : "");
}

/// <summary>Codes shared by every product (<c>DBD-</c> prefix). A product maps them onto its own prefix with <see cref="PrefixedCatalog"/>.</summary>
public static class SharedCodes
{
    public const string YamlSyntax = "DBD-101";
    public const string DuplicateKey = "DBD-102";
    public const string UnsupportedFeature = "DBD-103";
    public const string UnknownKey = "DBD-104";
    public const string MissingKey = "DBD-105";
    public const string InvalidValue = "DBD-106";
    public const string SchemaVersionNewer = "DBD-110";
    public const string SchemaVersionInvalid = "DBD-111";
    public const string MigrationFailed = "DBD-112";
    public const string MigrationQuestion = "DBD-113";
    public const string Deprecated = "DBD-120";
    public const string NotEditable = "DBD-130";
    public const string ValidationFailed = "DBD-140";
    public const string UnknownUnionKind = "DBD-107";
    public const string FileNotFound = "DBD-108";

    public static IReadOnlyDictionary<string, (ConfigSeverity Severity, string Fix)> Defaults { get; } = new Dictionary<string, (ConfigSeverity, string)>
    {
        [YamlSyntax] = (ConfigSeverity.Error, "Correct the syntax at the reported position."),
        [DuplicateKey] = (ConfigSeverity.Error, "Remove or merge the duplicate key."),
        [UnsupportedFeature] = (ConfigSeverity.Error, "Write the value out in full (no anchors, aliases, merge keys or tags)."),
        [UnknownKey] = (ConfigSeverity.Error, "Remove the key, or correct its spelling."),
        [MissingKey] = (ConfigSeverity.Error, "Add the missing key."),
        [InvalidValue] = (ConfigSeverity.Error, "Use a value of the documented shape."),
        [FileNotFound] = (ConfigSeverity.Error, "Check the path, or create the file."),
        [UnknownUnionKind] = (ConfigSeverity.Error, "Use one of the documented kinds."),
        [SchemaVersionNewer] = (ConfigSeverity.Error, "Upgrade the tool; this file was written by a newer version."),
        [SchemaVersionInvalid] = (ConfigSeverity.Error, "schemaVersion must be a positive integer."),
        [MigrationFailed] = (ConfigSeverity.Error, "Fix the reported problem and run the migration again."),
        [MigrationQuestion] = (ConfigSeverity.Error, "Answer the question and run the migration again."),
        [Deprecated] = (ConfigSeverity.Warning, "Move to the replacement."),
        [NotEditable] = (ConfigSeverity.Error, "Edit the file by hand."),
        [ValidationFailed] = (ConfigSeverity.Error, "Correct the value."),
    };
}

/// <summary>Creates diagnostics. Injectable so each product keeps its own code prefix, fix text and severity policy.</summary>
public interface IDiagnosticCatalog
{
    /// <summary>Creates a diagnostic. <c>code</c> is a shared <see cref="SharedCodes"/> value (or a product code, passed through).</summary>
    ConfigDiagnostic Create(string code, string file, int line, int column, string message, string? fix = null, string? keyPath = null);
}

/// <summary>Shared <c>DBD-</c> codes with default fix text.</summary>
public sealed class DefaultDiagnosticCatalog : IDiagnosticCatalog
{
    public static readonly DefaultDiagnosticCatalog Instance = new();

    public ConfigDiagnostic Create(string code, string file, int line, int column, string message, string? fix = null, string? keyPath = null)
    {
        var d = SharedCodes.Defaults.TryGetValue(code, out var def) ? def : (ConfigSeverity.Error, "");
        return new ConfigDiagnostic(code, d.Item1, file, line, column, message, fix ?? (d.Item2.Length > 0 ? d.Item2 : null)) { KeyPath = keyPath };
    }
}

/// <summary>Maps shared codes to a product's own (<c>DBD-101</c> to <c>DDB-101</c> by prefix swap, or an explicit map), with optional severity overrides.</summary>
public sealed class PrefixedCatalog(string prefix, IReadOnlyDictionary<string, string>? codeMap = null, IReadOnlyDictionary<string, ConfigSeverity>? severities = null) : IDiagnosticCatalog
{
    public ConfigDiagnostic Create(string code, string file, int line, int column, string message, string? fix = null, string? keyPath = null)
    {
        var d = DefaultDiagnosticCatalog.Instance.Create(code, file, line, column, message, fix, keyPath);
        var mapped = codeMap != null && codeMap.TryGetValue(code, out var m) ? m : code.StartsWith("DBD-", StringComparison.Ordinal) ? prefix + code[4..] : code;
        var sev = severities != null && severities.TryGetValue(code, out var s) ? s : d.Severity;
        return d with { Code = mapped, Severity = sev };
    }
}

public static class DiagnosticExtensions
{
    public static bool HasErrors(this IEnumerable<ConfigDiagnostic> diags) => diags.Any(d => d.Severity == ConfigSeverity.Error);
}
