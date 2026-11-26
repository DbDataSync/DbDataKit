namespace DbDataKit.Cli;

/// <summary>The answer of a health probe. <see cref="Detail"/> is fixed text (a status code, a kind of failure), never what the server said.</summary>
public sealed record HealthResult(bool Healthy, string Detail);

public interface IHealthProbe
{
    Task<HealthResult> CheckAsync(Uri url, CancellationToken cancellationToken);
}

/// <summary>Healthy when the URL answers a GET with a 2xx status. Nothing of the response body is read or repeated.</summary>
public sealed class HttpHealthProbe(HttpClient? client = null) : IHealthProbe
{
    private readonly HttpClient _client = client ?? new HttpClient();

    public async Task<HealthResult> CheckAsync(Uri url, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            return new HealthResult(response.IsSuccessStatusCode, $"HTTP {(int)response.StatusCode}");
        }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException) { return new HealthResult(false, "nothing answered"); }
    }
}

/// <summary><c>health check</c>: is a running server answering. What a container's health check runs; exit 0 when it is, 1 when it is not.</summary>
public static class HealthNoun
{
    public const int DefaultTimeoutSeconds = 5;

    /// <param name="probe">How the address is asked.</param>
    /// <param name="defaultUrl">The product's health URL on its default address (<c>http://localhost:5080/api/health</c>).</param>
    public static Noun Create(IHealthProbe probe, string defaultUrl) => new("health", "Is a running server answering",
    [
        new Verb("check", "Ask the server's health address; exit 0 when it answers, 1 when it does not", EffectClass.OfflineOnly, "[--url <url>] [--timeout <seconds>]",
            (args, context) => CheckAsync(probe, defaultUrl, null, "[--url <url>] [--timeout <seconds>]", args, context),
            Schema),
    ]);

    /// <summary>
    /// <c>health check</c> of a server that pins the address it listens on in its settings file: with no <c>--url</c>, the address of the
    /// <c>listen:</c> line of the settings file in <c>--repo</c> (or the server's default folder) is asked, with the path of
    /// <paramref name="defaultUrl"/>; with no such line, or no file, <paramref name="defaultUrl"/> is.
    /// </summary>
    /// <param name="probe">How the address is asked.</param>
    /// <param name="defaultUrl">The product's health URL on its default address, asked when nothing is pinned; its path is asked on a pinned address.</param>
    /// <param name="pinned">
    /// The address the server pinned, given the <c>--repo</c> value (null when it was not given), or null when its settings file or line is not there.
    /// It throws <see cref="InvalidOperationException"/> (a line that is not an address), <see cref="IOException"/> or
    /// <see cref="UnauthorizedAccessException"/> when the file cannot be read; the check then fails naming why and <c>--url</c>, rather than ask
    /// another address that could be another server's.
    /// </param>
    /// <param name="repoUsage">How <c>--repo</c> is shown in the usage (<c>--repo &lt;server directory&gt;</c>).</param>
    public static Noun Create(IHealthProbe probe, string defaultUrl, Func<string?, Uri?> pinned, string repoUsage = "--repo <server directory>") => new("health", "Is a running server answering",
    [
        new Verb("check", "Ask the server's health address (its settings file's listen: line, else its default address); exit 0 when it answers, 1 when it does not", EffectClass.OfflineOnly,
            $"[--url <url>] [{repoUsage}] [--timeout <seconds>]",
            (args, context) => CheckAsync(probe, defaultUrl, pinned, $"[--url <url>] [{repoUsage}] [--timeout <seconds>]", args, context),
            Schema),
    ]);

    private static readonly string Schema = DataSchemas.Object([("healthy", DataSchemas.Boolean), ("detail", DataSchemas.String), ("url", DataSchemas.String)], "healthy", "detail");

    /// <summary>
    /// The health URL on <paramref name="listen"/>, the address a server listens on: its scheme and port with the path of <paramref name="defaultUrl"/>.
    /// A server that listens on every address (<c>0.0.0.0</c>, <c>::</c>) or on a host name other than an address is asked on <c>localhost</c>,
    /// which reaches it on this machine; one that listens on one address is asked there.
    /// </summary>
    public static Uri HealthUrlOn(Uri listen, string defaultUrl)
    {
        var path = new Uri(defaultUrl, UriKind.Absolute).PathAndQuery;
        var host = listen.HostNameType switch
        {
            UriHostNameType.IPv4 when listen.Host == "0.0.0.0" => "localhost",
            UriHostNameType.IPv6 when System.Net.IPAddress.Parse(listen.Host.Trim('[', ']')).Equals(System.Net.IPAddress.IPv6Any) => "localhost",
            UriHostNameType.IPv4 or UriHostNameType.IPv6 => listen.Host,
            _ => "localhost",
        };
        return new UriBuilder(listen.Scheme, host, listen.Port, path).Uri;
    }

    private static async Task<int> CheckAsync(IHealthProbe probe, string defaultUrl, Func<string?, Uri?>? pinned, string usage, string[] args, CliContext context)
    {
        var options = pinned is null ? new HashSet<string> { "url", "timeout" } : new HashSet<string> { "url", "repo", "timeout" };
        var parsed = CliArgs.Parse(args, new HashSet<string>(), options);
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync($"Usage: health check {usage}");
            return CliExit.Usage;
        }
        var address = parsed.Get("url");
        if (address is null && pinned is not null)
        {
            try { address = pinned(parsed.Get("repo")) is { } listen ? HealthUrlOn(listen, defaultUrl).ToString() : null; }
            catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                await context.Error.WriteLineAsync($"The address the server listens on cannot be read: {e.Message} Fix the listen: line, or give the address with --url.");
                return CliExit.Failure;
            }
        }
        if (!Uri.TryCreate(address ?? defaultUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            await context.Error.WriteLineAsync("The address must be an http or https URL.");
            return CliExit.Usage;
        }
        var seconds = DefaultTimeoutSeconds;
        if (parsed.Get("timeout") is { } text && (!int.TryParse(text, out seconds) || seconds is < 1 or > 600))
        {
            await context.Error.WriteLineAsync("The timeout is a number of seconds from 1 to 600.");
            return CliExit.Usage;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
        HealthResult result;
        try { result = await probe.CheckAsync(url, timeout.Token); }
        catch (OperationCanceledException) when (!context.Cancellation.IsCancellationRequested) { result = new HealthResult(false, $"no answer in {seconds} s"); }

        context.Data["healthy"] = result.Healthy;
        context.Data["detail"] = result.Detail;
        context.Data["url"] = url.ToString();
        await (result.Healthy ? context.Out : context.Error).WriteLineAsync($"{(result.Healthy ? "healthy" : "unhealthy")}: {result.Detail}");
        return result.Healthy ? CliExit.Ok : CliExit.Failure;
    }
}
