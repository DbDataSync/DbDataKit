using Grpc.Core.Interceptors;
using System.Data;
using System.Data.Common;
using System.Net.Security;
using System.Text;
using Apache.Arrow.Flight;
using Apache.Arrow.Flight.Client;
using Apache.Arrow.Flight.Sql;
using Apache.Arrow.Flight.Sql.Client;
using Grpc.Core;
using Grpc.Net.Client;

namespace DbDataKit.Connections.FlightSql;

/// <summary>
/// A read-only ADO.NET connection to an Arrow Flight SQL server.
/// <para>
/// Flight SQL has no ADO.NET provider, but everything above the drivers in this codebase — readers,
/// the connection test, metadata browsing — speaks <see cref="DbConnection"/>, so this adapter is what
/// lets the existing generic readers run against it unchanged. It is deliberately thin: it opens a gRPC
/// channel, authenticates, and hands out commands. Transactions and writes are not supported, and say
/// so rather than pretending.
/// </para>
/// <para>
/// **The credential never leaves this class.** <see cref="ConnectionString"/> is the address alone; the
/// token or password is held only as the header value attached to calls.
/// </para>
/// </summary>
public sealed class FlightSqlConnection : DbConnection
{
    private const string AuthorizationHeader = "authorization";

    private readonly FlightSqlSettings _settings;
    private string? _catalog;
    private GrpcChannel? _channel;
    private string? _authorization;
    private ConnectionState _state = ConnectionState.Closed;
    private readonly DeadlineInterceptor _deadlines = new();

    public FlightSqlConnection(FlightSqlSettings settings, Func<object?, string> renderLiteral)
    {
        _settings = settings;
        _catalog = settings.Catalog;
        RenderLiteral = renderLiteral ?? throw new ArgumentNullException(nameof(renderLiteral));
    }

    /// <summary>How a parameter value is written into the SQL text when <see cref="FlightSqlParameterMode.Inline"/> is chosen: the engine behind the server decides the literal syntax.</summary>
    internal Func<object?, string> RenderLiteral { get; }

    internal FlightSqlSettings Settings => _settings;

    /// <summary>Set once the server has refused bound parameters (Unimplemented): later commands skip
    /// the prepared round trips and send values inline.</summary>
    internal volatile bool ServerRefusesParameters;

    /// <summary>The Flight SQL client, for the calls a command does not make (the metadata calls, a statement's schema). It is the library's own: its unary calls take the headers and the deadline of <see cref="CallOptions"/>.</summary>
    public FlightSqlClient Client { get; private set; } = null!;

    /// <summary>
    /// Streams the record batches behind a ticket, decompressing LZ4/Zstd ones. Used instead of
    /// <c>Client.DoGetAsync</c>, which cannot read a compressed batch — see <see cref="FlightDataReader"/>.
    /// </summary>
    public IAsyncEnumerable<Apache.Arrow.RecordBatch> DoGetAsync(
        FlightTicket ticket, FlightCallOptions options, CancellationToken cancellationToken)
    {
        var channel = _channel ?? throw new InvalidOperationException("The connection is not open.");
        DateTime? deadline = options.Timeout > TimeSpan.Zero ? DateTime.UtcNow + options.Timeout : null;
        return FlightDataReader.ReadAsync(channel.CreateCallInvoker(), ticket, options.Headers, deadline, cancellationToken);
    }

    /// <summary>A fresh options object per call: <see cref="Metadata"/> is mutable and shared state
    /// between concurrent calls would be a bug waiting to happen.</summary>
    public FlightCallOptions CallOptions(TimeSpan? timeout = null)
    {
        var options = new FlightCallOptions();
        if (_authorization is not null)
            options.Headers.Add(AuthorizationHeader, _authorization);

        if (_settings.DataSource is { } dataSource)
            options.Headers.Add(FlightSqlSettings.DataSourceHeader, dataSource);
        if (_settings.Compression is { } compression)
            options.Headers.Add(FlightSqlSettings.CompressionHeader, compression);

        foreach (var (name, value) in _settings.Headers ?? [])
            options.Headers.Add(name, value);

        var effective = timeout ?? _settings.CommandTimeout;
        if (effective > TimeSpan.Zero && effective != Timeout.InfiniteTimeSpan)
            options.Timeout = effective;

        // The library's client ignores FlightCallOptions.Timeout, so the limit is put on the calls as a gRPC deadline (the server receives it too and can stop). One limit at a time per connection, as a connection runs one command at a time.
        _deadlines.Timeout = options.Timeout > TimeSpan.Zero ? options.Timeout : null;

        return options;
    }

    /// <summary>The address only — never a credential. Setting it is not supported: a connection is
    /// built from its settings.</summary>
    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string ConnectionString
    {
        get => _settings.Address.ToString();
        set => throw new NotSupportedException("A Flight SQL connection is configured when it is created.");
    }

    public override string Database => _catalog ?? "";
    public override string DataSource => _settings.Address.Authority;

    /// <summary>Not queried: the version is only available through an optional SqlInfo call that many
    /// servers and proxies do not implement, and a property that can throw is how
    /// <c>JdbcConnection.ServerVersion</c> turned a successful test into a crash (phase 171V).</summary>
    public override string ServerVersion => "Arrow Flight SQL";

    public override ConnectionState State => _state;

    public override void ChangeDatabase(string databaseName) => _catalog = string.IsNullOrWhiteSpace(databaseName) ? null : databaseName;

    public override void Open() => OpenAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task OpenAsync(CancellationToken cancellationToken)
    {
        if (_state == ConnectionState.Open)
            throw new InvalidOperationException("The connection is already open.");

        var handler = new SocketsHttpHandler
        {
            EnableMultipleHttp2Connections = true,
            ConnectTimeout = _settings.ConnectTimeout,
        };

        if (_settings.SkipCertificateValidation)
        {
            // An explicit, per-connection opt-out the operator chose (and was warned about on the form) —
            // for a proxy with a self-signed or private-CA certificate. Not a global switch.
            handler.SslOptions = new SslClientAuthenticationOptions
            {
                RemoteCertificateValidationCallback = static (_, _, _, _) => true,
            };
        }
        else if (_settings.CaFile is { } caFile)
        {
            handler.SslOptions = CustomRootTrust(caFile);
        }

        _channel = GrpcChannel.ForAddress(_settings.Address, new GrpcChannelOptions
        {
            HttpHandler = handler,
            // Arrow record batches routinely exceed gRPC's 4 MB default; a read must not fail on size.
            MaxReceiveMessageSize = null,
            MaxSendMessageSize = null,
            DisposeHttpClient = true,
        });

        var flight = new FlightClient(_channel.Intercept(_deadlines));
        Client = new FlightSqlClient(flight);

        try
        {
            _authorization = _settings.Auth switch
            {
                FlightSqlAuthMethod.Bearer => $"Bearer {_settings.Credential}",
                FlightSqlAuthMethod.Basic => await HandshakeAsync(flight, cancellationToken),
                _ => null,
            };
            _state = ConnectionState.Open;
        }
        catch
        {
            _channel.Dispose();
            _channel = null;
            _authorization = null;
            throw;
        }
    }

    /// <summary>
    /// Basic authentication: the Flight handshake, with the credentials as a Basic header on that one
    /// call. A server that issues sessions answers with a bearer token (header, or trailer on some),
    /// which replaces the password on every later call. A server that issues none — a proxy that checks
    /// Basic credentials on each request — leaves nothing to swap in, so the Basic header itself is kept.
    /// </summary>
    private async Task<string> HandshakeAsync(FlightClient flight, CancellationToken cancellationToken)
    {
        var basic = "Basic " + Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_settings.UserId}:{_settings.Credential}"));

        var metadata = new Metadata { { AuthorizationHeader, basic } };
        DateTime? deadline = _settings.ConnectTimeout == Timeout.InfiniteTimeSpan
            ? null
            : DateTime.UtcNow + _settings.ConnectTimeout;

        using var call = flight.Handshake(metadata, deadline, cancellationToken);
        var issued = await ExchangeHandshakeAsync(call, cancellationToken);
        return issued ?? basic;
    }

    /// <summary>
    /// The handshake call itself: send the (empty) request, then read the answer. Returns the authorization the server issued, or null if it
    /// issued none.
    /// <para>
    /// The request write is allowed to fail. A server that rejects the credentials can answer, and end the call, before it has read the
    /// request, and the client's write then fails with a transport "cancelled" (seen in CI, from <c>HttpContentClientStreamWriter</c>)
    /// while the server's real answer, <c>Unauthenticated</c> for a wrong password, is waiting on the response side. So a failed write is
    /// not the result: the response is read next, and throws the server's own status. Only the caller's own cancellation is left to
    /// propagate. If the server was in fact content, the handshake simply carries on.
    /// </para>
    /// </summary>
    internal static async Task<string?> ExchangeHandshakeAsync(
        AsyncDuplexStreamingCall<FlightHandshakeRequest, FlightHandshakeResponse> call, CancellationToken cancellationToken)
    {
        try
        {
            try
            {
                await call.RequestStream.WriteAsync(new FlightHandshakeRequest(Google.Protobuf.ByteString.Empty, 0));
                await call.RequestStream.CompleteAsync();
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // See the summary: the answer, not this failure, says what happened.
            }

            var headers = await call.ResponseHeadersAsync;
            while (await call.ResponseStream.MoveNext(cancellationToken))
            {
                // Nothing in the payload is needed; draining the stream is what lets the trailers arrive.
            }

            return Authorization(headers) ?? Authorization(call.GetTrailers());
        }
        catch (Exception ex) when (FlightSqlException.TryWrap(ex, out var wrapped))
        {
            throw wrapped;
        }
    }

    /// <summary>
    /// Trust for the certificate authorities of a PEM file instead of the machine's: the chain must build to one of them, and the host name must still match.
    /// </summary>
    private static SslClientAuthenticationOptions CustomRootTrust(string caFile)
    {
        System.Security.Cryptography.X509Certificates.X509Certificate2Collection roots;
        try { roots = []; roots.ImportFromPemFile(caFile); }
        catch (Exception ex) when (ex is IOException or System.Security.Cryptography.CryptographicException or UnauthorizedAccessException)
        { throw new InvalidOperationException("The CA file of the Flight SQL connection could not be read as a PEM file of certificates."); }
        return new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            {
                if (certificate is null) return false;
                if ((errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0 || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNotAvailable) != 0) return false;
                using var chain = new System.Security.Cryptography.X509Certificates.X509Chain();
                chain.ChainPolicy.TrustMode = System.Security.Cryptography.X509Certificates.X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.AddRange(roots);
                chain.ChainPolicy.RevocationMode = System.Security.Cryptography.X509Certificates.X509RevocationMode.NoCheck;
                return chain.Build(new System.Security.Cryptography.X509Certificates.X509Certificate2(certificate));
            },
        };
    }

    private static string? Authorization(Metadata? metadata) =>
        metadata?.FirstOrDefault(e => string.Equals(e.Key, AuthorizationHeader, StringComparison.OrdinalIgnoreCase))?.Value;

    public override void Close()
    {
        _channel?.Dispose();
        _channel = null;
        _authorization = null;
        _state = ConnectionState.Closed;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            Close();
        base.Dispose(disposing);
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("The Arrow Flight SQL driver is read-only and has no transactions.");

    protected override DbCommand CreateDbCommand() => new FlightSqlCommand(this);
}
