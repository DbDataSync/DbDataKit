namespace DbDataProxy.Server.Configuration;

/// <summary>
/// JWT bearer authentication. Clients send <c>authorization: Bearer &lt;jwt&gt;</c> on every call (no handshake).
/// Set <see cref="Authority"/> for an OIDC provider (Entra ID: https://login.microsoftonline.com/{tenant}/v2.0), or
/// <see cref="SigningKey"/> for tokens signed with a shared HMAC key.
/// </summary>
public sealed class JwtOptions
{
    public bool Enabled => !string.IsNullOrWhiteSpace(Authority) || !string.IsNullOrWhiteSpace(SigningKey);

    public string? Authority { get; set; }

    /// <summary>Base64 HMAC-SHA256 key, for providers or tooling that issue symmetric tokens.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Expected issuer; defaults to the authority's. With a symmetric key the issuer is only checked when set.</summary>
    public string? Issuer { get; set; }

    /// <summary>Expected audience (for Entra ID: the application ID URI or client id). Required.</summary>
    public string? Audience { get; set; }

    /// <summary>Claim holding the display/user name.</summary>
    public string NameClaim { get; set; } = "preferred_username";

    public int ClockSkewSeconds { get; set; } = 60;

    /// <summary>Which token claims grant which data sources. A token that matches no mapping is refused.</summary>
    public List<JwtMapping> Mappings { get; set; } = [];
}

public sealed class JwtMapping
{
    /// <summary>Claim type to inspect, e.g. "groups", "roles", "scp" or "sub".</summary>
    public string Claim { get; set; } = "";

    /// <summary>Claim value that triggers this mapping (a space-separated claim such as "scp" is split).</summary>
    public string Value { get; set; } = "";

    public List<string> DataSources { get; set; } = [];
    public string? DefaultDataSource { get; set; }
}
