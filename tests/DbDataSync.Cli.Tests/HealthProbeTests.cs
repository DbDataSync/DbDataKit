using System.Security.Authentication;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using DbDataSync.Certificates;
using Xunit;

namespace DbDataSync.Cli.Tests;

/// <summary>
/// The real <see cref="HttpHealthProbe"/>, against real sockets — what <c>dbdatasync update</c> leans on to decide
/// whether a version it just started is worth keeping. The fakes in <c>UpdateCommandTests</c> cover what the flow
/// does with the answer; these cover the answer itself, which is where the transport decides things.
/// </summary>
public class HealthProbeTests
{
    /// <summary>
    /// A TLS listener holding a self-signed certificate issued for a name that is *not* the one the probe dials —
    /// a deployment whose console URL and certificate disagree, which is the ordinary state of an internal install
    /// that never got a certificate from the enterprise CA. Answers every request <c>200 {"status":"ok"}</c>.
    /// </summary>
    private sealed class SelfSignedHealthServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly X509Certificate2 _certificate;
        private readonly CancellationTokenSource _stopping = new();

        public SelfSignedHealthServer(string certificateHost)
        {
            // Through a PFX, not straight from the builder: CreateSelfSigned's key is ephemeral, and SChannel
            // refuses to use one of those as a server credential.
            using var generated = ManagedSelfSignedCertificate.Generate(certificateHost, validityDays: 1);
            _certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), password: null);

            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Url = $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";
            _ = AcceptAsync();
        }

        public string Url { get; }

        private async Task AcceptAsync()
        {
            while (!_stopping.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stopping.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
                {
                    return;
                }

                _ = ServeAsync(client);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            try
            {
                using (client)
                await using (var tls = new SslStream(client.GetStream()))
                {
                    await tls.AuthenticateAsServerAsync(_certificate);
                    await tls.ReadAsync(new byte[4096]); // The request line, which this server does not vary on.

                    const string body = """{"status":"ok"}""";
                    await tls.WriteAsync(Encoding.ASCII.GetBytes(
                        $"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\n" +
                        $"Connection: close\r\n\r\n{body}"));
                    await tls.FlushAsync();
                }
            }
            catch (Exception ex) when (ex is IOException or AuthenticationException or ObjectDisposedException)
            {
                // A probe that hung up mid-handshake is the test's business, not this listener's.
            }
        }

        public void Dispose()
        {
            _stopping.Cancel();
            _listener.Dispose();
            _certificate.Dispose();
            _stopping.Dispose();
        }
    }

    /// <summary>
    /// The regression this file exists for: a service answering perfectly well behind a self-signed certificate
    /// whose name does not match its configured URL is **up**. Failing it here is what rolled two good updates back
    /// and reported nothing but a 90-second timeout.
    /// </summary>
    [Fact]
    public async Task ASelfSignedCertificateThatDoesNotMatchTheUrl_IsHealthy_AndTheCertificateIsNamed()
    {
        using var server = new SelfSignedHealthServer("dbdatasync.example.invalid");

        var result = await new HttpHealthProbe().WaitUntilHealthyAsync(
            server.Url, TimeSpan.FromSeconds(30), CancellationToken.None);

        Assert.True(result.Healthy);
        Assert.Null(result.Reason);
        Assert.Contains("did not validate", result.CertificateProblem);
        Assert.Contains("its name does not match this URL", result.CertificateProblem);
        Assert.Contains("not trusted here", result.CertificateProblem);
    }

    /// <summary>A service that is genuinely not there still fails — accepting any certificate loosens what counts
    /// as a valid peer, not what counts as an answer.</summary>
    [Fact]
    public async Task NothingListening_IsNotHealthy_AndCarriesTheTransportsOwnReason()
    {
        var probed = UnusedLoopbackPort();

        var result = await new HttpHealthProbe().WaitUntilHealthyAsync(
            $"http://127.0.0.1:{probed}", TimeSpan.FromSeconds(1), CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.Null(result.CertificateProblem);
        Assert.Contains($"http://127.0.0.1:{probed}/api/health", result.Reason);
        // The sentence that names the cause, rather than HttpRequestException's own "An error occurred while
        // sending the request", which names nothing and is what the operator used to be left with.
        Assert.DoesNotContain("An error occurred while sending the request", result.Reason);
    }

    private static int UnusedLoopbackPort()
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)socket.LocalEndPoint!).Port;
    }
}
