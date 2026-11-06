using System.Buffers.Text;
using System.Security.Cryptography;
using DbDataKit.State;

namespace DbDataKit.Identity;

/// <summary>The keys the home signs tokens with: ES256 (ECDSA P-256), which every OpenID Connect library supports. One is current; a retired one stays out of the JWKS.</summary>
public sealed class ModuleSigningKeys(StateDatabase database, ISigningKeyProtector protector)
{
    private const string Module = IdentityModule.Name;

    public sealed record Key(string Kid, ECDsa Private);

    /// <summary>The newest key that is not retired, made on first use.</summary>
    public Key Current()
    {
        var found = Load(activeOnly: true).FirstOrDefault();
        return found ?? Rotate();
    }

    /// <summary>Makes a new current key. The older ones keep verifying (they stay in the JWKS) until <see cref="Retire"/>.</summary>
    public Key Rotate()
    {
        var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var kid = Convert.ToHexString(SHA256.HashData(ecdsa.ExportSubjectPublicKeyInfo()))[..16].ToLowerInvariant();
        var stored = Convert.ToBase64String(protector.Protect(ecdsa.ExportPkcs8PrivateKey()));
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(Module, connection, "INSERT INTO {table:signing_key} (kid, private_key, created_at) VALUES ($kid, $key, $at);");
            cmd.Bind(database, "kid", kid);
            cmd.Bind(database, "key", stored);
            cmd.Bind(database, "at", database.Time.GetUtcNow());
            cmd.ExecuteNonQuery();
        });
        return new Key(kid, ecdsa);
    }

    /// <summary>The key with that id, if it is not retired (what verifies a token the home issued).</summary>
    public Key? Find(string kid) => Load(activeOnly: true).FirstOrDefault(k => k.Kid == kid);

    public void Retire(string kid) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, "UPDATE {table:signing_key} SET retired_at = $at WHERE kid = $kid AND retired_at IS NULL;");
        cmd.Bind(database, "kid", kid);
        cmd.Bind(database, "at", database.Time.GetUtcNow());
        cmd.ExecuteNonQuery();
    });

    /// <summary>The public halves of every key that is not retired, as a JSON Web Key Set.</summary>
    public object Jwks() => new
    {
        keys = Load(activeOnly: true).Select(k =>
        {
            var p = k.Private.ExportParameters(false);
            return new { kty = "EC", crv = "P-256", use = "sig", alg = "ES256", kid = k.Kid, x = Base64Url.EncodeToString(p.Q.X!), y = Base64Url.EncodeToString(p.Q.Y!) };
        }).ToList(),
    };

    private List<Key> Load(bool activeOnly) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(Module, connection, activeOnly
            ? "SELECT kid, private_key FROM {table:signing_key} WHERE retired_at IS NULL ORDER BY created_at DESC, kid;"
            : "SELECT kid, private_key FROM {table:signing_key} ORDER BY created_at DESC, kid;");
        using var reader = cmd.ExecuteReader();
        var keys = new List<Key>();
        while (reader.Read())
        {
            var ecdsa = ECDsa.Create();
            ecdsa.ImportPkcs8PrivateKey(protector.Unprotect(Convert.FromBase64String(reader.GetString(1))), out _);
            keys.Add(new Key(reader.GetString(0), ecdsa));
        }
        return keys;
    });
}
