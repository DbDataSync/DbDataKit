using DbDataKit.State;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DbDataGateway.Server.State;

/// <summary>
/// The gateway's own state database (plan 13, P02; decision D18; plan 29a Q16): SQLite through <c>DbDataKit.State</c>, in the data directory beside the certificate, created on first start; or SQL Server
/// or PostgreSQL when the operator names one (P11). It is built from modules, each applied and versioned on its own: <c>identity</c> (the identity home, plan 20 O06: its tables are there whether or not
/// the home is on), <c>notifications</c>, <c>audit</c> (the management audit), and the modules kept per group: <c>definitions</c>, <c>app_storage</c>, <c>trust</c> (the group's instance id, paired clients,
/// pairing codes, key check and secrets) and the gateway's own <see cref="ServingModule"/> (client sessions, running instances, saved queries). A state database made by the earlier structure, one schema
/// version for the whole database with the gateway's tables interleaved among the home's, is refused with a message naming it.
/// </summary>
public static class GatewayState
{
    public const string FileName = "state.db";

    /// <summary>The service name the gateway records on what it writes to a database it may share (the feed's and the audit's <c>service_id</c>).</summary>
    public const string ServiceId = "dbdatagateway";

    /// <summary>The group the instances of one gateway belong to when none is named: every instance that shares a database and does not say otherwise is one group.</summary>
    public const string DefaultGroup = "main";

    /// <summary>The modules the gateway's state is built from.</summary>
    public static IReadOnlyList<StateModule> Modules { get; } =
    [
        IdentityModule.Module, NotificationsModule.Module, AuditModule.Module,
        DefinitionsModule.Module, AppStorageModule.Module, DbDataKit.Web.Trust.TrustModule.Module, ServingModule.Module, UserViewsModule.Module, SemanticRunsModule.Module, PayloadStore.Module,
    ];

    public static StateScope ScopeOf(string? group = null) => new(Group: string.IsNullOrWhiteSpace(group) ? DefaultGroup : group, Service: ServiceId);

    public static string PathIn(string dataDirectory) => Path.Combine(dataDirectory, FileName);

    /// <summary>Whether the state is a group's (SQL Server or PostgreSQL), whose instances share their identity and pairings; a SQLite state is one instance's.</summary>
    public static bool IsGroup(StateDatabase state) => !string.Equals(state.Dialect.Engine, DbDataKit.State.StateEngineIds.Sqlite, StringComparison.OrdinalIgnoreCase);

    public static StateDatabase Open(string dataDirectory, StateSettings? settings = null, Func<string, string>? resolveConnectionString = null, string? group = null)
    {
        Directory.CreateDirectory(dataDirectory);
        settings ??= new StateSettings();
        // the secret reference in a server engine's connection string is resolved here, so the password is read when the store is opened and never written to the settings
        if (!settings.IsSqlite && settings.ConnectionString is { Length: > 0 } text && resolveConnectionString is not null)
            settings = settings with { ConnectionString = resolveConnectionString(text) };
        return StateOpener.Open(settings, PathIn(dataDirectory), Modules, ScopeOf(group), appliedBy: $"{ServiceId} {typeof(GatewayState).Assembly.GetName().Version}", groupInstances: new StandardGroupInstanceSource());
    }
}

/// <summary>Opens the state database when the service starts, so a first start creates it and a store that cannot be opened stops the service there, not at the first request that needs it.</summary>
public sealed class GatewayStateStartup(StateDatabase database, ILogger<GatewayStateStartup> log, GroupSessionKey? sessionKey = null) : IHostedLifecycleService
{
    public Task StartingAsync(CancellationToken cancellationToken)
    {
        using var connection = database.OpenConnection();
        log.LogInformation("State database ready ({Engine}).", database.Dialect.Engine);
        // the group's session key, checked against the state now: a wrong one stops the start here, naming the secret
        sessionKey?.Ensure();
        return Task.CompletedTask;
    }

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
