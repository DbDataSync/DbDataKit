using System.Security.Cryptography;
using System.Text;
using DbDataKit.State;

namespace DbDataKit.Identity;

/// <summary>The applications that sign in through the home. A confidential client has a secret (shown once, kept as a hash); a public one has none and relies on PKCE.</summary>
public sealed class ModuleOidcClients(StateDatabase database)
{
    private const string Module = IdentityModule.Name;

    /// <summary>Registers a client. Returns the secret once for a confidential client, null for a public one.</summary>
    public (OidcClient Client, string? Secret) Register(string name, IReadOnlyList<string> redirectUris, bool confidential, IReadOnlyList<string>? postLogoutRedirectUris = null)
    {
        if (redirectUris.Count == 0) throw new ArgumentException("A client needs at least one redirect URI.", nameof(redirectUris));
        postLogoutRedirectUris ??= [];
        foreach (var uri in redirectUris.Concat(postLogoutRedirectUris))
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var u) || u.Fragment.Length > 0 || uri.Contains('\n')) throw new ArgumentException("A redirect URI is an absolute URI with no fragment.", nameof(redirectUris));
        var client = new OidcClient(Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant(), name, redirectUris, confidential, postLogoutRedirectUris);
        var secret = confidential ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).Replace('+', '-').Replace('/', '_').TrimEnd('=') : null;
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "INSERT INTO {table:oidc_client} (client_id, name, secret_hash, redirect_uris, created_at) VALUES ($id, $name, $secret, $uris, $at);");
            cmd.Bind(database, "id", client.ClientId);
            cmd.Bind(database, "name", name);
            cmd.Bind(database, "secret", secret is null ? DBNull.Value : Hash(secret));
            cmd.Bind(database, "uris", string.Join('\n', redirectUris));
            cmd.Bind(database, "at", database.Time.GetUtcNow());
            cmd.ExecuteNonQuery();
            if (postLogoutRedirectUris.Count == 0) return;
            using var logout = database.Command(Module, connection, "INSERT INTO {table:oidc_client_logout} (client_id, post_logout_redirect_uris) VALUES ($id, $uris);");
            logout.Bind(database, "id", client.ClientId);
            logout.Bind(database, "uris", string.Join('\n', postLogoutRedirectUris));
            logout.ExecuteNonQuery();
        });
        return (client, secret);
    }

    public OidcClient? Find(string clientId) => Read(clientId)?.Client;

    /// <summary>Removes a client: it can sign nobody in from then on, and the refresh tokens it holds are revoked. False when there is no such client.</summary>
    public bool Remove(string clientId) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var transaction = connection.BeginTransaction();
        using (var revoke = database.Command(Module, connection, transaction, "UPDATE {table:refresh_token} SET revoked_at = $at WHERE client_id = $id AND revoked_at IS NULL;"))
        {
            revoke.Bind(database, "at", database.Time.GetUtcNow());
            revoke.Bind(database, "id", clientId);
            revoke.ExecuteNonQuery();
        }
        using (var logout = database.Command(Module, connection, transaction, "DELETE FROM {table:oidc_client_logout} WHERE client_id = $id;"))
        {
            logout.Bind(database, "id", clientId);
            logout.ExecuteNonQuery();
        }
        using var cmd = database.Command(Module, connection, transaction, "DELETE FROM {table:oidc_client} WHERE client_id = $id;");
        cmd.Bind(database, "id", clientId);
        var removed = cmd.ExecuteNonQuery() == 1;
        transaction.Commit();
        return removed;
    });

    /// <summary>Every registered client, oldest first (the secret is never kept, only its hash).</summary>
    public IReadOnlyList<OidcClient> List()
    {
        var ids = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "SELECT client_id FROM {table:oidc_client} ORDER BY created_at, client_id;");
            using var reader = cmd.ExecuteReader();
            var found = new List<string>();
            while (reader.Read()) found.Add(reader.GetString(0));
            return found;
        });
        return [.. ids.Select(Find).OfType<OidcClient>()];
    }

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
        using var cmd = database.Command(Module, connection, "SELECT client_id, name, secret_hash, redirect_uris FROM {table:oidc_client} WHERE client_id = $id;");
        cmd.Bind(database, "id", clientId);
        using var reader = cmd.ExecuteReader();
        if (!reader.Read()) return ((OidcClient, string?)?)null;
        var hash = reader.IsDBNull(2) ? null : reader.GetString(2);
        var (id, name, uris) = (reader.GetString(0), reader.GetString(1), reader.GetString(3).Split('\n'));
        reader.Close();
        using var logout = database.Command(Module, connection, "SELECT post_logout_redirect_uris FROM {table:oidc_client_logout} WHERE client_id = $id;");
        logout.Bind(database, "id", clientId);
        var after = logout.ExecuteScalar() is string text ? text.Split('\n') : [];
        return (new OidcClient(id, name, uris, hash is not null, after), hash);
    });

    private static string Hash(string secret) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(secret))).ToLowerInvariant();
}
