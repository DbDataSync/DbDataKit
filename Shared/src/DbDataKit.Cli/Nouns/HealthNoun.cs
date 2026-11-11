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
            (args, context) => CheckAsync(probe, defaultUrl, args, context),
            DataSchemas.Object([("healthy", DataSchemas.Boolean), ("detail", DataSchemas.String), ("url", DataSchemas.String)], "healthy", "detail")),
    ]);

    private static async Task<int> CheckAsync(IHealthProbe probe, string defaultUrl, string[] args, CliContext context)
    {
        var parsed = CliArgs.Parse(args, new HashSet<string>(), new HashSet<string> { "url", "timeout" });
        if (parsed.Unknown.Count > 0 || parsed.Words.Count > 0)
        {
            await context.Error.WriteLineAsync("Usage: health check [--url <url>] [--timeout <seconds>]");
            return CliExit.Usage;
        }
        if (!Uri.TryCreate(parsed.Get("url") ?? defaultUrl, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
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
