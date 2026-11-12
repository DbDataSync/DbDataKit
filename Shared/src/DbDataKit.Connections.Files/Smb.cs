using DbDataKit.Secrets;

namespace DbDataKit.Connections.Files;

/// <summary>
/// A file store over a folder that <c>System.IO</c> can reach (plan 27a IX05): the shared body of <see cref="SmbFileStore"/> (a Windows share as <c>\\host\share\folder</c>) and of any test that wants the same behaviour on a local folder. Every path is under <paramref name="baseFolder"/> and a path that leaves it is refused;
/// a write goes to <c>name.part</c> and is renamed when whole; a listing leaves out temporary names. What the operating system throws is classified by <see cref="SmbFailures"/>.
/// </summary>
public sealed class PathFileStore(string baseFolder) : IFileStore
{
    private string Physical(string path)
    {
        var resolved = FileStorePaths.Resolve("/", path);
        return resolved == "/" ? baseFolder : Path.Combine(baseFolder, resolved[1..].Replace('/', Path.DirectorySeparatorChar));
    }

    public Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default) => Run(() =>
    {
        var physical = Physical(folder);
        var logical = FileStorePaths.Resolve("/", folder);
        IReadOnlyList<FileEntry> entries = [.. new DirectoryInfo(physical).EnumerateFileSystemInfos()
            .Where(i => includeTemporary || i is DirectoryInfo || !FileNames.IsTemporary(i.Name))
            .Select(i => new FileEntry((logical == "/" ? "" : logical) + "/" + i.Name, i.Name, i is FileInfo f ? f.Length : 0, new DateTimeOffset(i.LastWriteTimeUtc, TimeSpan.Zero), i is DirectoryInfo))
            .OrderByDescending(e => e.IsDirectory).ThenBy(e => e.Name, StringComparer.Ordinal)];
        return entries;
    }, ct);

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Run<Stream>(() => new FileStream(Physical(path), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan), ct);

    public Task WriteAsync(string path, Stream content, CancellationToken ct = default) => RunAsync(async () =>
    {
        var target = Physical(path);
        var temporary = target + FileNames.PartSuffix;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            await content.CopyToAsync(output, ct).ConfigureAwait(false);
        File.Move(temporary, target, overwrite: true);   // the rename is the moment the file exists under its name
    }, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) => Run(() =>
    {
        var physical = Physical(path);
        if (!File.Exists(physical)) throw FileFailures.Missing();   // File.Delete of a missing file is silent; a connection says so
        File.Delete(physical);
        return 0;
    }, ct);

    private static Task<T> Run<T>(Func<T> call, CancellationToken ct) => RunAsync(() => Task.FromResult(call()), ct);

    private static async Task<T> RunAsync<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try { return await Task.Run(call, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new ConnectionFailureException(SmbFailures.Classify(e)); }
    }

    private static Task RunAsync(Func<Task> call, CancellationToken ct) => RunAsync(async () => { await call().ConfigureAwait(false); return 0; }, ct);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>
/// SMB (CIFS) with Windows sign-in (plan 27a IX05): the share <c>\\host\share</c> and the folder <c>file_server.root</c> in it, reached with <c>System.IO</c> as the <b>Windows identity of the process</b>. Windows negotiates Kerberos (a ticket for the server's name, from the domain controller, no secret anywhere in the connection) and signs and encrypts
/// as the server's policy says. This works on a Windows host only: elsewhere <see cref="OpenAsync"/> is the classified failure <see cref="NeedsWindows"/>. A connection that is not authorised, or has no ticket, is a rejected login (<see cref="SmbFailures"/>).
/// </summary>
public static class SmbFileStore
{
    public const string NeedsWindows = "smb-needs-windows";

    /// <summary>Whether this host can sign in to a share (Windows).</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    /// <summary>The UNC address of the connection's folder: <c>\\host\share</c> and <c>file_server.root</c> in it.</summary>
    public static string UncPath(ConnectionDefinition definition)
    {
        var server = definition.FileServer!;
        var root = server.Root.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return @"\\" + server.Host + @"\" + server.Share + (root.Length == 0 ? "" : @"\" + string.Join('\\', root));
    }

    public static Task<IFileStore> OpenAsync(ConnectionDefinition definition, ISecretResolver secrets, CancellationToken ct = default)
    {
        _ = secrets;   // nothing to read: the identity is Windows'
        if (!IsSupported) throw FileFailures.Unsupported(NeedsWindows);
        return OpenOnWindowsAsync(definition, ct);
    }

    private static async Task<IFileStore> OpenOnWindowsAsync(ConnectionDefinition definition, CancellationToken ct)
    {
        var unc = UncPath(definition);
        var store = new PathFileStore(unc);
        // reaching the root is what signs in: Windows asks the server for the share and proves who the process is
        await store.ListAsync("/", true, ct).ConfigureAwait(false);
        return store;
    }
}

/// <summary>
/// What <c>System.IO</c> throws for a share, as the shared failure kinds. The numbers are Windows' (an HResult of <c>0x8007xxxx</c> is Win32 error xxxx): a logon that failed or a Kerberos ticket that could not be had is a rejected login; a server or share that is not there, or not reachable, is the network
/// (or not found); access denied is a permission; a timeout is a timeout. Never the exception's own message (it holds the path).
/// </summary>
public static class SmbFailures
{
    public static ConnectionFailure Classify(Exception exception)
    {
        if (exception is UnauthorizedAccessException) return new(ConnectionFailureKind.Permission, "smb-access-denied", FileFailures.Engine);
        var hr = exception.HResult;
        var win32 = (hr & 0xFFFF0000) == 0x80070000 ? hr & 0xFFFF : -1;
        return (win32, (uint)hr) switch
        {
            (5, _) => new(ConnectionFailureKind.Permission, "smb-access-denied", FileFailures.Engine),
            (1326, _) or (1311, _) or (1244, _) or (1787, _) or (1219, _) or (1909, _) or (1331, _) or (1385, _) or (1386, _) => new(ConnectionFailureKind.Login, "smb-logon", FileFailures.Engine),
            (_, 0x8009030Cu) or (_, 0x8009030Eu) or (_, 0x80090311u) or (_, 0x80090322u) or (_, 0x8009030Du) => new(ConnectionFailureKind.Login, "smb-kerberos", FileFailures.Engine),   // SEC_E_LOGON_DENIED, NO_CREDENTIALS, NO_AUTHENTICATING_AUTHORITY, WRONG_PRINCIPAL (a ticket for another name), UNKNOWN_CREDENTIALS
            (_, 0x80090324u) => new(ConnectionFailureKind.Login, "smb-clock", FileFailures.Engine),   // SEC_E_TIME_SKEW: the clock is too far from the domain controller's
            (53, _) or (67, _) or (64, _) or (1231, _) or (1225, _) or (59, _) => new(ConnectionFailureKind.Network, "smb-network", FileFailures.Engine),
            (121, _) or (1460, _) => new(ConnectionFailureKind.Timeout, "smb-timeout", FileFailures.Engine),
            (2, _) or (3, _) => new(ConnectionFailureKind.NotFound, "smb-not-found", FileFailures.Engine),
            _ when exception is FileNotFoundException or DirectoryNotFoundException => new(ConnectionFailureKind.NotFound, "smb-not-found", FileFailures.Engine),
            _ => new(ConnectionFailureKind.Other, "smb-io", FileFailures.Engine),
        };
    }
}
