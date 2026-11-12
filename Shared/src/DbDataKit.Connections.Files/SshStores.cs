using System.Text;
using DbDataKit.Secrets;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace DbDataKit.Connections.Files;

/// <summary>The connection of an SSH file store: the server, who signs in and how, what it is pinned to. Shared by SFTP and SCP so both refuse the same servers.</summary>
internal static class Ssh
{
    public const int DefaultPort = 22;

    /// <summary>
    /// The connection information of <paramref name="definition"/>: the user's password or private key (read now from the secrets), the timeouts of the connection. The host key is checked on connect by <see cref="Pin"/>.
    /// </summary>
    public static async Task<ConnectionInfo> InfoAsync(ConnectionDefinition definition, ISecretResolver secrets, CancellationToken ct)
    {
        var server = definition.FileServer!;
        var signIn = definition.SignIn ?? new SignInSpec();
        AuthenticationMethod method;
        if (signIn.Method == "ssh_key")
        {
            var text = await SecretReader.ReadAsync(secrets, signIn.PrivateKeySecret!, ct).ConfigureAwait(false);
            var passphrase = signIn.PassphraseSecret is null ? null : await SecretReader.ReadAsync(secrets, signIn.PassphraseSecret, ct).ConfigureAwait(false);
            try
            {
                using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n").Trim() + "\n"));
                method = new PrivateKeyAuthenticationMethod(signIn.User!, new PrivateKeyFile(stream, passphrase));
            }
            catch (Exception e) when (e is SshException or ArgumentException or FormatException or InvalidOperationException)
            {
                // what failed is told by its kind, never by the library's message (it can hold part of the key)
                throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Login, "ssh-private-key", FileFailures.Engine));
            }
        }
        else if (signIn.Method == "basic") method = new PasswordAuthenticationMethod(signIn.User!, await SecretReader.ReadAsync(secrets, signIn.PasswordSecret!, ct).ConfigureAwait(false));
        else throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Login, "ssh-no-sign-in", FileFailures.Engine));

        return new ConnectionInfo(server.Host, server.Port ?? DefaultPort, signIn.User!, method)
        {
            Timeout = TimeSpan.FromSeconds(definition.Timeouts.ConnectS),
            RetryAttempts = 1,
        };
    }

    /// <summary>
    /// Wires the host-key check of a client: the key the server shows is trusted only when its fingerprint is one of <c>file_server.host_keys</c> (or <c>tls.verify: false</c> says any key is). The fingerprint offered is kept so that a refusal can name it;
    /// <see cref="Pin.Check"/> turns the failed connect into the <see cref="PinMismatchException"/>.
    /// </summary>
    public sealed class Pin
    {
        private readonly ConnectionDefinition _definition;
        private readonly bool _probe;

        public Pin(BaseClient client, ConnectionDefinition definition, bool probe = false)
        {
            _definition = definition;
            _probe = probe;
            client.HostKeyReceived += (_, e) =>
            {
                Offered = Fingerprints.OfHostKey(e.HostKey);
                var pinned = definition.FileServer!.HostKeys;
                e.CanTrust = !probe && (!definition.Tls.EffectiveVerify || pinned.Contains(Offered, StringComparer.Ordinal));
            };
        }

        /// <summary>The fingerprint of the key the server showed (set once the server was reached).</summary>
        public string? Offered { get; private set; }

        /// <summary>Throws <see cref="PinMismatchException"/> when the server showed a key that is not pinned (a failed connect after that is because of it); returns for any other failure.</summary>
        public void Check()
        {
            if (!_probe && Offered is { } offered && _definition.Tls.EffectiveVerify && !_definition.FileServer!.HostKeys.Contains(offered, StringComparer.Ordinal))
                throw new PinMismatchException("host key", _definition.FileServer.Host, offered, _definition.FileServer.HostKeys);
        }
    }

    /// <summary>Connects with the pin checked; a refused key is reported as such, whatever the library says about the failed negotiation.</summary>
    public static async Task ConnectAsync(BaseClient client, Pin pin, CancellationToken ct)
    {
        try { await client.ConnectAsync(ct).ConfigureAwait(false); }
        catch (Exception) when (pin.Offered is not null)
        {
            pin.Check();
            throw;
        }
    }

    /// <summary>What the server at the connection's address shows as its host key, without signing in: the fingerprint to pin (a person confirms it with the server's administrator). Null when the server could not be reached.</summary>
    public static async Task<string?> ProbeAsync(ConnectionDefinition definition, CancellationToken ct = default)
    {
        var server = definition.FileServer!;
        using var client = new SshClient(new ConnectionInfo(server.Host, server.Port ?? DefaultPort, "probe", new NoneAuthenticationMethod("probe")) { Timeout = TimeSpan.FromSeconds(definition.Timeouts.ConnectS) });
        var pin = new Pin(client, definition, probe: true);
        try { await client.ConnectAsync(ct).ConfigureAwait(false); }
        catch (Exception) { /* the key is refused on purpose: nothing is sent after the key exchange */ }
        return pin.Offered;
    }

    public static string Quote(string path) => "'" + path.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}

/// <summary>SFTP (plan 27a IX04): list, read, write and delete over SSH with SSH.NET, as a thin wrapper. A write goes to <c>name.part</c> and is renamed when whole.</summary>
public sealed class SftpFileStore : IFileStore
{
    private readonly SftpClient _client;
    private readonly string _root;

    private SftpFileStore(SftpClient client, string root) => (_client, _root) = (client, root);

    public static async Task<SftpFileStore> OpenAsync(ConnectionDefinition definition, ISecretResolver secrets, CancellationToken ct = default)
    {
        var info = await Ssh.InfoAsync(definition, secrets, ct).ConfigureAwait(false);
        var client = new SftpClient(info) { OperationTimeout = TimeSpan.FromSeconds(definition.Timeouts.CommandS) };
        var pin = new Ssh.Pin(client, definition);
        try
        {
            await Ssh.ConnectAsync(client, pin, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new SftpFileStore(client, definition.FileServer!.Root);
    }

    public Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default) => Run(() =>
    {
        var resolved = FileStorePaths.Resolve(_root, folder);
        IReadOnlyList<FileEntry> entries = [.. _client.ListDirectory(resolved)
            .Where(f => f.Name is not ("." or "..") && (includeTemporary || f.IsDirectory || !FileNames.IsTemporary(f.Name)))
            .Select(f => new FileEntry(FileStorePaths.Relative(_root, f.FullName), f.Name, f.IsDirectory ? 0 : f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero), f.IsDirectory))
            .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.Ordinal)];
        return entries;
    }, ct);

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Run<Stream>(() => _client.OpenRead(FileStorePaths.Resolve(_root, path)), ct);

    public Task WriteAsync(string path, Stream content, CancellationToken ct = default) => Run(() =>
    {
        var target = FileStorePaths.Resolve(_root, path);
        var temporary = target + FileNames.PartSuffix;
        var walked = "";
        foreach (var segment in FileStorePaths.Split(target).Folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            walked += "/" + segment;
            if (!_client.Exists(walked)) _client.CreateDirectory(walked);
        }

        _client.UploadFile(content, temporary, canOverride: true);
        try { _client.RenameFile(temporary, target, isPosix: true); }
        catch (SshException)
        {
            // a server without posix-rename: replace the old file the plain way
            if (_client.Exists(target)) _client.DeleteFile(target);
            _client.RenameFile(temporary, target);
        }

        return 0;
    }, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) => Run(() => { _client.DeleteFile(FileStorePaths.Resolve(_root, path)); return 0; }, ct);

    /// <summary>Runs a call of the library and throws what went wrong as a classified failure (never the library's message).</summary>
    private static Task<T> Run<T>(Func<T> call, CancellationToken ct) => Task.Run(() =>
    {
        try { return call(); }
        catch (Exception e) when (e is not OperationCanceledException and not ConnectionFailureException and not PinMismatchException) { throw new ConnectionFailureException(FileFailures.Classify(e, ct)); }
    }, ct);

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// <summary>SCP (plan 27a IX04): read and write one file over SSH, and delete it with a command. SCP cannot list a folder, so <see cref="ListAsync"/> says <see cref="ConnectionFailureKind.Unsupported"/>: use <c>sftp</c> to list. A write goes to <c>name.part</c> and is moved when whole.</summary>
public sealed class ScpFileStore : IFileStore
{
    private readonly ScpClient _scp;
    private readonly SshClient _ssh;
    private readonly string _root;

    private ScpFileStore(ScpClient scp, SshClient ssh, string root) => (_scp, _ssh, _root) = (scp, ssh, root);

    public static async Task<ScpFileStore> OpenAsync(ConnectionDefinition definition, ISecretResolver secrets, CancellationToken ct = default)
    {
        var info = await Ssh.InfoAsync(definition, secrets, ct).ConfigureAwait(false);
        var scp = new ScpClient(info, RemotePathTransformation.ShellQuote) { OperationTimeout = TimeSpan.FromSeconds(definition.Timeouts.CommandS) };
        var ssh = new SshClient(await Ssh.InfoAsync(definition, secrets, ct).ConfigureAwait(false));
        var scpPin = new Ssh.Pin(scp, definition);
        var sshPin = new Ssh.Pin(ssh, definition);
        try
        {
            await Ssh.ConnectAsync(scp, scpPin, ct).ConfigureAwait(false);
            await Ssh.ConnectAsync(ssh, sshPin, ct).ConfigureAwait(false);
        }
        catch
        {
            scp.Dispose();
            ssh.Dispose();
            throw;
        }

        return new ScpFileStore(scp, ssh, definition.FileServer!.Root);
    }

    public Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default) =>
        throw FileFailures.Unsupported("scp-no-listing");

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => Run<Stream>(() =>
    {
        var buffer = new MemoryStream();
        _scp.Download(FileStorePaths.Resolve(_root, path), buffer);
        buffer.Position = 0;
        return buffer;
    }, ct);

    public Task WriteAsync(string path, Stream content, CancellationToken ct = default) => Run(() =>
    {
        var target = FileStorePaths.Resolve(_root, path);
        var temporary = target + FileNames.PartSuffix;
        var made = _ssh.RunCommand($"mkdir -p -- {Ssh.Quote(FileStorePaths.Split(target).Folder)}");
        if (made.ExitStatus != 0) throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Permission, "scp-mkdir", FileFailures.Engine));
        _scp.Upload(content, temporary);
        var moved = _ssh.RunCommand($"mv -f -- {Ssh.Quote(temporary)} {Ssh.Quote(target)}");
        if (moved.ExitStatus != 0) throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Permission, "scp-move", FileFailures.Engine));
        return 0;
    }, ct);

    public Task DeleteAsync(string path, CancellationToken ct = default) => Run(() =>
    {
        var removed = _ssh.RunCommand($"rm -- {Ssh.Quote(FileStorePaths.Resolve(_root, path))}");
        if (removed.ExitStatus != 0) throw FileFailures.Missing();
        return 0;
    }, ct);

    private static Task<T> Run<T>(Func<T> call, CancellationToken ct) => Task.Run(() =>
    {
        try { return call(); }
        catch (Exception e) when (e is not OperationCanceledException and not ConnectionFailureException and not PinMismatchException) { throw new ConnectionFailureException(FileFailures.Classify(e, ct)); }
    }, ct);

    public ValueTask DisposeAsync()
    {
        _scp.Dispose();
        _ssh.Dispose();
        return ValueTask.CompletedTask;
    }
}
