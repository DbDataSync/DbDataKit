namespace DbData.Hosting;

/// <summary>Used by <c>--healthcheck</c> (Docker HEALTHCHECK, watchdog scripts): GET the health URL, exit code 0/1.</summary>
public static class HealthProbeClient
{
    /// <summary>
    /// If args are <c>--healthcheck [url]</c>, run the probe and return the process exit code; otherwise null so
    /// startup continues. Call first in Main (the Dockerfile HEALTHCHECK relies on it).
    /// </summary>
    public static async Task<int?> TryHandleAsync(string[] args)
    {
        var i = Array.FindIndex(args, a => string.Equals(a, "--healthcheck", StringComparison.OrdinalIgnoreCase));
        if (i < 0) return null;
        var text = i + 1 < args.Length ? args[i + 1] : "http://127.0.0.1:8080/health/live";
        return Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https"
            ? await RunAsync(url, TimeSpan.FromSeconds(4)).ConfigureAwait(false)
            : 2;
    }

    public static async Task<int> RunAsync(Uri url, TimeSpan timeout, HttpMessageHandler? handler = null)
    {
        using var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = timeout;
        try
        {
            using var response = await http.GetAsync(url).ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return 1;
        }
    }
}
