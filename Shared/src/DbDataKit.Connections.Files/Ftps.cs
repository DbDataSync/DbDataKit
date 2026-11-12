using System.Net;
using System.Security.Cryptography.X509Certificates;
using DbDataKit.Secrets;
using FluentFTP;

namespace DbDataKit.Connections.Files;

/// <summary>
/// FTPS (plan 27a IX04), FTP with TLS, with FluentFTP as a thin wrapper. The server's certificate is trusted when its SHA-256 is one of <c>file_server.certificate_sha256</c> (the pin), or when the machine's roots (or <c>tls.ca_file</c>) vouch for it; <c>tls.verify: false</c> takes any certificate on purpose.
/// A certificate that is neither is refused naming its fingerprint. A write goes to <c>name.part</c> and is renamed when whole. A read is buffered in memory.
/// </summary>
public sealed class FtpsFileStore : IFileStore
{
    private readonly AsyncFtpClient _client;
    private readonly string _root;

    private FtpsFileStore(AsyncFtpClient client, string root) => (_client, _root) = (client, root);

    public static async Task<FtpsFileStore> OpenAsync(ConnectionDefinition definition, ISecretResolver secrets, CancellationToken ct = default)
    {
        var server = definition.FileServer!;
        var signIn = definition.SignIn ?? new SignInSpec();
        var credential = signIn.Method == "basic"
            ? new NetworkCredential(signIn.User!, await SecretReader.ReadAsync(secrets, signIn.PasswordSecret!, ct).ConfigureAwait(false))
            : new NetworkCredential("anonymous", "anonymous@");
        var implicitMode = server.FtpsMode == "implicit";
        var client = new AsyncFtpClient(server.Host, credential, server.Port ?? (implicitMode ? 990 : 21));
        client.Config.EncryptionMode = implicitMode ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
        client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        client.Config.ConnectTimeout = definition.Timeouts.ConnectS * 1000;
        client.Config.ReadTimeout = definition.Timeouts.CommandS * 1000;
        client.Config.DataConnectionConnectTimeout = definition.Timeouts.ConnectS * 1000;
        client.Config.RetryAttempts = 1;
        string? refused = null;
        client.ValidateCertificate += (_, e) =>
        {
            var offered = Fingerprints.OfCertificate(e.Certificate.GetRawCertData());
            e.Accept = Trusts(definition, e.Certificate, e.PolicyErrors, offered);
            if (!e.Accept) refused = offered;
        };
        try
        {
            await client.Connect(ct).ConfigureAwait(false);
        }
        catch (Exception) when (refused is not null)
        {
            client.Dispose();
            throw new PinMismatchException("certificate", server.Host, refused, server.CertificateSha256);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        return new FtpsFileStore(client, server.Root);
    }

    /// <summary>Whether the certificate the server showed is accepted: any with <c>tls.verify: false</c>; one that is pinned; otherwise one the machine's roots (or <c>tls.ca_file</c>) vouch for, when the name matches.</summary>
    internal static bool Trusts(ConnectionDefinition definition, X509Certificate certificate, System.Net.Security.SslPolicyErrors errors, string offered)
    {
        var server = definition.FileServer!;
        if (!definition.Tls.EffectiveVerify) return true;
        if (server.CertificateSha256.Any(p => string.Equals(p, offered, StringComparison.OrdinalIgnoreCase))) return true;
        if (server.CertificateSha256.Count > 0 && definition.Tls.CaFile is null) return false;   // pinned: only the pinned certificate
        if (errors == System.Net.Security.SslPolicyErrors.None) return true;
        if ((errors & ~System.Net.Security.SslPolicyErrors.RemoteCertificateChainErrors) != 0 || definition.Tls.CaFile is not { Length: > 0 } caFile) return false;
        var roots = new X509Certificate2Collection();
        roots.ImportFromPemFile(caFile);
        using var chain = new X509Chain();
        chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        chain.ChainPolicy.CustomTrustStore.AddRange(roots);
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return chain.Build(new X509Certificate2(certificate));
    }

    /// <summary>What the server at the connection's address shows as its certificate, without signing in: the SHA-256 to pin (a person confirms it with the server's administrator). Null when the server could not be reached.</summary>
    public static async Task<string?> ProbeAsync(ConnectionDefinition definition, CancellationToken ct = default)
    {
        var server = definition.FileServer!;
        var implicitMode = server.FtpsMode == "implicit";
        using var client = new AsyncFtpClient(server.Host, new NetworkCredential("anonymous", "anonymous@"), server.Port ?? (implicitMode ? 990 : 21));
        client.Config.EncryptionMode = implicitMode ? FtpEncryptionMode.Implicit : FtpEncryptionMode.Explicit;
        client.Config.ConnectTimeout = definition.Timeouts.ConnectS * 1000;
        client.Config.RetryAttempts = 1;
        string? offered = null;
        client.ValidateCertificate += (_, e) => { offered = Fingerprints.OfCertificate(e.Certificate.GetRawCertData()); e.Accept = false; };   // the certificate is read and refused: nothing is sent after it
        try { await client.Connect(ct).ConfigureAwait(false); }
        catch (Exception) { /* refused on purpose */ }
        return offered;
    }

    public async Task<IReadOnlyList<FileEntry>> ListAsync(string folder = "/", bool includeTemporary = false, CancellationToken ct = default) => await Run(async () =>
    {
        var resolved = FileStorePaths.Resolve(_root, folder);
        var items = await _client.GetListing(resolved, ct).ConfigureAwait(false);
        return (IReadOnlyList<FileEntry>)[.. items
            .Where(i => i.Type is FtpObjectType.File or FtpObjectType.Directory && i.Name is not ("." or "..") && (includeTemporary || i.Type == FtpObjectType.Directory || !FileNames.IsTemporary(i.Name)))
            .Select(i => new FileEntry(FileStorePaths.Relative(_root, i.FullName), i.Name, i.Type == FtpObjectType.Directory ? 0 : Math.Max(0, i.Size), i.Modified == default ? default : new DateTimeOffset(DateTime.SpecifyKind(i.Modified, DateTimeKind.Utc)), i.Type == FtpObjectType.Directory))
            .OrderByDescending(f => f.IsDirectory).ThenBy(f => f.Name, StringComparer.Ordinal)];
    }, ct).ConfigureAwait(false);

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => await Run(async () =>
    {
        var buffer = new MemoryStream();
        if (!await _client.DownloadStream(buffer, FileStorePaths.Resolve(_root, path), 0, null, ct).ConfigureAwait(false)) throw FileFailures.Missing();
        buffer.Position = 0;
        return (Stream)buffer;
    }, ct).ConfigureAwait(false);

    public async Task WriteAsync(string path, Stream content, CancellationToken ct = default) => await Run(async () =>
    {
        var target = FileStorePaths.Resolve(_root, path);
        var temporary = target + FileNames.PartSuffix;
        var status = await _client.UploadStream(content, temporary, FtpRemoteExists.Overwrite, true, null, ct).ConfigureAwait(false);
        if (status == FtpStatus.Failed) throw new ConnectionFailureException(new ConnectionFailure(ConnectionFailureKind.Permission, "ftp-upload", FileFailures.Engine));
        await _client.MoveFile(temporary, target, FtpRemoteExists.Overwrite, ct).ConfigureAwait(false);
        return 0;
    }, ct).ConfigureAwait(false);

    public async Task DeleteAsync(string path, CancellationToken ct = default) => await Run(async () =>
    {
        await _client.DeleteFile(FileStorePaths.Resolve(_root, path), ct).ConfigureAwait(false);
        return 0;
    }, ct).ConfigureAwait(false);

    private static async Task<T> Run<T>(Func<Task<T>> call, CancellationToken ct)
    {
        try { return await call().ConfigureAwait(false); }
        catch (Exception e) when (e is not OperationCanceledException and not ConnectionFailureException and not PinMismatchException) { throw new ConnectionFailureException(FileFailures.Classify(e, ct)); }
    }

    public ValueTask DisposeAsync()
    {
        _client.Dispose();
        return ValueTask.CompletedTask;
    }
}
