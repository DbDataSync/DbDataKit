using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Secrets.Store;

/// <summary>
/// One key provided from outside, by a platform that already keeps it safe: a Docker or Kubernetes secret, a systemd credential. The file holds 32 raw bytes, or the key as base64 or hex text (a
/// trailing newline is ignored). Its id is a short hash of the key itself, so the same key always has the same id wherever it is mounted. There is no rotation here: the platform rotates it.
/// <para>The file's permissions are not judged: a mounted secret is typically 0444 inside its own mount. That is the platform's contract.</para>
/// </summary>
public sealed class ExternalKeyFile : IKeySource
{
    private readonly string _path;
    private readonly string _id;

    public ExternalKeyFile(string path)
    {
        _path = Path.GetFullPath(path);
        var key = Read();
        try { _id = "ext-" + Convert.ToHexString(SHA256.HashData(key))[..12].ToLowerInvariant(); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public string FilePath => _path;

    public string ActiveKeyId => _id;

    public IReadOnlyList<string> KeyIds() => [_id];

    public byte[] GetKey(string id)
    {
        var key = Read();
        var actual = "ext-" + Convert.ToHexString(SHA256.HashData(key))[..12].ToLowerInvariant();
        if (!string.Equals(id, actual, StringComparison.Ordinal))
        {
            CryptographicOperations.ZeroMemory(key);
            throw new KeyNotFoundException($"The key in '{_path}' is '{actual}', not '{id}': the key provided from outside is not the one these secrets were written with.");
        }
        return key;
    }

    private byte[] Read()
    {
        if (!File.Exists(_path)) throw new FileNotFoundException($"The secrets key file '{_path}' does not exist.", _path);
        var bytes = File.ReadAllBytes(_path);
        if (bytes.Length == KeyRing.KeyLength) return bytes;
        var text = Encoding.UTF8.GetString(bytes).Trim();
        CryptographicOperations.ZeroMemory(bytes);
        byte[]? decoded = null;
        if (text.Length == KeyRing.KeyLength * 2 && text.All(Uri.IsHexDigit)) decoded = Convert.FromHexString(text);
        else
        {
            try { decoded = Convert.FromBase64String(text); } catch (FormatException) { }
        }
        if (decoded is { Length: KeyRing.KeyLength }) return decoded;
        throw new InvalidDataException($"'{_path}' is not a {KeyRing.KeyLength * 8}-bit key: it must hold {KeyRing.KeyLength} raw bytes, or the key as base64 or hex text.");
    }
}
