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
    public static IApplicationBuilder UseManagementApiVersion(this IApplicationBuilder app, int version = Version) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                context.Response.Headers[VersionHeader] = version.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return Task.CompletedTask;
            });
            return next();
        });
}

/// <summary>A server answered with a management API version this client does not support, or did not say.</summary>
public sealed class IncompatibleServerException(string message, int? serverVersion = null) : Exception(message)
{
    public int? ServerVersion { get; } = serverVersion;
}

/// <summary>
/// Goes in front of every call a client makes to a paired server: states the client's version, and refuses the response of a server whose version it does not support (or that does not report one), with
/// the upgrade message. <paramref name="observed"/> hears each version seen, so a console can keep the recorded version of an instance current.
/// </summary>
public sealed class ManagementVersionHandler(HttpMessageHandler inner, int own = ManagementApi.Version, Action<int>? observed = null) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.TryAddWithoutValidation(ManagementApi.VersionHeader, own.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var response = await base.SendAsync(request, cancellationToken);
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
        return response;
    }
}
