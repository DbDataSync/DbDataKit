using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DbDataKit.Web.Trust;

/// <summary>What a server tells a console that is waiting to hear of it: who it is, where the console can reach it, and a single-use code that lets the console pair once an administrator there confirms.</summary>
public sealed record Announcement(string Product, string InstanceId, string Name, string Url, string Code, DateTimeOffset Expires);

/// <summary>
/// A server that offers itself to a console (plan 13a, L03): when <see cref="UrlVariable"/> names a console's announce address (a private port of the console, reachable only on the network the two share, such as a compose network) and this server has
/// no console paired, it repeats an <see cref="Announcement"/> every <see cref="Interval"/> until one is. The console lists the server as pending; nothing is paired until an administrator confirms it there, and the code it carries expires in minutes and works once.
/// </summary>
public sealed class ConsoleAnnouncer(string product, string instanceId, string name, Uri advertise, Uri console, PairedClientsFile clients, PairingCodes codes, TimeProvider? clock = null)
{
    public const string UrlVariable = "DBDATAKIT_CONSOLE_ANNOUNCE_URL";
    public const string AdvertiseVariable = "DBDATAKIT_ADVERTISE_URL";
    public const string NameVariable = "DBDATAKIT_INSTANCE_NAME";
    public const string Path = "/announce";
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan Renew = TimeSpan.FromMinutes(3);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private PairingCode? _code;

    /// <summary>The announcer this environment asks for, or null when it asks for none (or does not say where the server can be reached, which a console needs).</summary>
    public static ConsoleAnnouncer? FromEnvironment(string product, string instanceId, PairedClientsFile clients, PairingCodes codes, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        if (!Uri.TryCreate(environment(UrlVariable), UriKind.Absolute, out var console) || console.Scheme is not ("http" or "https")) return null;
        if (!Uri.TryCreate(environment(AdvertiseVariable), UriKind.Absolute, out var advertise) || advertise.Scheme != Uri.UriSchemeHttps) return null;
        return new ConsoleAnnouncer(product, instanceId, environment(NameVariable) is { Length: > 0 } named ? named : product, advertise, console, clients, codes);
    }

    /// <summary>Tells the console once, unless a console is paired already. Returns whether the console took the announcement.</summary>
    public async Task<bool> AnnounceOnceAsync(HttpClient http, CancellationToken cancellationToken = default)
    {
        if (clients.All().Any(c => c.Kind == ClientKind.Console)) return false;
        var now = _clock.GetUtcNow();
        // a new code when the one held is about to lapse, or the server's waiting codes were cancelled (five wrong codes cancel them all)
        if (_code is null || _code.ExpiresAt - now < Renew || codes.Pending() == 0)
            _code = codes.Create(ServerRole.Admin, null, Lifetime);
        var announcement = new Announcement(product, instanceId, name, advertise.AbsoluteUri.TrimEnd('/'), _code.Code, _code.ExpiresAt);
        try
        {
            using var response = await http.PostAsJsonAsync(new Uri(console, Path), announcement, Json, cancellationToken).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException) { return false; }
    }

    internal async Task RunAsync(CancellationToken stopping)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var timer = new PeriodicTimer(Interval);
        do { await AnnounceOnceAsync(http, stopping).ConfigureAwait(false); }
        while (await WaitAsync(timer, stopping).ConfigureAwait(false));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken stopping)
    {
        try { return await timer.WaitForNextTickAsync(stopping).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}

public static class ConsoleAnnouncing
{
    /// <summary>Starts the announcer the environment asks for (if any) when the host has started, and stops it with the host.</summary>
    public static IEndpointRouteBuilder AnnounceToConsole(this IEndpointRouteBuilder app, string product, string instanceId, PairedClientsFile clients, PairingCodes codes)
    {
        if (ConsoleAnnouncer.FromEnvironment(product, instanceId, clients, codes) is not { } announcer) return app;
        var lifetime = app.ServiceProvider.GetRequiredService<IHostApplicationLifetime>();
        lifetime.ApplicationStarted.Register(() => _ = announcer.RunAsync(lifetime.ApplicationStopping));
        return app;
    }
}
