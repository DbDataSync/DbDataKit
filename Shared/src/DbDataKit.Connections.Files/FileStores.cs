using System.Diagnostics;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Files;

/// <summary>Opens the client of a connection of a file kind (plan 27a IX04 to IX06).</summary>
public static class FileStores
{
    public static Task<IFileStore> OpenAsync(ConnectionDefinition definition, ISecretResolver secrets, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return definition.KindName switch
        {
            ConnectionKinds.Sftp => Open(SftpFileStore.OpenAsync(definition, secrets, ct)),
            ConnectionKinds.Scp => Open(ScpFileStore.OpenAsync(definition, secrets, ct)),
            ConnectionKinds.Ftps => Open(FtpsFileStore.OpenAsync(definition, secrets, ct)),
            ConnectionKinds.Smb => SmbFileStore.OpenAsync(definition, secrets, ct),
            _ => throw new ArgumentException($"A connection of kind `{definition.KindName}` is not a file store this library opens.", nameof(definition)),
        };

        // what the library throws while connecting and signing in is told by its kind (a rejected login, no answer, a timeout), never by its own message; a refused pin and an unset secret say what they are
        static async Task<IFileStore> Open<T>(Task<T> opening) where T : IFileStore
        {
            try { return await opening.ConfigureAwait(false); }
            catch (Exception e) when (e is not (OperationCanceledException or ConnectionFailureException or PinMismatchException or FileSecretNotSetException)) { throw new ConnectionFailureException(FileFailures.Classify(e)); }
        }
    }

    /// <summary>What the server of an <c>sftp</c>, <c>scp</c> or <c>ftps</c> connection shows (its host key fingerprint, or its certificate's SHA-256) without signing in: what a person confirms with the server's administrator and pins. Null when it cannot be reached.</summary>
    public static Task<string?> ProbeAsync(ConnectionDefinition definition, CancellationToken ct = default) => definition.KindName switch
    {
        ConnectionKinds.Sftp or ConnectionKinds.Scp => Ssh.ProbeAsync(definition, ct),
        ConnectionKinds.Ftps => FtpsFileStore.ProbeAsync(definition, ct),
        _ => Task.FromResult<string?>(null),
    };
}

/// <summary>
/// What <b>Test</b> of a file connection found (plan 27a IX04): whether the server was reached, whether it accepted the sign-in, a sentence in words, and the classification a retry reads. <see cref="Reached"/> is null when nothing was tried (a secret is not set).
/// Nothing in it is a secret; a fingerprint is public.
/// </summary>
public sealed record FileTestResult(bool Ok, bool? Reached, string SignIn, string Message, ConnectionFailureKind? Kind, bool Temporary, long Milliseconds);

/// <summary>Tests a connection of a file kind: connects as the file says (the host key or certificate checked against the pins), signs in and lists the root once.</summary>
public static class FileStoreTester
{
    public static async Task<FileTestResult> TestAsync(ConnectionDefinition definition, ISecretResolver secrets, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        FileTestResult Done(bool ok, bool? reached, string signIn, string message, ConnectionFailureKind? kind = null) => new(ok, reached, signIn, message, kind, kind is { } k && ConnectionFailure.IsTemporaryKind(k), clock.ElapsedMilliseconds);
        var server = definition.FileServer;
        var where = server is null ? definition.Name : definition.KindName == ConnectionKinds.Smb ? SmbFileStore.UncPath(definition) : $"{server.Host}:{server.Port ?? DefaultPort(definition)}";
        var pinned = server is not null && (server.HostKeys.Count > 0 || server.CertificateSha256.Count > 0);
        try
        {
            await using var store = await FileStores.OpenAsync(definition, secrets, ct).ConfigureAwait(false);
            // nothing is pinned (the connection takes any key on purpose): say what the server shows, so a person can confirm and pin it
            var seen = server is not null && !pinned ? await ProbeOrNull(definition, ct).ConfigureAwait(false) : null;
            var hint = seen is not null ? $" The server shows {seen}: confirm it with its administrator, pin it ({(definition.KindName == ConnectionKinds.Ftps ? "certificate_sha256" : "host_keys")}) and remove `tls.verify: false`." : "";
            if (definition.KindName == ConnectionKinds.Scp) return Done(true, true, "accepted", $"Signed in to {where} over SSH. SCP cannot list a folder, so nothing was read; use `sftp` to browse.{hint}");
            var entries = await store.ListAsync("/", false, ct).ConfigureAwait(false);
            return Done(true, true, "accepted", $"Signed in to {where} and listed the root: {entries.Count} {(entries.Count == 1 ? "entry" : "entries")}.{hint}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            var failure = FileFailures.Classify(e, ct);
            return e switch
            {
                PinMismatchException pin => Done(false, true, "not checked", pin.Message, failure.Kind),
                FileSecretNotSetException unset => Done(false, null, "not checked", $"{unset.Message} Type it on this screen (it is kept in the console's secret store), or set the secret or variable that names.", failure.Kind),
                _ => Done(false, failure.Kind is ConnectionFailureKind.Login or ConnectionFailureKind.Permission ? true : failure.Kind is ConnectionFailureKind.Network or ConnectionFailureKind.Timeout ? false : null,
                    failure.Kind == ConnectionFailureKind.Login ? "rejected" : "not checked", Words(failure, where), failure.Kind),
            };
        }
    }

    private static string Words(ConnectionFailure failure, string where) => failure.Code switch
    {
        SmbFileStore.NeedsWindows => "An SMB share is signed in to with the Windows identity of the process (Kerberos): this host is not Windows, so the connection cannot be used here. Run it on a Windows host that is joined to the domain.",
        "smb-kerberos" => $"{where} refused the Windows sign-in or no Kerberos ticket could be had for it. Check that this machine is in the domain, the clock is right, and the server is named by its DNS name (an address makes Windows use NTLM).",
        "smb-clock" => $"No Kerberos ticket for {where}: this machine's clock is too far from the domain controller's.",
        "smb-logon" => $"{where} did not accept the Windows identity of the process. Check that the account may use the share, and that the machine is in the domain.",
        _ => WordsOfKind(failure, where),
    };

    private static string WordsOfKind(ConnectionFailure failure, string where) => failure.Kind switch
    {
        ConnectionFailureKind.Login => $"{where} did not accept the sign-in ({failure.Code}). Check the user and the secret (the password, or the private key and its passphrase).",
        ConnectionFailureKind.Network => $"{where} could not be reached: nothing answered there. Check the address and that the server is up.",
        ConnectionFailureKind.Timeout => $"{where} did not answer in time.",
        ConnectionFailureKind.Tls => "A secure connection could not be established: the server's certificate is not trusted. Pin it with `certificate_sha256`, give its authority in `tls.ca_file`, or turn verification off for this connection if you must.",
        ConnectionFailureKind.Permission => $"{where} accepted the sign-in, but the user may not list the root folder.",
        ConnectionFailureKind.NotFound => $"{where} accepted the sign-in, but the folder (`file_server.root`) is not there.",
        _ => ConnectionFailure.TextFor(failure.Kind) + $" ({failure.Code})",
    };

    private static async Task<string?> ProbeOrNull(ConnectionDefinition definition, CancellationToken ct)
    {
        try { return await FileStores.ProbeAsync(definition, ct).ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    private static int DefaultPort(ConnectionDefinition d) => d.KindName == ConnectionKinds.Smb ? 445 : d.KindName == ConnectionKinds.Ftps ? (d.FileServer?.FtpsMode == "implicit" ? 990 : 21) : 22;
}
