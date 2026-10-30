using DbDataKit.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataProxy.Server.State;

/// <summary>
/// The proxy's own state database (plan 13, P02; decision D18): SQLite through <c>DbDataKit.State</c>, in the data directory beside the certificate, created on first start; or SQL Server or PostgreSQL when the operator names one (P11).
/// Today it holds the notification feed and each console's read position; the audit log and the rest of what a console shows are added to <see cref="Schema"/> by the slices
/// that produce them (P06, P10). Append to the schema, never edit an entry that has shipped.
/// </summary>
public static class ProxyState
{
    public const string FileName = "state.db";

    /// <summary>The proxy's tables, in the order they were added.</summary>
    public static StateSchema Schema { get; } = new([NotificationSchema.Feed, SavedQuerySchema.Tables]);

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    public static StateDatabase Open(string dataDirectory, StateSettings? settings = null, Func<string, string>? resolveConnectionString = null)
    {
        Directory.CreateDirectory(dataDirectory);
        settings ??= new StateSettings();
        // the secret reference in a server engine's connection string is resolved here, so the password is read when the store is opened and never written to the settings
        if (!settings.IsSqlite && settings.ConnectionString is { Length: > 0 } text && resolveConnectionString is not null)
            settings = settings with { ConnectionString = resolveConnectionString(text) };
        return StateOpener.Open(settings, PathIn(dataDirectory), Schema);
    }
}

/// <summary>Opens the state database when the service starts, so a first start creates it and a store that cannot be opened stops the service there, not at the first request that needs it.</summary>
public sealed class ProxyStateStartup(StateDatabase database, ILogger<ProxyStateStartup> log) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        log.LogInformation("State database ready ({Engine}).", database.Dialect.Engine);
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
