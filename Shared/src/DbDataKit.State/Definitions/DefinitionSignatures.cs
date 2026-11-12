using System.Data.Common;
using System.Security.Cryptography;

namespace DbDataKit.State;

/// <summary>What a signature check found: <see cref="Unsigned"/> (no signature), <see cref="Verified"/> or <see cref="Invalid"/> (there is one and it does not hold, or its key is unknown or revoked).</summary>
public enum SignatureState
{
    Unsigned,
    Verified,
    Invalid,
}

/// <summary>The outcome of <see cref="IVersionSignatureVerifier.Verify"/>: the state, who signed it when it verified (the publisher's key id), and a sentence for a refusal.</summary>
public sealed record SignatureVerdict(SignatureState State, string? Signer = null, string? Message = null)
{
    public string Word => State.ToString().ToLowerInvariant();
}

/// <summary>What a signature covers (plan 30 section 6.2): the manifest, and every artifact's path and SHA-256, plus the signature text the publisher sent, if any.</summary>
public sealed record VersionSignatureRequest(string Kind, string Name, string Manifest, IReadOnlyList<ArtifactRef> Artifacts, string? Signature);

/// <summary>The seam every publisher of a group calls (plan 30 PB03): the shared publish API and the runner's. The default, <see cref="NoSignatureVerifier"/>, finds every version unsigned; the trust store's verifier (<c>DbDataKit.State.Api</c>) checks Ed25519.</summary>
public interface IVersionSignatureVerifier
{
    SignatureVerdict Verify(VersionSignatureRequest request);
}

/// <summary>The default verifier: nothing is checked, so every version is recorded as unsigned.</summary>
public sealed class NoSignatureVerifier : IVersionSignatureVerifier
{
    public SignatureVerdict Verify(VersionSignatureRequest request) => new(SignatureState.Unsigned);
}

/// <summary>One publisher key a group trusts for a kind: its id (the first 16 hex digits of the SHA-256 of the public key), the 32-byte Ed25519 public key (base64url), a label, who added it and when, and when it was revoked.</summary>
public sealed record PublisherKey(string Kind, string KeyId, string PublicKey, string? Label, string AddedBy, DateTimeOffset AddedAt, DateTimeOffset? RevokedAt, string? RevokedBy)
{
    public bool Revoked => RevokedAt is not null;
}

/// <summary>
/// The group's trust store (plan 30 PB03): which publisher keys each kind accepts. A key is added by an administrator (or when a client pairs as a Publisher), rotated by adding the new one and revoking the old, and revoked at once:
/// a revoked key refuses new versions; versions it signed stay as they are.
/// </summary>
public sealed class DefinitionTrustStore(StateDatabase database)
{
    private const string Module = DefinitionsModule.Name;
    private const string Columns = "kind, key_id, public_key, label, added_by, added_at, revoked_at, revoked_by";

    /// <summary>The id of a public key: the first 16 hex digits of its SHA-256.</summary>
    public static string KeyIdOf(ReadOnlySpan<byte> publicKey) => Convert.ToHexString(SHA256.HashData(publicKey))[..16].ToLowerInvariant();

    /// <summary>Trusts the key for the kind; the same key again changes nothing (a revoked key stays revoked: add a new key). Returns the row.</summary>
    public PublisherKey Add(string kind, ReadOnlySpan<byte> publicKey, string? label, string by)
    {
        if (publicKey.Length != 32) throw new ArgumentException("An Ed25519 public key is 32 bytes.", nameof(publicKey));
        var id = KeyIdOf(publicKey);
        var text = Convert.ToBase64String(publicKey).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            if (Read(connection, kind, id) is { } existing) return existing;
            using var insert = database.Command(Module, connection, "INSERT INTO {table:definition_publisher_key} (kind, key_id, public_key, label, added_by, added_at) VALUES ($kind, $id, $key, $label, $by, $now);");
            insert.Bind(database, "kind", kind).Bind(database, "id", id).Bind(database, "key", text).Bind(database, "label", label, System.Data.DbType.String).Bind(database, "by", by).Bind(database, "now", now).ExecuteNonQuery();
            return Read(connection, kind, id)!;
        });
    }

    /// <summary>Revokes a key; false when there is none or it is revoked already.</summary>
    public bool Revoke(string kind, string keyId, string by)
    {
        var now = database.Time.GetUtcNow();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var update = database.Command(Module, connection, "UPDATE {table:definition_publisher_key} SET revoked_at = $now, revoked_by = $by WHERE kind = $kind AND key_id = $id AND revoked_at IS NULL;");
            update.Bind(database, "now", now).Bind(database, "by", by).Bind(database, "kind", kind).Bind(database, "id", keyId);
            return update.ExecuteNonQuery() > 0;
        });
    }

    public PublisherKey? Find(string kind, string keyId) => database.Retry(() => { using var connection = database.OpenConnection(); return Read(connection, kind, keyId); });

    public IReadOnlyList<PublisherKey> List(string kind) =>
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var command = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:definition_publisher_key}} WHERE kind = $kind ORDER BY added_at, key_id;");
            command.Bind(database, "kind", kind);
            using var reader = command.ExecuteReader();
            var found = new List<PublisherKey>();
            while (reader.Read()) found.Add(Row(reader));
            return (IReadOnlyList<PublisherKey>)found;
        });

    private PublisherKey? Read(DbConnection connection, string kind, string id)
    {
        using var command = database.Command(Module, connection, $"SELECT {Columns} FROM {{table:definition_publisher_key}} WHERE kind = $kind AND key_id = $id;");
        command.Bind(database, "kind", kind).Bind(database, "id", id);
        using var reader = command.ExecuteReader();
        return reader.Read() ? Row(reader) : null;
    }

    private static PublisherKey Row(DbDataReader r) =>
        new(r.GetString(0), r.GetString(1), r.GetString(2), r.IsDBNull(3) ? null : r.GetString(3), r.GetString(4), r.Time(5), r.NullableTime(6), r.IsDBNull(7) ? null : r.GetString(7));
}
