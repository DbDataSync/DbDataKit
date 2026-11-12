using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli.Server;

/// <summary>The connections of a project whose tracking is on a target and whose read login is set: what a copy of the tracking tables (the Build server's, or a runner task's) goes through (plan 28c E16).</summary>
internal static class BuildTrackedConnections
{
    /// <summary>True when the project says `tracking: { runner_copy: false }` (plan 28c E16, owner 2026-11-11): the runner keeps no copy, so its follower step and the `catch-up` task do nothing. A project that cannot be read is not switched off.</summary>
    public static bool RunnerCopyOff(string root)
    {
        if (!Directory.Exists(root)) return false;
        try { return ProjectConfigLoader.LoadFromProject(root, new List<DbDataBuild.Core.Diagnostic>()) is { Tracking.RunnerCopy: false }; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>True when the project's tracking is state-first (plan 28c E16): its runs write the records to the state before the target, so no follower copies them.</summary>
    public static bool StateFirst(string root, Func<string, string?> env)
    {
        if (!Directory.Exists(root)) return false;
        try { return ProjectConfigLoader.LoadFromProject(root, new List<DbDataBuild.Core.Diagnostic>()) is { } config && TrackingJournals.IsStateFirst(config, env); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <param name="includeStateFirst">A project whose tracking is state-first (plan 28c E16) writes its records to the state itself, so no follower copies it; the check before a switch of the primary compares the two sides anyway.</param>
    public static IEnumerable<(string Connection, TrackingScope Scope, LoginSettings Read)> Of(string root, string? only, Func<string, string?> env, bool includeStateFirst = false)
    {
        if (!Directory.Exists(root)) yield break;
        var diagnostics = new List<DbDataBuild.Core.Diagnostic>();
        ProjectConfig? config;
        try { config = ProjectConfigLoader.LoadFromProject(root, diagnostics); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { yield break; }
        if (config is null) yield break;
        if (!includeStateFirst && TrackingJournals.IsStateFirst(config, env)) yield break;
        foreach (var connection in config.Connections.Values.OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            if (only is not null && connection.Name != only) continue;
            if (config.TrackingOf(connection.Name).Target is not { } target) continue;
            var (read, _) = LoginSettings.FromEnvironment(target.Connection, target.Engine, Login.Read, env);
            if (read is null) continue;
            yield return (connection.Name, new TrackingScope(target.Engine, target.SchemaName, connection.Name), read);
        }
    }
}
