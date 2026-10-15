namespace DbData.Hosting;

/// <summary>Used by <c>--healthcheck</c> (Docker HEALTHCHECK, watchdog scripts): GET the health URL, exit code 0/1.</summary>
public static class HealthProbeClient
{
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
