using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>How widely one copy of a module is shared (plan 29 section 3.2): the whole install, one serving or runner group, or one service.</summary>
public enum StateScopeKind
{
    Install,
    Group,
    Service,
}

/// <summary>Which group and which service the process opening the database is, so the modules that are kept per group or per service find their copy.</summary>
/// <param name="Group">The group's name; needed when a module of <see cref="StateScopeKind.Group"/> is opened.</param>
/// <param name="Service">The service's name; needed when a module of <see cref="StateScopeKind.Service"/> is opened.</param>
public sealed record StateScope(string? Group = null, string? Service = null)
{
    public static StateScope Install { get; } = new();

    /// <summary>The name the scope contributes to a namespace, or null for the install scope.</summary>
    public string? NameFor(StateScopeKind kind, string module) => kind switch
    {
        StateScopeKind.Install => null,
        StateScopeKind.Group => Group is { Length: > 0 } g ? g : throw new InvalidOperationException($"The module '{module}' is kept per group, and no group name was given."),
        _ => Service is { Length: > 0 } s ? s : throw new InvalidOperationException($"The module '{module}' is kept per service, and no service name was given."),
    };
}

/// <summary>
/// A set of tables owned by one library, with its own version and its own append-only list of migrations (plan 29 section 3.1). A database holds several modules; each
/// is applied and versioned on its own, so a library adds a migration to its module without any product changing, and no product interleaves scripts.
/// <para>
/// A migration is a template, written like the existing <see cref="StateSchema"/>'s (<c>{{text}}</c>, <c>{{key}}</c>, ...) with one change: it never writes a table or index name.
/// It writes <c>{table:users}</c> (its own module's table) or <c>{table:identity.users}</c> (a declared dependency's), and <c>{index:users_by_name}</c>, and the namespace's schema and
/// prefix decide the real names. Never edit or reorder a migration that has shipped; append.
/// </para>
/// </summary>
public sealed partial class StateModule
{
    [GeneratedRegex("^[a-z][a-z0-9_]*$")]
    private static partial Regex NamePattern { get; }

    public StateModule(
        string name,
        StateScopeKind scope,
        IReadOnlyList<string> migrations,
        IEnumerable<StateModule>? dependsOn = null,
        IReadOnlyList<RetentionRule>? retention = null)
    {
        if (!NamePattern.IsMatch(name))
            throw new ArgumentException($"A module name is lower case letters, digits and underscores, starting with a letter; '{name}' is not.", nameof(name));
        ArgumentNullException.ThrowIfNull(migrations);

        Name = name;
        Scope = scope;
        Migrations = [.. migrations];
        DependsOn = [.. dependsOn ?? []];
        Retention = [.. retention ?? []];

        foreach (var dependency in DependsOn)
        {
            if (dependency.Scope > scope)
                throw new ArgumentException($"The module '{name}' is kept per {scope.ToString().ToLowerInvariant()} and cannot depend on '{dependency.Name}', which is kept per {dependency.Scope.ToString().ToLowerInvariant()}: a narrower module is not one copy of a wider one's data.", nameof(dependsOn));
        }
    }

    public string Name { get; }

    public StateScopeKind Scope { get; }

    /// <summary>The migrations in order; the module's version is how many of them a database has applied.</summary>
    public IReadOnlyList<string> Migrations { get; }

    /// <summary>The modules whose tables this one refers to (by <c>{table:module.name}</c>) and that must be applied first. Object references, so there is no cycle to detect and no name to misspell.</summary>
    public IReadOnlyList<StateModule> DependsOn { get; }

    /// <summary>What history this module keeps and for how long (plan 29 section 3.3); empty for a module that holds only current state.</summary>
    public IReadOnlyList<RetentionRule> Retention { get; }

    /// <summary>The version a database is at when every migration of this module has been applied.</summary>
    public int Version => Migrations.Count;

    public override string ToString() => Name;
}
