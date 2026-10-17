using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Secrets;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace DbDataProxy.Server.Auth;

/// <summary>Validates JWT bearer tokens and turns their claims into a principal.</summary>
public interface IJwtValidator
{
    bool Enabled { get; }
    ValueTask<ProxyPrincipal?> ValidateAsync(string token, CancellationToken cancellationToken);
}

public sealed class JwtBearerValidator(IOptionsMonitor<ProxyOptions> options, ILogger<JwtBearerValidator> log, SecretsRuntime? secrets = null) : IJwtValidator
{
    // The resolved signing key lives exactly as long as the options snapshot (JwtOptions instance) it was resolved from.
    private (JwtOptions Snapshot, byte[] Key)? _signingKey;

    private readonly JsonWebTokenHandler _handler = new();
    private ConfigurationManager<OpenIdConnectConfiguration>? _discovery;
    private string? _discoveryAuthority;

    public bool Enabled => options.CurrentValue.Auth.Jwt.Enabled;

    public async ValueTask<ProxyPrincipal?> ValidateAsync(string token, CancellationToken cancellationToken)
    {
        var jwt = options.CurrentValue.Auth.Jwt;
        if (!jwt.Enabled) return null;
        if (string.IsNullOrWhiteSpace(jwt.Audience))
        {
            log.LogError("Proxy:Auth:Jwt is enabled but Audience is not set; refusing all tokens.");
            return null;
        }

        TokenValidationResult result;
        try
        {
            var parameters = await BuildParametersAsync(jwt, cancellationToken).ConfigureAwait(false);
            result = await _handler.ValidateTokenAsync(token, parameters).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.LogWarning("JWT validation failed: {Message}", ex.Message);
            return null;
        }
        if (!result.IsValid)
        {
            log.LogDebug(result.Exception, "JWT rejected");
            return null;
        }

        var claims = result.ClaimsIdentity.Claims.ToList();
        var dataSources = new List<string>();
        string? defaultSource = null;
        foreach (var mapping in jwt.Mappings)
        {
            bool matches = claims.Any(c => c.Type == mapping.Claim &&
                                           c.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(mapping.Value, StringComparer.OrdinalIgnoreCase));
            if (!matches) continue;
            dataSources.AddRange(mapping.DataSources);
            defaultSource ??= mapping.DefaultDataSource;
        }
        if (dataSources.Count == 0)
        {
            log.LogWarning("JWT for {Subject} is valid but matches no data source mapping.", claims.FirstOrDefault(c => c.Type == "sub")?.Value);
            return null;
        }

        string name = claims.FirstOrDefault(c => c.Type == jwt.NameClaim)?.Value
                      ?? claims.FirstOrDefault(c => c.Type == "sub")?.Value
                      ?? "unknown";
        return new ProxyPrincipal(name, "jwt", dataSources.Distinct(StringComparer.OrdinalIgnoreCase), defaultSource);
    }

    private async Task<TokenValidationParameters> BuildParametersAsync(JwtOptions jwt, CancellationToken cancellationToken)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateAudience = true,
            ValidAudience = jwt.Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ClockSkew = TimeSpan.FromSeconds(jwt.ClockSkewSeconds),
        };

        if (!string.IsNullOrWhiteSpace(jwt.SigningKey))
        {
            parameters.IssuerSigningKey = new SymmetricSecurityKey(SigningKeyFor(jwt));
            parameters.ValidateIssuer = !string.IsNullOrWhiteSpace(jwt.Issuer);
            parameters.ValidIssuer = jwt.Issuer;
            return parameters;
        }

        var configuration = await Discovery(jwt.Authority!).GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        parameters.IssuerSigningKeys = configuration.SigningKeys;
        parameters.ValidateIssuer = true;
        parameters.ValidIssuer = string.IsNullOrWhiteSpace(jwt.Issuer) ? configuration.Issuer : jwt.Issuer;
        return parameters;
    }

    private byte[] SigningKeyFor(JwtOptions jwt)
    {
        if (_signingKey is { } cached && ReferenceEquals(cached.Snapshot, jwt)) return cached.Key;
        string configured = secrets?.ResolveSecret("Proxy:Auth:Jwt:SigningKey", jwt.SigningKey) ?? jwt.SigningKey!;
        var key = Convert.FromBase64String(configured);
        _signingKey = (jwt, key);
        return key;
    }

    private ConfigurationManager<OpenIdConnectConfiguration> Discovery(string authority)
    {
        if (_discovery is null || _discoveryAuthority != authority)
        {
            string metadata = authority.TrimEnd('/') + "/.well-known/openid-configuration";
            _discovery = new ConfigurationManager<OpenIdConnectConfiguration>(
                metadata, new OpenIdConnectConfigurationRetriever(), new HttpDocumentRetriever { RequireHttps = true });
            _discoveryAuthority = authority;
        }
        return _discovery;
    }
}
