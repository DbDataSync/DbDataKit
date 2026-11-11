namespace DbDataKit.Hosting;

/// <summary>How <see cref="HealthProbeClient"/> talks to the product's health endpoint.</summary>
public sealed class HealthProbeOptions
{
    /// <summary>URL used when <c>--healthcheck</c> has no argument.</summary>
    public string DefaultUrl { get; init; } = "http://127.0.0.1:8080/health/live";

    /// <summary>Request version. Hosts that listen for HTTP/2 only (gRPC and Flight services) need 2.0.</summary>
    public Version HttpVersion { get; init; } = System.Net.HttpVersion.Version11;

    /// <summary>
    /// Accept any server certificate when the URL points at this machine (loopback or localhost). For a local
    /// liveness check against a self-signed certificate; nothing is sent but a GET, and other hosts are validated normally.
    /// </summary>
    public bool TrustLoopbackCertificate { get; init; }
}

/// <summary>Used by <c>--healthcheck</c> (Docker HEALTHCHECK, watchdog scripts): GET the health URL, exit code 0/1.</summary>
public static class HealthProbeClient
{
    /// <summary>
    /// If args are <c>--healthcheck [url]</c>, run the probe and return the process exit code; otherwise null so
    /// startup continues. Call first in Main (the Dockerfile HEALTHCHECK relies on it).
    /// </summary>
    public static async Task<int?> TryHandleAsync(string[] args, HealthProbeOptions? options = null)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--healthcheck", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        var text = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : options?.DefaultUrl ?? "http://127.0.0.1:8080/health/live";
        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? await RunAsync(url, TimeSpan.FromSeconds(4), null, options).ConfigureAwait(false)
            : 2;
    }

    public static async Task<int> RunAsync(Uri url, TimeSpan timeout, HttpMessageHandler? handler = null, HealthProbeOptions? options = null)
    {
        using var own = handler is null && options?.TrustLoopbackCertificate == true && url.IsLoopback
            ? new SocketsHttpHandler { SslOptions = { RemoteCertificateValidationCallback = (_, _, _, _) => true } }
            : null;
        using var http = handler is not null ? new HttpClient(handler, disposeHandler: false)
            : own is not null ? new HttpClient(own, disposeHandler: false)
            : new HttpClient();
        http.Timeout = timeout;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (options is not null)
            {
                request.Version = options.HttpVersion;
                request.VersionPolicy = options.HttpVersion >= System.Net.HttpVersion.Version20
                    ? HttpVersionPolicy.RequestVersionExact
                    : HttpVersionPolicy.RequestVersionOrLower;
            }

            using var response = await http.SendAsync(request).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
