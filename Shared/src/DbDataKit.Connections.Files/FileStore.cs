using System.Security.Cryptography;
using DbDataKit.Dialect;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Files;

/// <summary>One file or folder of a file store: its path (under the connection's root, with <c>/</c> separators), size, when it was last changed and whether it is a folder. A caller that wants to know a file is finished compares size and time across two looks (plan 27a IX07).</summary>
public sealed record FileEntry(string Path, string Name, long Size, DateTimeOffset Modified, bool IsDirectory);

/// <summary>
/// What a connection of a file kind does (plan 27a IX04 to IX06), the same for SFTP, SCP, FTPS, SMB and object storage so a caller never asks which: list a folder, read a file, write a file, delete one. Every path is under the connection's root and a path that leaves it is refused.
/// <b>A partial file is never picked up:</b> <see cref="WriteAsync"/> writes under a temporary name and renames when the last byte is there, and <see cref="ListAsync"/> leaves out names that look like a producer's temporary file (<see cref="FileNames.IsTemporary"/>).
/// A failure is a <see cref="ConnectionFailureException"/> with the shared <see cref="ConnectionFailure"/> (so a retry knows temporary from permanent), or a <see cref="PinMismatchException"/> when the server is not the one pinned.
/// </summary>
public interface IFileStore : IAsyncDisposable
{
    /// <summary>The entries of a folder, folders first, by name; temporary names left out unless <paramref name="includeTemporary"/>. A protocol that cannot list (SCP) says so as <see cref="ConnectionFailureKind.Unsupported"/>.</summary>
    Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default);

    /// <summary>The content of a file, to read and dispose. A missing file is <see cref="ConnectionFailureKind.NotFound"/>.</summary>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct = default);

    /// <summary>Writes the file (replacing one that is there, and making the folders above it) so that no reader of the store sees it half written.</summary>
    Task WriteAsync(string path, Stream content, CancellationToken ct = default);

    Task DeleteAsync(string path, CancellationToken ct = default);
}

/// <summary>The names that mean "being written": a reader that lists a store skips them and picks the file up when the writer renames it.</summary>
public static class FileNames
{
    /// <summary>The suffix <see cref="IFileStore.WriteAsync"/> writes under until the file is whole.</summary>
    public const string PartSuffix = ".part";

    private static readonly string[] Suffixes = [PartSuffix, ".tmp", ".temp", ".partial", ".filepart", ".crdownload", ".uploading", "~"];

    public static bool IsTemporary(string name) => Suffixes.Any(s => name.EndsWith(s, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Paths of a file store: under the connection's root, <c>/</c> separated, never above it.</summary>
public static class FileStorePaths
{
    /// <summary>The absolute path on the server for a path of the connection (relative to <paramref name="root"/>); a path with <c>..</c> that would leave the root is refused.</summary>
    public static string Resolve(string root, string path)
    {
        var parts = new List<string>();
        foreach (var segment in (path ?? "").Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (parts.Count == 0) throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Permission, "path-outside-root", FileFailures.Engine));
                parts.RemoveAt(parts.Count - 1);
            }
            else parts.Add(segment);
        }

        var baseParts = root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return "/" + string.Join('/', baseParts.Concat(parts));
    }

    /// <summary>The path of a server-side absolute path as the connection shows it: relative to the root, with a leading slash.</summary>
    public static string Relative(string root, string absolute)
    {
        var baseText = "/" + string.Join('/', root.Split('/', StringSplitOptions.RemoveEmptyEntries));
        var text = absolute.Replace('\\', '/');
        return baseText != "/" && text.StartsWith(baseText, StringComparison.Ordinal) ? (text[baseText.Length..] is { Length: > 0 } rest ? rest : "/") : text;
    }

    /// <summary>The folder and the name of a resolved path.</summary>
    public static (string Folder, string Name) Split(string resolved)
    {
        var i = resolved.LastIndexOf('/');
        return (i <= 0 ? "/" : resolved[..i], resolved[(i + 1)..]);
    }
}

/// <summary>The server is not the one the connection is pinned to: a host key or a certificate whose fingerprint is not in the pinned list. The message names the server and both fingerprints (they are public) and what to do; it is permanent and nothing is sent to the server.</summary>
public sealed class PinMismatchException : Exception
{
    public PinMismatchException(string what, string host, string offered, IReadOnlyList<string> pinned)
        : base($"The {what} of {host} is {offered}, which is not one of the pinned ones ({(pinned.Count == 0 ? "none" : string.Join(", ", pinned))}). "
             + "If the server was reinstalled or its key was replaced, check the new fingerprint with its administrator and pin it; otherwise do not connect: this may not be the server you mean.")
    {
        What = what;
        Host = host;
        Offered = offered;
        Pinned = pinned;
        Failure = new ConnectionFailure(ConnectionFailureKind.Tls, what == "host key" ? "host-key-refused" : "certificate-refused", FileFailures.Engine);
    }

    public string What { get; }
    public string Host { get; }

    /// <summary>The fingerprint the server showed.</summary>
    public string Offered { get; }

    public IReadOnlyList<string> Pinned { get; }
    public ConnectionFailure Failure { get; }
}

/// <summary>A secret the connection names is not set (a password, a private key): a permanent login failure that names the reference, which holds no value.</summary>
public sealed class FileSecretNotSetException(string reference) : Exception($"The secret `{reference}` is not set.")
{
    public string Reference { get; } = reference;
    public ConnectionFailure Failure { get; } = new(ConnectionFailureKind.Login, "secret-not-set", FileFailures.Engine);
}

internal static class SecretReader
{
    public static async Task<string> ReadAsync(ISecretResolver secrets, string reference, CancellationToken ct)
    {
        try
        {
            using var value = await secrets.ResolveAsync(SecretRef.Parse(reference), ct).ConfigureAwait(false);
            return value.RevealString();
        }
        catch (SecretNotFoundException) { throw new FileSecretNotSetException(reference); }
    }
}

/// <summary>Classifies what a file protocol throws as the shared <see cref="ConnectionFailure"/> (so a retry reads <see cref="ConnectionFailure.IsTemporary"/>); never the driver's own message, which can hold a path or a host.</summary>
public static class FileFailures
{
    public static readonly EngineId Engine = new("files");

    public static ConnectionFailure Classify(Exception exception, CancellationToken caller = default)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case ConnectionFailureException known: return known.Failure;
                case PinMismatchException pin: return pin.Failure;
                case FileSecretNotSetException unset: return unset.Failure;
                case OperationCanceledException when caller.IsCancellationRequested: return new(ConnectionFailureKind.Other, "canceled", Engine);
                case TimeoutException: return new(ConnectionFailureKind.Timeout, nameof(TimeoutException), Engine);
                case System.Net.Sockets.SocketException se: return new(ConnectionFailureKind.Network, "socket-" + (int)se.SocketErrorCode, Engine);
                case System.Security.Authentication.AuthenticationException: return new(ConnectionFailureKind.Tls, "tls", Engine);
                case FileNotFoundException or DirectoryNotFoundException: return new(ConnectionFailureKind.NotFound, "not-found", Engine);
                case UnauthorizedAccessException: return new(ConnectionFailureKind.Permission, "permission", Engine);
            }
        }

        return Protocol.Classify(exception) ?? new(ConnectionFailureKind.Other, exception.GetType().Name, Engine);
    }

    /// <summary>The failure of a path that is not there or not allowed, thrown by the stores.</summary>
    internal static ConnectionFailureException Missing(string code = "not-found") => new(new ConnectionFailure(ConnectionFailureKind.NotFound, code, Engine));

    internal static ConnectionFailureException Unsupported(string code) => new(new ConnectionFailure(ConnectionFailureKind.Unsupported, code, Engine));
}

/// <summary>Fingerprints as the tools print them.</summary>
public static class Fingerprints
{
    /// <summary>An SSH host key as <c>ssh-keygen -lf</c> prints it: <c>SHA256:</c> and the base64 of the hash of the key blob without padding.</summary>
    public static string OfHostKey(byte[] hostKey) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(hostKey)).TrimEnd('=');

    /// <summary>A certificate's SHA-256 as 64 upper-case hex digits (what <c>certificate_sha256</c> pins).</summary>
    public static string OfCertificate(byte[] rawCertificate) => Convert.ToHexString(SHA256.HashData(rawCertificate));
}
