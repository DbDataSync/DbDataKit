using System.Text.RegularExpressions;

namespace DbDataKit.State;

/// <summary>
/// The state login of one service and group on a database server several services share, and the namespaces that keep that login's tables apart from every other login's. The login is named
/// <c>dbdatakit_&lt;service&gt;[_&lt;group&gt;]</c> (<c>dbdatakit_gateway_sales</c>, <c>dbdatakit_runner_main</c>, <c>dbdatakit_console</c>), and every schema its modules and its registry use starts with that name,
/// so <see cref="StateGrants"/> for the login names schemas no other service's login is granted. A module kept per install, which one database would otherwise share between services, is one copy per login.
/// Design: docs/state-logins.md#one-login-per-service-and-group
/// </summary>
public static partial class StateLogins
{
    private const string Prefix = "dbdatakit_";

    [GeneratedRegex("[^a-z0-9_]")]
    private static partial Regex NotNameChars { get; }

    /// <summary>The login's name for a service and a group. A service is named by its program (<c>dbdatagateway</c>) without the <c>dbdata</c> in front; the console's program, <c>dbdatakit</c>, is <c>console</c>.</summary>
    /// <param name="service">The service name the process records (<see cref="StateScope.Service"/>); null or empty: <paramref name="program"/>.</param>
    /// <param name="group">The group; null or empty: the login is the service's alone (the console).</param>
    /// <param name="program">The command's name, used when the scope names no service.</param>
    public static string Name(string? service, string? group, string program)
    {
        var name = Slug(string.IsNullOrWhiteSpace(service) ? program : service);
        if (name.StartsWith("dbdata", StringComparison.Ordinal)) name = name["dbdata".Length..];
        if (name is "" or "kit") name = "console";
        return Prefix + name + (string.IsNullOrWhiteSpace(group) ? "" : "_" + Slug(group));
    }

    /// <summary>The namespaces of the login's own tables: a schema per module that starts with the login's name, and a registry of its own, <c>&lt;login&gt;_registry.modules</c>, so two logins never share a module's row.</summary>
    public static StateNamespaceSettings Namespaces(string login) => new()
    {
        Default = new NamespaceSetting(Schema: login + "_{module}"),
        Registry = new NamespaceSetting(Schema: login + "_registry"),
    };

    /// <summary>True when the settings name no schema, prefix, file or registry of their own: the built-in names, which are the same for every service of a database.</summary>
    public static bool IsDefault(StateNamespaceSettings settings) =>
        settings.Default is { Schema: null, Prefix: null, File: null } && settings.Modules.Count == 0 && settings.Registry is { Schema: null, Prefix: null, File: null };

    private static string Slug(string text) => NotNameChars.Replace(text.Trim().ToLowerInvariant(), "_");
}
