using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;
using DbDataKit.State;
using DbDataKit.State.Tracking;

namespace DbDataBuild.Cli.Server;

/// <summary>
/// Runs the copy of the tracking tables for the projects a Build server hosts (plan 28c E16): for each tracked connection of a project, its tracking store is read with the read login the server holds, and the rows are copied into <c>build_tracking</c> in the server's
/// state by <see cref="BuildTrackingCopier"/>. Called after every scheduled refresh and from the scheduler's tick as a catch-up, so a copy that was down catches up without a refresh. It never throws and never waits longer than its budget: a down copy or target
/// is a line in the log, and a refresh is never slowed by it.
/// </summary>
internal sealed class BuildTrackingHost(ProjectsFile projects, StateDatabase database, TrackingSettings settings, Func<CancellationToken, Task<IReadOnlyDictionary<string, string>>> logins, Action<string> log, TimeSpan? settleLag = null)
{
    /// <summary>Two Build processes write with their own clocks; rows newer than this are copied by a later run.</summary>
    public static readonly TimeSpan DefaultSettleLag = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _one = new(1, 1);

    public bool Enabled => settings.Mode != TrackingMode.Target;

    /// <summary>Copies the tracking of one project (or every hosted project when <paramref name="project"/> is null; of one connection when <paramref name="connection"/> is given), within <paramref name="budget"/> in all.</summary>
    public async Task<IReadOnlyList<BuildTrackingResult>> CopyAsync(HostedProject? project, string? connection, TimeSpan budget, CancellationToken cancellationToken)
    {
        var results = new List<BuildTrackingResult>();
        if (!Enabled || !await _one.WaitAsync(0, cancellationToken).ConfigureAwait(false)) return results;
        try
        {
            var started = DateTime.UtcNow;
            var env = await logins(cancellationToken).ConfigureAwait(false);
            string? Env(string name) => env.TryGetValue(name, out var v) ? v : Environment.GetEnvironmentVariable(name);
            foreach (var hosted in project is null ? projects.All() : [project])
            {
                foreach (var (name, scope, read) in Tracked(hosted, connection, Env))
                {
                    var remaining = budget - (DateTime.UtcNow - started);
                    if (remaining <= TimeSpan.Zero) return results;
                    results.Add(await CopyOneAsync(hosted, name, scope, read, remaining, cancellationToken).ConfigureAwait(false));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log($"The copy of the tracking tables failed ({ex.GetType().Name}); the next run tries again.");
        }
        finally
        {
            _one.Release();
        }

        return results;
    }

    private IEnumerable<(string Connection, TrackingScope Scope, LoginSettings Read)> Tracked(HostedProject hosted, string? only, Func<string, string?> env)
    {
        var root = projects.RootOf(hosted);
        if (!Directory.Exists(root)) yield break;
        var diagnostics = new List<DbDataBuild.Core.Diagnostic>();
        ProjectConfig? config;
        try { config = ProjectConfigLoader.LoadFromProject(root, diagnostics); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        if (config is null) yield break;
        foreach (var connection in config.Connections.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (only is not null && connection.Name != only) continue;
            if (config.TrackingOf(connection.Name).Target is not { } target) continue;
            var (read, _) = LoginSettings.FromEnvironment(target.Connection, target.Engine, Login.Read, env);
            if (read is null) continue;
            yield return (connection.Name, new TrackingScope(target.Engine, target.SchemaName, connection.Name), read);
        }
    }

    private async Task<BuildTrackingResult> CopyOneAsync(HostedProject hosted, string connection, TrackingScope scope, LoginSettings login, TimeSpan budget, CancellationToken cancellationToken)
    {
        try
        {
            await using var read = await ReadSession.OpenAsync(login, cancellationToken).ConfigureAwait(false);
            var status = await TrackingStore.StatusAsync(read, scope.Engine, scope.SchemaName, cancellationToken).ConfigureAwait(false);
            if (status.State != TrackingState.Ready)
            {
                var message = $"Project '{hosted.Id}', {connection}: the tracking tables are not at layout {TrackingSchema.Version} (`connection init --upgrade --apply`), so they are not copied.";
                log(message);
                return new BuildTrackingResult([], 0, false, message);
            }

            var identity = BuildTrackingIdentity.For(login, scope);
            var source = new BuildTrackingSource(read, scope, identity, settleLag ?? DefaultSettleLag);
            var copier = new BuildTrackingCopier(identity, source, new BuildTrackingStore(database), (kind, json) => MetadataInfoRows.HasRows(kind) ? MetadataInfoRows.From(kind, json) : null);
            var result = await copier.RunAsync(budget, cancellationToken).ConfigureAwait(false);
            foreach (var problem in result.Problems) log($"Project '{hosted.Id}', {connection}: the copy of the tracking tables: {problem}");
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = $"Project '{hosted.Id}', {connection}: the tracking store could not be read for the copy ({ex.GetType().Name}).";
            log(message);
            return new BuildTrackingResult([], 0, false, message);
        }
    }
}
