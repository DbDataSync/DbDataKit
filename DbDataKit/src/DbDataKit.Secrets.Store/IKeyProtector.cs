namespace DbDataKit.Secrets.Store;

/// <summary>
/// How a data key is kept at rest. The key ring writes what <see cref="Protect"/> returns and hands it back to <see cref="Unprotect"/>; the protector's
/// <see cref="Kind"/> is written beside it so a key file is never read by a protector it was not made by.
/// </summary>
public interface IKeyProtector
{
    /// <summary>A stable number for this way of protecting a key (1 raw in a restricted file, 2 DPAPI for the machine, 3 DPAPI for the user).</summary>
    byte Kind { get; }

    string Description { get; }

    byte[] Protect(ReadOnlySpan<byte> key);

    byte[] Unprotect(ReadOnlySpan<byte> blob);
}

/// <summary>
/// The key as it is, in a file that only its owner can read: the protection is the operating system's file permissions (mode 0600 in a 0700 directory, the owner being the service user;
/// an access list for the identities given on Windows). The key ring checks the permissions every time it reads a key and refuses a file that others can read.
/// </summary>
public sealed class RestrictedFileKeyProtector : IKeyProtector
{
    public const byte KindValue = 1;

    public byte Kind => KindValue;

    public string Description => "a file only its owner can read";

    public byte[] Protect(ReadOnlySpan<byte> key) => key.ToArray();

    public byte[] Unprotect(ReadOnlySpan<byte> blob) => blob.ToArray();
}
