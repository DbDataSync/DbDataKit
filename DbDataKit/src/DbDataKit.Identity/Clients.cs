using System.Security.Cryptography;
using System.Text;
using DbDataKit.State;

namespace DbDataKit.Identity;

public sealed record OidcClient(string ClientId, string Name, IReadOnlyList<string> RedirectUris, bool Confidential);

/// <summary>The applications that sign in through the home. A confidential client has a secret (shown once, kept as a hash); a public one has none and relies on PKCE.</summary>
public sealed class OidcClients(StateDatabase database)
{
    /// <summary>Registers a client. Returns the secret once for a confidential client, null for a public one.</summary>
    public (OidcClient Client, string? Secret) Register(string name, IReadOnlyList<string> redirectUris, bool confidential)
    {
        if (redirectUris.Count == 0) throw new ArgumentException("A client needs at least one redirect URI.", nameof(redirectUris));
        foreach (var uri in redirectUris)
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Fragment.Length > 0 || uri.Contains('\n')) throw new ArgumentException("A redirect URI is an absolute URI with no fragment.", nameof(redirectUris));
        var client = new OidcClient(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), name, redirectUris, confidential);
        var secret = confidential ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=') : null;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(connection, "INSERT INTO OidcClients (ClientId, Name, SecretHash, RedirectUris, CreatedAtUtc) VALUES ($id, $name, $secret, $uris, $at);");
            cmd.Bind(database, "id", client.ClientId);
            cmd.Bind(database, "name", name);
            cmd.Bind(database, "secret", secret is null ? DBNull.Value : Hash(secret));
            cmd.Bind(database, "uris", string.Join('\n', redirectUris));
            cmd.Bind(database, "at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });
        return (client, secret);
    }

    public OidcClient? Find(string clientId) => Read(clientId)?.Client;

    /// <summary>Whether <paramref name="secret"/> is the client's (a public client has none: any secret is wrong, and none is right).</summary>
    public bool Authenticates(string clientId, string? secret)
    {
        var row = Read(clientId);
        if (row is null) return false;
        if (row.Value.SecretHash is null) return string.IsNullOrEmpty(secret);
        return !string.IsNullOrEmpty(secret) && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Hash(secret)), Encoding.ASCII.GetBytes(row.Value.SecretHash));
    }

    private (OidcClient Client, string? SecretHash)? Read(string clientId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "SELECT ClientId, Name, SecretHash, RedirectUris FROM OidcClients WHERE ClientId = $id;");
        cmd.Bind(database, "id", clientId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return ((OidcClient, string?)?)null;
        var hash = reader.IsDBNull(2) ? null : reader.GetString(2);
        return (new OidcClient(reader.GetString(0), reader.GetString(1), reader.GetString(3).Split('\n'), hash is not null), hash);
    });

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
