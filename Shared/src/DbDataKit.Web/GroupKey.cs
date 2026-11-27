using System.Security.Cryptography;
using System.Text;
using DbDataKit.Secrets;
using DbDataKit.State;

namespace DbDataKit.Web.Trust;

/// <summary>
/// AES-256-GCM in the form every group key of the suite writes: a version byte, the 12-byte nonce, the 16-byte tag and the ciphertext. The signing keys of an identity home (<c>KeySigningKeyProtector</c>), a Gateway group's
/// sessions and a Runner group's secrets are all protected this way, so a value written by an earlier version still opens.
/// </summary>
public static class GroupCipher
{
    private const byte Version = 1;
    private const int NonceSize = 12, TagSize = 16;

    /// <summary>A new random key of 32 bytes, as base64.</summary>
    public static string NewKey() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>The 32 bytes of a key written as <see cref="NewKey"/> writes it; <see cref="FormatException"/> naming what is wrong with any other text.</summary>
    public static byte[] Parse(string text, string what = "The key")
    {
        byte[] bytes;
        try { bytes = Convert.FromBase64String(text.Trim()); }
        catch (FormatException ex) { throw new FormatException($"{what} is not base64.", ex); }
        if (bytes.Length != 32) throw new FormatException($"{what} is 32 bytes; this one is {bytes.Length}.");
        return bytes;
    }

    public static byte[] Protect(byte[] key, byte[] plain)
    {
        var output = new byte[1 + NonceSize + TagSize + plain.Length];
        output[0] = Version;
        var nonce = output.AsSpan(1, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagSize);
        aes.Encrypt(nonce, plain, output.AsSpan(1 + NonceSize + TagSize), output.AsSpan(1 + NonceSize, TagSize));
        return output;
    }

    /// <summary>Throws <see cref="CryptographicException"/> for a value in another form (with <paramref name="notTheForm"/>) or one another key protected (the tag does not match).</summary>
    public static byte[] Unprotect(byte[] key, byte[] protectedValue, string notTheForm = "The value is not in the form a group key writes.")
    {
        if (protectedValue.Length < 1 + NonceSize + TagSize || protectedValue[0] != Version) throw new CryptographicException(notTheForm);
        var plain = new byte[protectedValue.Length - 1 - NonceSize - TagSize];
        using var aes = new AesGcm(key, TagSize);
        aes.Decrypt(protectedValue.AsSpan(1, NonceSize), protectedValue.AsSpan(1 + NonceSize + TagSize), protectedValue.AsSpan(1 + NonceSize, TagSize), plain);
        return plain;
    }
}

/// <summary>A group key that is missing, not a key, or not the group's; <see cref="Secret"/> names the secret that holds it on the instance.</summary>
public sealed class GroupKeyException(string secret, string message, GroupKeyProblem problem = GroupKeyProblem.WrongKey, Exception? inner = null) : InvalidOperationException(message, inner)
{
    public string Secret { get; } = secret;

    public GroupKeyProblem Problem { get; } = problem;
}

/// <summary>What is wrong with a group key on an instance.</summary>
public enum GroupKeyProblem
{
    /// <summary>The instance's store has no key and the key is not made there: the instance has not been joined to its group.</summary>
    Missing,

    /// <summary>The text is not a key (not base64 of 32 bytes).</summary>
    NotAKey,

    /// <summary>A key, but not the one the group's values are protected with.</summary>
    WrongKey,
}

/// <summary>
/// What a group key is called and what it says when something is wrong.
/// </summary>
/// <param name="Secret">The secret in the instance's own store that holds the key (base64 of 32 bytes), such as <c>dbdatakit:runner:state:group-key</c>.</param>
/// <param name="CheckName">The row of the <c>trust</c> module's <c>group_key</c> table that holds the group's check value.</param>
/// <param name="CheckText">The known text the check value is the encryption of.</param>
/// <param name="MakeWhenMissing">True: an instance whose store has no key makes one (the Gateway: the first instance of a group); false: the key is handed to the instance (the Runner: by the console when it joins it).</param>
/// <param name="Missing">What an instance without the key says (when <paramref name="MakeWhenMissing"/> is false).</param>
/// <param name="WrongKey">What an instance whose key is not the group's says.</param>
/// <param name="NotAKey">What an instance whose secret is not a key says, given the reason.</param>
public sealed record GroupKeySpec(string Secret, string CheckName, string CheckText, bool MakeWhenMissing, Func<string>? Missing = null, Func<string>? WrongKey = null, Func<string, string>? NotAKey = null)
{
    internal string MissingText => Missing?.Invoke() ?? $"This instance does not hold the group's key {Secret}: it has not been joined to its group.";
    internal string WrongKeyText => WrongKey?.Invoke() ?? $"The group's values are protected with another key than the one in {Secret}.";
    internal string NotAKeyText(string reason) => NotAKey?.Invoke(reason) ?? $"The secret {Secret} is not a key: {reason}";
}

/// <summary>
/// The key a group of instances that share one state database protects what it keeps there with (AES-256-GCM, <see cref="GroupCipher"/>): every instance holds a copy in its own secret store under <see cref="GroupKeySpec.Secret"/>,
/// and the group's <c>trust</c> state holds a check value (a known text encrypted with the key), so an instance given another key is refused naming the secret instead of failing value by value. The first key the group sees is
/// its key: the first instance to load or be given one writes the check value. Reused by the Gateway (sessions, its clients' secrets) and the Runner (the logins sent to the group, plan 28c E20d).
/// </summary>
/// <param name="state">The group's state (the <c>trust</c> module's <c>group_key</c> table).</param>
/// <param name="store">The instance's own secret store, opened when the key is first needed; it is read as an <see cref="ISecretProvider"/> too.</param>
/// <param name="spec">What the key is called and what it says.</param>
public sealed class GroupKey(StateDatabase state, Func<ISecretStore> store, GroupKeySpec spec)
{
    private readonly object _gate = new();
    private byte[]? _key;

    public GroupKeySpec Spec => spec;

    public string Secret => spec.Secret;

    public byte[] Protect(byte[] value) => GroupCipher.Protect(Load(), value);

    /// <summary>Throws <see cref="CryptographicException"/> for a value this key did not protect (or that was changed), <see cref="GroupKeyException"/> when the instance has no usable key.</summary>
    public byte[] Unprotect(byte[] value) => GroupCipher.Unprotect(Load(), value);

    /// <summary>Loads the key now (or makes it, when the spec says so), checking it against the group's; a start calls it, so a wrong key stops the instance there.</summary>
    public void Ensure() => _ = Load();

    /// <summary>Whether this instance holds the group's key (and it is the group's).</summary>
    public bool Held
    {
        get
        {
            try { _ = Load(); return true; }
            catch (GroupKeyException) { return false; }
        }
    }

    /// <summary>Whether the group has a key yet: some instance loaded or was given one.</summary>
    public bool GroupHasKey => ReadCheck() is not null;

    /// <summary>
    /// Gives this instance the group's key (the console does, when it joins the instance to the group): checked against the group's check value (written from this key when the group has none yet), then kept in the instance's
    /// store. Returns true when the instance already held that key. A text that is not a key or a key that is not the group's is refused with <see cref="GroupKeyException"/> and nothing is kept.
    /// </summary>
    public bool Join(string keyText)
    {
        byte[] key;
        try { key = GroupCipher.Parse(keyText); }
        catch (FormatException ex) { throw new GroupKeyException(spec.Secret, spec.NotAKeyText(ex.Message), GroupKeyProblem.NotAKey, inner: ex); }
        Check(key, made: false, opened: null);
        lock (_gate)
        {
            var opened = store();
            var reference = SecretRef.Parse("secret:" + spec.Secret);
            using (var held = Reader(opened).TryResolveAsync(reference).AsTask().GetAwaiter().GetResult())
            {
                if (held is not null && Same(held.RevealString(), key))
                {
                    _key = key;
                    return true;
                }
            }

            using var value = SecretValue.From(Convert.ToBase64String(key));
            opened.SetAsync(SecretName.Parse(spec.Secret), value).GetAwaiter().GetResult();
            _key = key;
            return false;
        }
    }

    // every store of the suite can be read as well as written (EncryptedFileSecretStore is both)
    private static ISecretProvider Reader(ISecretStore opened) => opened as ISecretProvider ?? throw new InvalidOperationException($"The secret store {opened.GetType().Name} cannot be read.");

    private static bool Same(string text, byte[] key)
    {
        try { return CryptographicOperations.FixedTimeEquals(GroupCipher.Parse(text), key); }
        catch (FormatException) { return false; }
    }

    private byte[] Load()
    {
        lock (_gate)
        {
            if (_key is not null) return _key;
            var opened = store();
            var name = SecretName.Parse(spec.Secret);
            string text;
            var made = false;
            using (var held = Reader(opened).TryResolveAsync(SecretRef.Parse("secret:" + spec.Secret)).AsTask().GetAwaiter().GetResult())
            {
                if (held is null)
                {
                    if (!spec.MakeWhenMissing) throw new GroupKeyException(spec.Secret, spec.MissingText, GroupKeyProblem.Missing);
                    text = GroupCipher.NewKey();
                    using var value = SecretValue.From(text);
                    opened.SetAsync(name, value).GetAwaiter().GetResult();
                    made = true;
                }
                else text = held.RevealString();
            }

            byte[] key;
            try { key = GroupCipher.Parse(text); }
            catch (FormatException ex) { throw new GroupKeyException(spec.Secret, spec.NotAKeyText(ex.Message), GroupKeyProblem.NotAKey, inner: ex); }
            Check(key, made, opened);
            _key = key;
            return key;
        }
    }

    /// <summary>Compares <paramref name="key"/> with the group's check value, writing it first when the group has none; a key it was just made is removed again when it is not the group's.</summary>
    private void Check(byte[] key, bool made, ISecretStore? opened)
    {
        var plain = Encoding.UTF8.GetBytes(spec.CheckText);
        var mine = Convert.ToBase64String(GroupCipher.Protect(key, plain));
        var existing = ReadCheck() ?? WriteCheck(mine);
        try
        {
            if (!GroupCipher.Unprotect(key, Convert.FromBase64String(existing)).AsSpan().SequenceEqual(plain)) throw new CryptographicException("The check value differs.");
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            if (made) opened?.DeleteAsync(SecretName.Parse(spec.Secret)).GetAwaiter().GetResult();
            throw new GroupKeyException(spec.Secret, spec.WrongKeyText, inner: ex);
        }
    }

    private string? ReadCheck() => state.Retry(() =>
    {
        using var connection = state.OpenConnection();
        using var read = state.Command(TrustModule.Name, connection, "SELECT check_value FROM {table:group_key} WHERE name = $name;");
        read.Bind(state, "name", spec.CheckName);
        return read.ExecuteScalar() as string;
    });

    /// <summary>Writes the check value; when another instance wrote one first, that one is the group's.</summary>
    private string WriteCheck(string check) => state.Retry(() =>
    {
        using var connection = state.OpenConnection();
        try
        {
            using var insert = state.Command(TrustModule.Name, connection, "INSERT INTO {table:group_key} (name, check_value) VALUES ($name, $check);");
            insert.Bind(state, "name", spec.CheckName);
            insert.Bind(state, "check", check);
            insert.ExecuteNonQuery();
            return check;
        }
        catch (System.Data.Common.DbException)
        {
            using var again = state.Command(TrustModule.Name, connection, "SELECT check_value FROM {table:group_key} WHERE name = $name;");
            again.Bind(state, "name", spec.CheckName);
            return (string)again.ExecuteScalar()!;
        }
    });
}

/// <summary>
/// The secrets a group keeps in the state its instances share (the <c>trust</c> module's <c>group_secret</c> table), each encrypted with the group's key (<see cref="GroupKey"/>), never in the clear: what is written through one
/// instance is there for every instance that holds the key. Only names that start with <paramref name="prefix"/> are kept here. An instance without the key can list and delete names but neither reads nor writes a value: it is
/// refused with <see cref="GroupKeyException"/> naming the key.
/// </summary>
/// <param name="state">The state, looked up when first needed (opening it may itself need a secret).</param>
/// <param name="key">The group's key.</param>
/// <param name="prefix">The names this store keeps (<c>dbdatakit:gateway:managed-</c> for a Gateway's clients, <c>dbdatakit:</c> for every name).</param>
/// <param name="enabled">Whether the store is in use on this instance (a Gateway: only in a group).</param>
/// <param name="storeName">The provider name the chain reports.</param>
public sealed class GroupStateSecretStore(Func<StateDatabase> state, Func<GroupKey> key, string prefix, Func<bool>? enabled = null, string storeName = "group") : ISecretStore, ISecretProvider
{
    public string Name => storeName;

    public string Prefix => prefix;

    public bool Enabled => enabled?.Invoke() ?? true;

    public bool CanHandle(SecretRef reference) => reference.Kind == SecretRefKind.Secret && reference.Target.StartsWith(prefix, StringComparison.Ordinal) && Enabled;

    /// <summary>Whether the group holds a value under the name (its sealed form only is read; no key is needed).</summary>
    public bool Contains(string fullName) => Sealed(fullName) is not null;

    public ValueTask<SecretValue?> TryResolveAsync(SecretRef reference, CancellationToken ct = default)
    {
        if (!CanHandle(reference)) return ValueTask.FromResult<SecretValue?>(null);
        if (Sealed(reference.Target) is not { } stored) return ValueTask.FromResult<SecretValue?>(null);
        var plain = key().Unprotect(Convert.FromBase64String(stored));
        try { return ValueTask.FromResult<SecretValue?>(SecretValue.From(Encoding.UTF8.GetString(plain))); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private string? Sealed(string fullName)
    {
        var database = state();
        return database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(TrustModule.Name, connection, "SELECT value FROM {table:group_secret} WHERE name = $name;");
            cmd.Bind(database, "name", fullName);
            return cmd.ExecuteScalar() as string;
        });
    }

    public Task SetAsync(SecretName name, SecretValue value, CancellationToken ct = default)
    {
        var full = name.ToString();
        if (!full.StartsWith(prefix, StringComparison.Ordinal)) throw new ArgumentException($"The group keeps only the secrets whose names start with {prefix}.", nameof(name));
        var plain = Encoding.UTF8.GetBytes(value.RevealString());
        string sealedValue;
        try { sealedValue = Convert.ToBase64String(key().Protect(plain)); }
        finally { CryptographicOperations.ZeroMemory(plain); }
        var database = state();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var transaction = connection.BeginTransaction();
            using (var delete = database.Command(TrustModule.Name, connection, transaction, "DELETE FROM {table:group_secret} WHERE name = $name;"))
                delete.Bind(database, "name", full).ExecuteNonQuery();
            using (var insert = database.Command(TrustModule.Name, connection, transaction, "INSERT INTO {table:group_secret} (name, value) VALUES ($name, $value);"))
                insert.Bind(database, "name", full).Bind(database, "value", sealedValue).ExecuteNonQuery();
            transaction.Commit();
        });
        return Task.CompletedTask;
    }

    public Task DeleteAsync(SecretName name, CancellationToken ct = default)
    {
        var database = state();
        database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(TrustModule.Name, connection, "DELETE FROM {table:group_secret} WHERE name = $name;");
            cmd.Bind(database, "name", name.ToString()).ExecuteNonQuery();
        });
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SecretName> ListAsync(string prefix, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var database = state();
        var names = database.Retry(() =>
        {
            using var connection = database.OpenConnection();
            using var cmd = database.Command(TrustModule.Name, connection, "SELECT name FROM {table:group_secret} ORDER BY name;");
            using var reader = cmd.ExecuteReader();
            var found = new List<string>();
            while (reader.Read()) found.Add(reader.GetString(0));
            return found;
        });
        foreach (var name in names.Where(n => n.StartsWith(Prefix, StringComparison.Ordinal) && n.StartsWith(prefix, StringComparison.Ordinal)))
            if (SecretName.TryParse(name, out var parsed)) yield return parsed;
        await Task.CompletedTask;
    }
}
