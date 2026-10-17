using System.Text.RegularExpressions;

namespace DbDataKit.Connections.Libraries;

/// <summary>An input to the library installer was refused. The message names the rule and the field, and quotes the value only after escaping it.</summary>
public sealed class LibraryValidationException(string field, string rule, string? shownValue = null)
    : Exception($"Invalid {field}: {rule}" + (shownValue is null ? "" : $" ({Quote(shownValue)})"))
{
    public string Field { get; } = field;
    public string Rule { get; } = rule;

    private static string Quote(string s)
    {
        var shown = s.Length > 40 ? s[..40] + "..." : s;
        return "'" + string.Concat(shown.Select(c => char.IsControl(c) ? "?" : c.ToString())) + "'";
    }
}

/// <summary>
/// The checks that H1 (docs/review/SECURITY.md) asked for, applied before anything is written, deleted or passed to <c>dotnet</c>:
/// NuGet package id grammar, exact SemVer versions (no ranges, wildcards or MSBuild properties), library/driver ids and file names,
/// factory type names, runtime identifiers.
/// </summary>
public static partial class LibraryValidation
{
    public const int MaxPackageIdLength = 100;

    [GeneratedRegex(@"^[A-Za-z0-9_]+(?:[.\-][A-Za-z0-9_]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex PackageIdRegex();

    // SemVer 2.0.0 (semver.org) with the optional fourth numeric part NuGet accepts
    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:\.[0-9]+)?(?:-(?:(?:0|[1-9][0-9]*|[0-9]*[A-Za-z\-][0-9A-Za-z\-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[A-Za-z\-][0-9A-Za-z\-]*))*))?(?:\+[0-9A-Za-z\-]+(?:\.[0-9A-Za-z\-]+)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex SemVerRegex();

    [GeneratedRegex(@"^[a-z0-9](?:[a-z0-9._\-]{0,62}[a-z0-9])?\z", RegexOptions.CultureInvariant)]
    private static partial Regex NameRegex();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*(?:\+[A-Za-z_][A-Za-z0-9_]*)*(?: *, *[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex TypeNameRegex();

    [GeneratedRegex(@"^[a-z][a-z0-9]*(?:-[a-z0-9]+)*(?:\.[0-9]+)?(?:-[a-z0-9]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex RidRegex();

    private static readonly HashSet<string> WindowsReserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul", "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
    };

    public static void ValidatePackageId(string? id, string field = "PackageId")
    {
        if (string.IsNullOrEmpty(id)) throw new LibraryValidationException(field, "it is empty");
        if (id.Length > MaxPackageIdLength) throw new LibraryValidationException(field, $"it is longer than {MaxPackageIdLength} characters");
        if (!PackageIdRegex().IsMatch(id)) throw new LibraryValidationException(field, "it is not a NuGet package id (letters, digits, '_', '.', '-')", id);
    }

    public static void ValidateVersion(string? version, string field = "Version")
    {
        if (string.IsNullOrEmpty(version)) throw new LibraryValidationException(field, "it is empty");
        if (version.Length > 64) throw new LibraryValidationException(field, "it is longer than 64 characters");
        if (!SemVerRegex().IsMatch(version)) throw new LibraryValidationException(field, "it is not an exact SemVer version (no ranges, wildcards or properties)", version);
    }

    /// <summary>A library id, driver id or file name: lower case letters, digits, '.', '_' and '-', no path parts, not a reserved device name.</summary>
    public static void ValidateName(string? name, string field = "name")
    {
        if (string.IsNullOrEmpty(name)) throw new LibraryValidationException(field, "it is empty");
        if (!NameRegex().IsMatch(name)) throw new LibraryValidationException(field, "use lower case letters, digits, '.', '_' and '-' (1 to 64 characters, starting and ending with a letter or digit)", name);
        if (name.Contains("..", StringComparison.Ordinal)) throw new LibraryValidationException(field, "it contains '..'", name);
        string stem = name.Split('.')[0];
        if (WindowsReserved.Contains(stem)) throw new LibraryValidationException(field, "it is a reserved device name", name);
    }

    /// <summary>An assembly qualified type name (<c>Namespace.Type, Assembly</c>), as written in a library manifest.</summary>
    public static void ValidateFactoryType(string? typeName, string field = "FactoryType")
    {
        if (string.IsNullOrEmpty(typeName)) throw new LibraryValidationException(field, "it is empty");
        if (typeName.Length > 300 || !TypeNameRegex().IsMatch(typeName)) throw new LibraryValidationException(field, "it is not a type name of the form 'Namespace.Type, Assembly'", typeName);
    }

    public static void ValidateRuntimeIdentifier(string? rid, string field = "RuntimeIdentifier")
    {
        if (string.IsNullOrEmpty(rid) || rid.Length > 40 || !RidRegex().IsMatch(rid)) throw new LibraryValidationException(field, "it is not a runtime identifier", rid);
    }
}

/// <summary>A NuGet feed that may be used: https only, no credentials in the URL, host on the allow-list.</summary>
public sealed class NuGetSourcePolicy
{
    public static NuGetSourcePolicy NuGetOrgOnly { get; } = new(["https://api.nuget.org/v3/index.json"]);

    private readonly HashSet<string> _allowed;

    public NuGetSourcePolicy(IEnumerable<string> allowedFeeds)
    {
        _allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var feed in allowedFeeds)
            _allowed.Add(Normalize(feed) ?? throw new ArgumentException($"The allowed feed '{feed}' is not an https URL.", nameof(allowedFeeds)));
    }

    public IReadOnlyCollection<string> Allowed => _allowed;

    private static string? Normalize(string source)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment)) return null;
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + uri.Query;
    }

    /// <summary>Returns the normalised feed, or throws. Null input means "the default feed", which must itself be allowed.</summary>
    public string Resolve(string? source)
    {
        if (source is null) return _allowed.First();
        if (source.Length == 0 || source.StartsWith('-') || source.Any(char.IsControl) || char.IsWhiteSpace(source[0]) || char.IsWhiteSpace(source[^1]))
            throw new LibraryValidationException("Source", "it is not a feed URL", source);
        var normalized = Normalize(source) ?? throw new LibraryValidationException("Source", "it must be an https URL without credentials", source);
        if (!_allowed.Contains(normalized)) throw new LibraryValidationException("Source", "it is not one of the configured feeds", source);
        return normalized;
    }
}
