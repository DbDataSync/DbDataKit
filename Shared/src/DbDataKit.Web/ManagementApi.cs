using System.Net;
using Microsoft.AspNetCore.Builder;

namespace DbDataKit.Web.Trust;

/// <summary>
/// Where a server's management API starts and which version of it this code speaks (plan 13, D17). Products release on their own, so a console and a server can differ in version; the management API has its own
/// integer version, reported at pairing and in a header of every response, and a client supports its own version and the one before. Anything else is refused with a message that says which side to upgrade.
/// </summary>
public static class ManagementApi
{
    public const int Version = 1;
    public const string PairingPath = "/management/v1/pairing";
    public const string RotationPath = "/management/v1/pairing/rotate";
    public const string VersionHeader = "X-DbData-Api-Version";

    /// <summary>The oldest server version a client of version <paramref name="own"/> works with: its own and the one before.</summary>
    public static int OldestSupported(int own = Version) => Math.Max(1, own - 1);

    public static bool Supports(int serverVersion, int own = Version) => serverVersion >= OldestSupported(own) && serverVersion <= own;

    /// <summary>Null when the server's version is one this client supports; otherwise what to tell the person.</summary>
    public static string? Incompatibility(int serverVersion, int own = Version)
    {
        if (Supports(serverVersion, own)) return null;
        var range = OldestSupported(own) == own ? $"{own}" : $"{OldestSupported(own)} to {own}";
        return serverVersion > own
            ? $"This server speaks management API version {serverVersion}; this console supports {range}. Upgrade this console."
            : $"This server speaks management API version {serverVersion}; this console supports {range}. Upgrade this server.";
    }

    /// <summary>Makes every response of the server say which management API version it speaks.</summary>
    public static IApplicationBuilder UseManagementApiVersion(this IApplicationBuilder app, int version = Version, int generation = SuiteCompatibility.Generation) =>
        app.UseSuiteCompatibility(generation).Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[VersionHeader] = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Task.CompletedTask;
            });
            return next();
        });
}

/// <summary>A server answered with a management API version or a compatibility generation this client does not support, or did not say its management API version.</summary>
public sealed class IncompatibleServerException(string message, int? serverVersion = null, int? serverGeneration = null) : Exception(message)
{
    public int? ServerVersion { get; } = serverVersion;

    /// <summary>The compatibility generation the server reported, when the refusal is about generations (<see cref="SuiteCompatibility"/>).</summary>
    public int? ServerGeneration { get; } = serverGeneration;
}

/// <summary>
/// Goes in front of every call a client makes to a paired server: states the client's version, and refuses the response of a server whose version it does not support (or that does not report one), with
/// the upgrade message. <paramref name="observed"/> hears each version seen, so a console can keep the recorded version of an instance current. After the management API version it checks the compatibility
/// generation the response states (<see cref="SuiteCompatibility"/>): a server more than one generation from <paramref name="ownGeneration"/> is refused with the message naming both program versions and the side to
/// upgrade, on every call, so a server that was upgraded is reached again by the next one; a response that states none is taken to be in range. <paramref name="observedGeneration"/> hears each generation seen.
/// </summary>
public sealed class ManagementVersionHandler(HttpMessageHandler inner, int own = ManagementApi.Version, Action<int>? observed = null, int ownGeneration = SuiteCompatibility.Generation, Action<int>? observedGeneration = null) : DelegatingHandler(inner)
{
    /// <summary>How long the handler waits before it sends a request again that the server answered with its own 408 (no version in it).</summary>
    public TimeSpan RetryAfterRequestTimeout { get; init; } = TimeSpan.FromMilliseconds(250);

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(ManagementApi.VersionHeader, own.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.TryAddWithoutValidation(SuiteCompatibility.GenerationHeader, ownGeneration.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var response = await base.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.RequestTimeout && !response.Headers.Contains(ManagementApi.VersionHeader))
        {
            // Kestrel itself answers 408 when the headers of a request arrive later than its header timeout (the server process stalled, or a loaded machine was slow): that answer is written before any
            // middleware runs, so it cannot carry the version, and the server never started the request. Send it once more instead of calling a busy server incompatible.
            response.Dispose();
            await Task.Delay(RetryAfterRequestTimeout, cancellationToken);
            try { response = await base.SendAsync(request, cancellationToken); }
            catch (InvalidOperationException) { throw new HttpRequestException($"{request.RequestUri?.Authority} timed out reading the request, and the request could not be sent again."); }
        }
        if (!response.Headers.TryGetValues(ManagementApi.VersionHeader, out var values) || !int.TryParse(values.FirstOrDefault(), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var version))
        {
            response.Dispose();
            throw new IncompatibleServerException($"{request.RequestUri?.Authority} did not report a management API version, so it is not a server this console can manage.");
        }
        observed?.Invoke(version);
        if (ManagementApi.Incompatibility(version, own) is { } message)
        {
            response.Dispose();
            throw new IncompatibleServerException(message, version);
        }
        var generation = response.Headers.TryGetValues(SuiteCompatibility.GenerationHeader, out var generations) ? SuiteCompatibility.ParseGeneration(generations.FirstOrDefault()) : null;
        if (generation is { } seen) observedGeneration?.Invoke(seen);
        var program = response.Headers.TryGetValues(SuiteCompatibility.ProgramVersionHeader, out var programs) ? programs.FirstOrDefault() : null;
        if (SuiteCompatibility.Incompatibility(generation, program, ownGeneration) is { } apart)
        {
            response.Dispose();
            throw new IncompatibleServerException(apart, version, generation);
        }
        return response;
    }
}
