using System.Text.Json.Nodes;
using DbDataKit.Secrets;

namespace DbDataKit.Connections.Inventory;

/// <summary>The login role a use of a connection needs (plan 28c ED17): <c>read</c> to look (metadata, previews, tests), <c>run</c> to read sources and write targets in a run, <c>write</c> to change a target on a person's request.</summary>
public static class UseRoles
{
    public const string Read = "read";
    public const string Run = "run";
    public const string Write = "write";

    public static readonly IReadOnlyList<string> All = [Read, Run, Write];
}

/// <summary>What kinds of process use a connection. The set is open for a product that has another, but these are the ones the console groups by.</summary>
public static class ProcessKinds
{
    /// <summary>The server itself (its own browsing, tests and previews).</summary>
    public const string Server = "server";
    /// <summary>A scheduled or run-now task of a published package, on a runner.</summary>
    public const string Task = "task";
    /// <summary>A replication of Sync.</summary>
    public const string Replication = "replication";
    /// <summary>A scheduled refresh plan of Build.</summary>
    public const string Plan = "plan";
    /// <summary>A flow a person starts (Build's deploy --apply, ack and seeds).</summary>
    public const string Flow = "flow";
    /// <summary>A runner instance that has a version of a package that uses the connection ready or active.</summary>
    public const string Runner = "runner";
}

/// <summary>
/// One process that uses a connection: what it is (<paramref name="Process"/>, one of <see cref="ProcessKinds"/>), its name (a task is <c>package/task</c>), the version of the definition it runs when it has one, the runner group and instance when it runs on
/// one, and the <paramref name="Role"/> of login the use needs.
/// </summary>
public sealed record ConnectionUse(string Process, string Name, string Role, string? Version = null, string? Group = null, string? Instance = null, string? Channel = null);

/// <summary>
/// One connection of one product, as its management API reports it (<c>GET /api/v1/connections</c>). It carries where the connection goes and the <b>reference</b> to its secret, never a value: <see cref="ConnectionInventoryJson"/> drops anything that is not
/// a reference. <paramref name="Source"/> and <paramref name="Version"/> are the runner's (plan 28c E27): where the definition it holds came from (<c>file</c>, <c>copy</c>, <c>reference</c>, <c>unknown</c>) and the version of its text; a product that does not say leaves them null, and its answer has no such key.
/// </summary>
public sealed record InventoryConnection(string Name, string Kind, string? Engine, string? Host, string? Database, string? SecretRef, IReadOnlyList<ConnectionUse> UsedBy, string? RunSecretRef = null, string? Source = null, string? Version = null)
{
    /// <summary>The roles any use needs, in the order read, run, write.</summary>
    public IReadOnlyList<string> Roles => [.. UseRoles.All.Where(r => UsedBy.Any(u => u.Role == r))];
}

/// <summary>The one JSON form every product answers with, and the reading of it, so a console joins the answers of Build, Sync and a runner without knowing which is which.</summary>
public static class ConnectionInventoryJson
{
    /// <summary>
    /// <c>{product, connections: [{name, kind, engine, host, database, secret_ref, roles, used_by: [{process, name, version, group, instance, channel, role}]}]}</c>. A secret reference that does not parse as a reference (a value somebody typed where a reference belongs) is written
    /// as <c>"(not a reference)"</c>, not echoed. A host is cut at anything that looks like credentials (<c>user:password@</c>).
    /// </summary>
    public static JsonObject Write(string product, IEnumerable<InventoryConnection> connections) => new()
    {
        ["product"] = product,
        ["connections"] = new JsonArray([.. connections.OrderBy(c => c.Name, StringComparer.Ordinal).Select(c => (JsonNode)Connection(c))]),
    };

    private static JsonObject Connection(InventoryConnection c)
    {
        var node = ConnectionBody(c);
        // a second login (Sync's run role, plan 28c E20) is reported as its own key, only when there is one: the answer of a connection with one login is what it was
        if (Reference(c.RunSecretRef) is { } run) node["run_secret_ref"] = run;
        // the runner's source and version (E27), likewise only when there is one
        if (c.Source is not null) node["source"] = c.Source;
        if (c.Version is not null) node["version"] = c.Version;
        return node;
    }

    private static JsonObject ConnectionBody(InventoryConnection c) => new()
    {
        ["name"] = c.Name,
        ["kind"] = c.Kind,
        ["engine"] = c.Engine,
        ["host"] = Host(c.Host),
        ["database"] = c.Database,
        ["secret_ref"] = Reference(c.SecretRef),
        ["roles"] = new JsonArray([.. c.Roles.Select(r => (JsonNode)JsonValue.Create(r)!)]),
        ["used_by"] = new JsonArray([.. c.UsedBy.OrderBy(u => u.Process, StringComparer.Ordinal).ThenBy(u => u.Name, StringComparer.Ordinal).ThenBy(u => u.Instance, StringComparer.Ordinal)
            .Select(u => (JsonNode)new JsonObject { ["process"] = u.Process, ["name"] = u.Name, ["version"] = u.Version, ["group"] = u.Group, ["instance"] = u.Instance, ["channel"] = u.Channel, ["role"] = u.Role })]),
    };

    /// <summary>The reference as written (an <c>env:</c>, <c>secret:</c> or <c>file:</c> name), or a marker; a <c>dpapi:</c> blob is elided.</summary>
    public static string? Reference(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return SecretRef.TryParse(text, out var reference) ? reference.ToAuditString() : "(not a reference)";
    }

    private static string? Host(string? host)
    {
        if (host is null) return null;
        var at = host.LastIndexOf('@');
        return at < 0 ? host : host[(at + 1)..];
    }

    public static IReadOnlyList<InventoryConnection> Read(JsonNode? document)
    {
        var found = new List<InventoryConnection>();
        if (document?["connections"] is not JsonArray list) return found;
        foreach (var node in list.OfType<JsonObject>())
        {
            var uses = (node["used_by"] as JsonArray ?? []).OfType<JsonObject>()
                .Select(u => new ConnectionUse(Text(u, "process") ?? "", Text(u, "name") ?? "", Text(u, "role") ?? "", Text(u, "version"), Text(u, "group"), Text(u, "instance"), Text(u, "channel"))).ToList();
            found.Add(new InventoryConnection(Text(node, "name") ?? "", Text(node, "kind") ?? "", Text(node, "engine"), Text(node, "host"), Text(node, "database"), Text(node, "secret_ref"), uses, Text(node, "run_secret_ref"), Text(node, "source"), Text(node, "version")));
        }

        return found;
    }

    private static string? Text(JsonObject node, string key) => node[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}

/// <summary>The rule every product applies when a connection is removed: one that a process uses is refused, naming the users (plan 28c E23; the console does the same for a state connection).</summary>
public static class ConnectionUsers
{
    /// <summary>Null when nobody uses the connection; else the sentence a 409 carries.</summary>
    public static string? RemovalRefusal(string connection, IReadOnlyList<ConnectionUse> uses)
    {
        // the server's own browsing does not stop a removal: it holds no run, and goes with the connection
        var blocking = uses.Where(u => u.Process != ProcessKinds.Server).Select(Describe).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        if (blocking.Count == 0) return null;
        return $"The connection '{connection}' is used by {string.Join(", ", blocking)}: change {(blocking.Count == 1 ? "it" : "them")} to use another connection first, then remove it.";
    }

    public static string Describe(ConnectionUse use)
    {
        var where = use.Instance is not null ? $" on {use.Group ?? "runner"}/{use.Instance}" : use.Group is not null ? $" in {use.Group}" : "";
        var version = use.Version is null ? "" : $" v{use.Version}";
        return $"{use.Process} {use.Name}{version}{where}";
    }
}
