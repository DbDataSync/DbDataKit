using DbDataKit.Dialect;

namespace DbDataBuild.Models;

/// <summary>Identifier quoting for the engines Build writes to, taken from the shared dialects (plan 13, B01) so every product quotes a name the same way and refuses the same names (empty, control characters).</summary>
public static class SharedQuoting
{
    /// <summary>The shared dialect for a Build target or built-in profile name, or null when there is none (a project's own profile).</summary>
    public static ISqlDialect? For(string name) => DialectCatalog.Builtin.TryGet(name, out var dialect) ? dialect : null;

    /// <summary>The identifier quoted for <paramref name="target"/> (<c>sqlserver</c>, <c>fabric</c> or <c>postgres</c>).</summary>
    public static string Quote(string target, string identifier) =>
        (For(target) ?? throw new ArgumentException($"No shared dialect for '{target}'.", nameof(target))).Quote(identifier);
}
