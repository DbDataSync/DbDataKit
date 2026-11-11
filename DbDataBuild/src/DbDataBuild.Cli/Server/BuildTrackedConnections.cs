using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli.Server;

/// <summary>The connections of a project whose tracking is on a target and whose read login is set: what a copy of the tracking tables (the Build server's, or a runner task's) goes through (plan 28c E16).</summary>
internal static class BuildTrackedConnections
{
    public static IEnumerable<(string Connection, TrackingScope Scope, LoginSettings Read)> Of(string root, string? only, Func<string, string?> env)
    {
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
}
