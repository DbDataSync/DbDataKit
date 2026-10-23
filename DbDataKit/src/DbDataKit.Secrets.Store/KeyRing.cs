using System.Security.Cryptography;
using System.Text;
using DbDataKit.Security.Acl;

namespace DbDataKit.Secrets.Store;

/// <summary>
/// The data keys of a secret store, one file per key in a directory only the service can reach: <c>keys/&lt;id&gt;.key</c>, and <c>keys/active</c> naming the one new values use. A key file is
/// <c>DBKEY</c>, a format byte, the kind of <see cref="IKeyProtector"/> that wrote it, and what that protector returned. Every read checks the permissions first and refuses a file or
/// directory others can read (<see cref="KeyPermissionException"/>); files are written restricted before any content is in them, and replaced atomically.
/// </summary>
public sealed class KeyRing : IKeySource
{
    public const int KeyLength = 32;

    private static readonly byte[] Magic = "DBKEY"u8.ToArray();
    private const byte Format = 1;
    private const UnixFileMode GroupAndOther = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    private readonly string _directory;
    private readonly IKeyProtector _protector;
    private readonly string[] _identities;

    /// <param name="directory">The key directory (for example <c>&lt;data&gt;/secrets/keys</c>).</param>
    /// <param name="protector">How a key is kept at rest.</param>
    /// <param name="windowsIdentities">Who may reach the files on Windows (default: the current user, SYSTEM and Administrators); ignored elsewhere.</param>
    public KeyRing(string directory, IKeyProtector? protector = null, IEnumerable<string>? windowsIdentities = null)
    {
        _directory = Path.GetFullPath(directory);
        _protector = protector ?? new RestrictedFileKeyProtector();
        _identities = (windowsIdentities ?? ["CurrentUser", "SYSTEM", "Administrators"]).ToArray();
    }

    public string DirectoryPath => _directory;

    public string ActiveKeyId =>
        ReadActive() ?? throw new InvalidOperationException($"The key ring '{_directory}' has no active key; create one first.");

    /// <summary>True when the ring has at least one key.</summary>
    public bool HasKeys => KeyIds().Count > 0;

    public IReadOnlyList<string> KeyIds()
    {
        if (!System.IO.Directory.Exists(_directory)) return [];
        return System.IO.Directory.EnumerateFiles(_directory, "*.key").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Makes sure the ring has an active key, creating its first one when it has none. Safe to call from several threads and processes at
    /// once on a fresh directory (a server and the workers it starts, all opening the store for the first time): they take turns on a lock
    /// file beside the key directory, so exactly one key is made and every caller ends up with the same active key. A ring that has keys
    /// but no <c>active</c> file (an interrupted first start) gets its first key activated rather than a new one.
    /// </summary>
    public void EnsureActiveKey()
    {
        if (ReadActive() is not null) return;
        EnsureDirectory();
        using var gate = AcquireCreationLock();
        if (ReadActive() is not null) return;
        var ids = KeyIds();
        if (ids.Count == 0) CreateKey(activate: true);
        else Activate(ids[0]);
    }

    // Beside the key directory, not in it: VerifyAll checks every file in the directory, and this one holds nothing.
    private FileStream AcquireCreationLock()
    {
        var path = _directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + ".lock";
        for (var attempt = 0; ; attempt++)
        {
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose); }
            catch (IOException) when (attempt < 400) { Thread.Sleep(25); }
            catch (IOException ex) { throw new InvalidOperationException($"Could not lock the key ring '{_directory}' to create its first key: another process is holding '{path}'.", ex); }
        }
    }

    /// <summary>Makes a new random key and returns its id. It becomes the active key when the ring had none or when <paramref name="activate"/> says so.</summary>
    public string CreateKey(bool activate = false)
    {
        EnsureDirectory();
        var key = RandomNumberGenerator.GetBytes(KeyLength);
        try
        {
            var id = Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
            var blob = _protector.Protect(key);
            var content = new byte[Magic.Length + 2 + blob.Length];
            Magic.CopyTo(content, 0);
            content[Magic.Length] = Format;
            content[Magic.Length + 1] = _protector.Kind;
            blob.CopyTo(content, Magic.Length + 2);
            WriteAtomically(KeyPath(id), content);
            if (activate || ReadActive() is null) Activate(id);
            return id;
        }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    /// <summary>Makes <paramref name="id"/> the key new values are encrypted with.</summary>
    public void Activate(string id)
    {
        if (!File.Exists(KeyPath(id))) throw new KeyNotFoundException($"The key ring '{_directory}' has no key '{id}'.");
        EnsureDirectory();
        WriteAtomically(Path.Combine(_directory, "active"), Encoding.UTF8.GetBytes(id));
    }

    public byte[] GetKey(string id)
    {
        ValidateId(id);
        var path = KeyPath(id);
        if (!File.Exists(path)) throw new KeyNotFoundException($"The key ring '{_directory}' has no key '{id}'.");
        VerifyPermissions(_directory);
        VerifyPermissions(path);
        var content = File.ReadAllBytes(path);
        if (content.Length < Magic.Length + 2 || !content.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new InvalidDataException($"'{path}' is not a secret key file.");
        if (content[Magic.Length] != Format) throw new InvalidDataException($"'{path}' is key file format {content[Magic.Length]}; this build reads format {Format}.");
        if (content[Magic.Length + 1] != _protector.Kind)
            throw new InvalidDataException($"'{path}' was written by key protection kind {content[Magic.Length + 1]}, but this host uses {_protector.Description} (kind {_protector.Kind}). A key file made on another machine or by another protection cannot be read here.");
        var key = _protector.Unprotect(content.AsSpan(Magic.Length + 2));
        if (key.Length != KeyLength) { CryptographicOperations.ZeroMemory(key); throw new InvalidDataException($"'{path}' does not hold a {KeyLength * 8}-bit key."); }
        return key;
    }

    /// <summary>Removes a key. The active key cannot be removed.</summary>
    public void DeleteKey(string id)
    {
        ValidateId(id);
        if (string.Equals(ReadActive(), id, StringComparison.Ordinal)) throw new InvalidOperationException("The active key cannot be deleted; activate another one first.");
        var path = KeyPath(id);
        if (File.Exists(path)) File.Delete(path);
    }

    /// <summary>Checks the directory and every key file for permissions others could read through, without changing anything. Throws <see cref="KeyPermissionException"/>.</summary>
    public void VerifyAll()
    {
        if (!System.IO.Directory.Exists(_directory)) return;
        VerifyPermissions(_directory);
        foreach (var file in System.IO.Directory.EnumerateFiles(_directory)) VerifyPermissions(file);
    }

    private string KeyPath(string id) => Path.Combine(_directory, id + ".key");

    private string? ReadActive()
    {
        var path = Path.Combine(_directory, "active");
        if (!File.Exists(path)) return null;
        VerifyPermissions(path);
        var id = File.ReadAllText(path, Encoding.UTF8).Trim();
        return id.Length == 0 ? null : id;
    }

    private static void ValidateId(string id)
    {
        if (id.Length == 0 || id.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_')))
            throw new ArgumentException($"'{id}' is not a key id (letters, digits, '-' and '_').", nameof(id));
    }

    private void EnsureDirectory()
    {
        AclHelper.EnsureRestrictedDirectory(_directory, _identities);
        VerifyPermissions(_directory);
    }

    private void WriteAtomically(string path, byte[] content)
    {
        var temp = path + "." + Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant() + ".tmp";
        try
        {
            AclHelper.WriteAllBytesRestricted(temp, content, _identities);
            File.Move(temp, path, overwrite: true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    private void VerifyPermissions(string path)
    {
        var isDirectory = System.IO.Directory.Exists(path);
        var fix = isDirectory
            ? $"Run: chmod 700 '{path}' (and keep the key files 600), and make sure the service user owns it."
            : $"Run: chmod 600 '{path}', and make sure the service user owns it.";
        if (OperatingSystem.IsWindows())
        {
            var report = AclHelper.Verify(path, _identities);
            if (!report.InSync)
                throw new KeyPermissionException(path, "has an access list that lets others in (" + string.Join("; ", report.Drift.Select(d => d.Description)) + ")",
                    "Restrict it to the service account, SYSTEM and Administrators.");
            return;
        }
        var mode = File.GetUnixFileMode(path);
        if ((mode & GroupAndOther) != 0)
            throw new KeyPermissionException(path, $"is readable or writable by others (mode {Convert.ToString((int)mode, 8)})", fix);
        if (UnixOwnership.IsOwnedByCurrentUser(path) == false)
            throw new KeyPermissionException(path, "is owned by another user", fix);
    }
}
