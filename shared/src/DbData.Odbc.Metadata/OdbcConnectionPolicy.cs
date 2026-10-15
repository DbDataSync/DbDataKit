namespace DbData.Odbc.Metadata;

/// <summary>A connection string was refused by an <see cref="OdbcConnectionPolicy"/>. <see cref="Reason"/> is safe to log; it names rules and keys, never values other than driver and DSN names.</summary>
public sealed class OdbcPolicyViolationException(string reason)
    : Exception("The ODBC connection string was refused by policy: " + reason)
{
    public string Reason { get; } = reason;
}

/// <summary>
/// What a connection string may ask the driver manager to do, checked before SQLDriverConnect. <c>DRIVER=</c> (a name or a
/// path), <c>FILEDSN=</c> and <c>SAVEFILE=</c> can make the driver manager load an arbitrary library or write a file, so a string a
/// user can influence must not reach it unchecked (Proxy review S-10).
/// </summary>
/// <remarks>
/// Every occurrence of a key is checked, not only the first (drivers manager differ on which duplicate wins). When either allow-list
/// is set, a string must be unambiguous: it names a driver or a DSN, not both, and the kind it names must be allowed.
/// The string is read with <see cref="OdbcConnectionString.TryParseStrict"/>; anything that parse rejects is refused.
/// </remarks>
public sealed record OdbcConnectionPolicy
{
    /// <summary>Forbids <c>FILEDSN</c> and <c>SAVEFILE</c>; everything else is allowed.</summary>
    public static OdbcConnectionPolicy Default { get; } = new();

    /// <summary>No checks at all, not even parsing. Only for strings written by the administrator.</summary>
    public static OdbcConnectionPolicy Unrestricted { get; } = new() { ForbidFileDsn = false, ForbidSaveFile = false, Enforce = false };

    /// <summary>Driver names (the text between braces after <c>DRIVER=</c>) that may be used, compared ignoring case. Null means any driver (unless only DSNs are allowed).</summary>
    public IReadOnlySet<string>? AllowedDrivers { get; init; }

    /// <summary>DSN names that may be used, compared ignoring case. Null means any DSN (unless only drivers are allowed).</summary>
    public IReadOnlySet<string>? AllowedDsns { get; init; }

    /// <summary><c>FILEDSN=</c> loads a connection description from a file.</summary>
    public bool ForbidFileDsn { get; init; } = true;

    /// <summary><c>SAVEFILE=</c> makes the driver manager write a file DSN.</summary>
    public bool ForbidSaveFile { get; init; } = true;

    /// <summary>Further keys that must not appear (compared ignoring case), e.g. <c>Trusted_Connection</c> or <c>Authentication</c>.</summary>
    public IReadOnlySet<string> ForbidKeys { get; init; } = new HashSet<string>();

    /// <summary>False only for <see cref="Unrestricted"/>.</summary>
    internal bool Enforce { get; init; } = true;

    /// <summary>Throws <see cref="OdbcPolicyViolationException"/> when the connection string (or a bare DSN name) breaks the policy.</summary>
    public void Validate(string connectionString)
    {
        if (!TryValidate(connectionString, out string? reason)) throw new OdbcPolicyViolationException(reason!);
    }

    /// <summary>True when the connection string is acceptable; otherwise <paramref name="reason"/> says why.</summary>
    public bool TryValidate(string connectionString, out string? reason)
    {
        reason = null;
        if (!Enforce) return true;
        ArgumentNullException.ThrowIfNull(connectionString);

        string text = OdbcConnectionString.Normalize(connectionString);
        if (!OdbcConnectionString.TryParseStrict(text, out var pairs, out string? parseError)) { reason = parseError; return false; }

        var forbidden = new HashSet<string>(ForbidKeys.Select(k => k.Trim()), StringComparer.OrdinalIgnoreCase);
        if (ForbidFileDsn) forbidden.Add("filedsn");
        if (ForbidSaveFile) forbidden.Add("savefile");
        foreach (var (key, _) in pairs)
            if (forbidden.Contains(key)) { reason = $"the key '{key}' is not allowed"; return false; }

        var drivers = pairs.Where(p => p.Key == "driver").Select(p => p.Value.Trim()).ToList();
        var dsns = pairs.Where(p => p.Key == "dsn").Select(p => p.Value.Trim()).ToList();
        if (drivers.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1 || dsns.Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1)
        { reason = "the connection string names more than one driver or DSN"; return false; }

        if (AllowedDrivers is null && AllowedDsns is null) return true;
        if (drivers.Count > 0 && dsns.Count > 0) { reason = "the connection string names both a driver and a DSN"; return false; }
        if (pairs.Any(p => p.Key == "filedsn")) { reason = "a file DSN cannot be checked against the allow-lists"; return false; }

        if (drivers.Count > 0)
        {
            if (AllowedDrivers is null) { reason = "only DSNs are allowed, not DRIVER="; return false; }
            if (!Contains(AllowedDrivers, drivers[0])) { reason = $"the driver '{drivers[0]}' is not allowed"; return false; }
            return true;
        }
        if (dsns.Count > 0)
        {
            if (AllowedDsns is null) { reason = "only drivers are allowed, not DSN="; return false; }
            if (!Contains(AllowedDsns, dsns[0])) { reason = $"the DSN '{dsns[0]}' is not allowed"; return false; }
            return true;
        }
        reason = "the connection string names neither a driver nor a DSN";
        return false;
    }

    private static bool Contains(IReadOnlySet<string> set, string value) => set.Any(s => string.Equals(s.Trim(), value, StringComparison.OrdinalIgnoreCase));
}
