using System.Buffers.Text;
using System.Security.Cryptography;
using DbDataKit.State;

namespace DbDataKit.Identity;

/// <summary>How the host protects a private key at rest (the console's protected store, DPAPI, a key file). The home never stores a key unprotected, so there is no default.</summary>
public interface ISigningKeyProtector
{
    byte[] Protect(byte[] privateKey);
    byte[] Unprotect(byte[] protectedKey);
}

/// <summary>The keys the home signs tokens with: ES256 (ECDSA P-256), which every OpenID Connect library supports. One is current; a retired one stays out of the JWKS.</summary>
public sealed class SigningKeys(StateDatabase database, ISigningKeyProtector protector)
{
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
            using var cmd = database.Command(connection, "INSERT INTO SigningKeys (Kid, PrivateKey, CreatedAtUtc) VALUES ($kid, $key, $at);");
            cmd.Bind(database, "kid", kid);
            cmd.Bind(database, "key", stored);
            cmd.Bind(database, "at", DateTimeOffset.UtcNow.ToString("O"));
            cmd.ExecuteNonQuery();
        });
        return new Key(kid, ecdsa);
    }

    /// <summary>The key with that id, if it is not retired (what verifies a token the home issued).</summary>
    public Key? Find(string kid) => Load(activeOnly: true).FirstOrDefault(k => k.Kid == kid);

    public void Retire(string kid) => database.Retry(() =>
    {
        using var connection = database.OpenConnection();
        using var cmd = database.Command(connection, "UPDATE SigningKeys SET RetiredAtUtc = $at WHERE Kid = $kid AND RetiredAtUtc IS NULL;");
        cmd.Bind(database, "kid", kid);
        cmd.Bind(database, "at", DateTimeOffset.UtcNow.ToString("O"));
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
        using var cmd = database.Command(connection, activeOnly
            ? "SELECT Kid, PrivateKey FROM SigningKeys WHERE RetiredAtUtc IS NULL ORDER BY CreatedAtUtc DESC, Kid;"
            : "SELECT Kid, PrivateKey FROM SigningKeys ORDER BY CreatedAtUtc DESC, Kid;");
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
