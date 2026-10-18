using DbDataBuild.Core;
using DbDataBuild.Execution;
using DbDataBuild.Models;
using DbDataBuild.State;

namespace DbDataBuild.Cli;

/// <summary>The tracking connection of a data connection, with its logins and the scope of its records.</summary>
internal sealed record TrackingLogins(TrackingScope Scope, LoginSettings Read, LoginSettings? Write, TrackingTarget Target);

/// <summary>
/// Which tracking store a command works with. A command that works on the records the tool keeps (`ack`, `report`, `publish-metadata`, `init`) needs the data connection to be tracked: the records are kept on its
/// tracking connection (its own `tracking:`, else the project's), which can be another connection, with that connection's logins.
/// </summary>
internal static class CommandTracking
{
    /// <summary>Resolves the tracking of <paramref name="data"/> and its logins; writes the reason and returns null when the connection is not tracked or a login is missing.</summary>
    public static TrackingLogins? Require(ProjectConfig config, ConnectionConfig data, Func<string, string?> env, bool needWrite, TextWriter error, string command)
    {
        var resolved = config.TrackingOf(data.Name);
        if (resolved.Target is not { } t)
        {
            error.WriteLine($"`{data.Name}` is not tracked, and `{command}` works on the records the tool keeps. Say where they go with `tracking: {{ connection: <name> }}` in {ProductInfo.ConfigFile}{(resolved.Explicit ? " (the connection has `tracking: none`)" : "")}.");
            return null;
        }
        var (read, readMissing) = LoginSettings.FromEnvironment(t.Connection, t.Engine, Login.Read, env);
        var (write, writeMissing) = needWrite ? LoginSettings.FromEnvironment(t.Connection, t.Engine, Login.Write, env) : (null, null);
        foreach (var m in new[] { readMissing, writeMissing }.OfType<Diagnostic>()) error.Diag(m);
        if (read == null || (needWrite && write == null)) return null;
        return new TrackingLogins(new TrackingScope(t.Engine, t.SchemaName, data.Name), read, write, t);
    }
}
