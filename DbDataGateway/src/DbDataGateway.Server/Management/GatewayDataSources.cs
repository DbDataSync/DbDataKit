using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Git;
using DbDataKit.Secrets;
using DbDataKit.State;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Auth;
using DbDataGateway.Server.Data;
using Microsoft.Extensions.Configuration;

namespace DbDataGateway.Server.Management;

/// <summary>
/// Data sources through the management API (plan 13, P04): list, read, create or change, delete and test the files of <c>connections/</c>. A write is a whole <c>connection/1</c> file, validated with the rest of the configuration as it would be
/// once written (a file that would make the configuration invalid is refused with the diagnostics and nothing is written), checked against the version the caller read (a stale write is a 409 with the current version), written, committed to the
/// configuration folder's git history as the person who made it, noted in the audit, and applied at once: the options monitor is told, and the next query sees the change without a restart.
/// <para>
/// In a group (plan 20 O04b), a data source a client publishes is the group's: it is kept in the state as a published definition (<see cref="PublishedDataSources"/>), not in this instance's folder or its history, so
/// every instance serves it. One made by hand stays a file of the instance it was made on.
/// </para>
/// </summary>
public sealed partial class GatewayDataSources(
    string configDirectory, GatewayManagement validation, Action reload, Func<DataSourceCatalog> catalog, IAuditLog audit, GitCommitService? git, TimeProvider? clock = null, Func<string, PairedClient?>? findClient = null,
    DbDataGateway.Server.Serving.PublishedDataSources? published = null, DataSourceAdoptions? adoptions = null, Func<DbDataKit.Serving.IServingCatalog>? servingCatalog = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WriteGuard _guard = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string Folder => Path.Combine(configDirectory, "connections");

    private string FileOf(string name) => Path.Combine(Folder, name + ".yaml");

    /// <summary>The data source's file: this instance's own, else the group's published one; null when there is neither.</summary>
    private string? TextOf(string name) => File.Exists(FileOf(name)) ? File.ReadAllText(FileOf(name)) : published?.Text(name);

    /// <summary>Whether a write to the name goes to the group: a group's instance, no file of its own under the name, and either a client publishing or a data source the group already has.</summary>
    private bool ToGroup(string name, ManagementCaller caller) =>
        published is { Enabled: true } && !File.Exists(FileOf(name)) && (!IsAdministrator(caller) || published.Text(name) is not null);

    private string Who(ManagementCaller caller) { var entry = caller.Audit("", null, "", _clock.GetUtcNow()); return entry.UserName ?? entry.UserId; }

    /// <summary>The version of a file: the SHA-256 of its text, which a caller names in its next write.</summary>
    public static string VersionOf(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public async Task<ManagementResult> HandleAsync(string method, string[] rest, string? body, string? expectedVersion, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (rest.Length == 0) return method == "GET" ? ManagementResult.Ok(List(OwnedBy(caller))) : ManagementResult.Problem(405, "Data sources are listed with GET.");
        var name = Uri.UnescapeDataString(rest[0]);
        if (!NamePattern().IsMatch(name)) return ManagementResult.Problem(400, "A data source name is a letter followed by letters, digits, '.', '_' or '-' (up to 64).");

        // taking over a hand-made data source is a person's act with the admin role (OwnerScope.AuthorizeAdopt), whatever a server client's ceiling says: decided before the owner rules, which a client would fail with another sentence
        if (rest.Length == 2 && rest[1] == "adopt")
            return method == "POST" ? await AdoptAsync(name, body, caller, cancellationToken) : ManagementResult.Problem(405, "A data source is adopted with POST.");

        // plan 33 SC20j: the shape of the data source's catalog (tables, columns, keys: never a row, a login or an address) for a Workspace's catalog cache; decided before the owner rules, which it applies in its own words
        if (rest.Length == 2 && rest[1] == "schema")
            return method == "POST" ? await SchemaAsync(name, body, caller, cancellationToken) : ManagementResult.Problem(405, "A data source's schema is read with POST (the body may carry \"adopt\").");

        // a publishing client reaches only the data sources it made (the owner rules, plan 13 W12); an administrator reaches all. The one exception (plan 13c D19) is a write that says `adopt: true` over a data source made by hand
        var action = method switch { "GET" or "POST" => ObjectAction.Change, "DELETE" => ObjectAction.Delete, _ => TextOf(name) is not null ? ObjectAction.Change : ObjectAction.Create };
        var adopting = method == "PUT" && ReadWrite(body)?.Adopt == true && !IsAdministrator(caller) && IsHandMade(name);
        var refused = adopting ? AdoptScope(caller, name) : Scope(caller, action, name);
        if (refused is not null)
        {
            Record(caller, $"data-source {method.ToLowerInvariant()}", name, "refused");
            // a write over a data source made by hand, without adopt: not the client's to change, and the way to take it over is said
            return method == "PUT" && IsHandMade(name) && !IsAdministrator(caller) ? ManagementResult.Problem(409, NotAdoptedSentence(name)) : refused;
        }

        if (rest.Length == 2 && rest[1] == "test") return method == "POST" ? await TestAsync(name, cancellationToken) : ManagementResult.Problem(405, "A data source is tested with POST.");
        if (rest.Length != 1) return ManagementResult.Problem(404, "There is no such call.");
        return method switch
        {
            "GET" => Read(name),
            "PUT" => await WriteAsync(name, body, caller, cancellationToken),
            "DELETE" => await DeleteAsync(name, expectedVersion, caller, cancellationToken),
            _ => ManagementResult.Problem(405, "A data source is read with GET, written with PUT, removed with DELETE."),
        };
    }

    /// <summary>The secret references the data sources name (the passwords, tokens and whole connection strings of their logins), for the secrets overview (UI06): this instance's files and the group's published ones. References only, none is resolved.</summary>
    public IEnumerable<ReferenceUse> References()
    {
        var texts = new List<(string Name, string Text)>();
        if (Directory.Exists(Folder))
            foreach (var file in Directory.EnumerateFiles(Folder, "*.yaml").OrderBy(f => f, StringComparer.Ordinal)) texts.Add((Path.GetFileNameWithoutExtension(file), File.ReadAllText(file)));
        foreach (var (name, text) in published?.All() ?? []) if (!texts.Any(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase))) texts.Add((name, text));
        foreach (var (name, text) in texts)
        {
            if (DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, name + ".yaml").Definition is not { } d) continue;
            var found = new List<string?> { d.ConnectionStringSecret };
            foreach (var login in new[] { d.Auth, d.ReadLogin, d.WriteLogin }) if (login is not null) found.AddRange([login.PasswordSecret, login.ConnectionStringSecret, login.TokenSecret]);
            var references = found.Where(f => !string.IsNullOrWhiteSpace(f)).Select(f => SecretRef.TryParse(f, out var r) ? r : null).Where(r => r is not null).Select(r => r!).ToList();
            references.AddRange(ConnectionStringReferences.Find(d.ConnectionString).Select(r => r.Reference));
            foreach (var reference in references.Distinct()) yield return new ReferenceUse(name, reference);
        }
    }

    private JsonObject List(string? owner)
    {
        var items = new JsonArray();
        if (Directory.Exists(Folder))
            foreach (var file in Directory.EnumerateFiles(Folder, "*.yaml").OrderBy(f => f, StringComparer.Ordinal))
            {
                var text = File.ReadAllText(file);
                var loaded = DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, file);
                if (owner is not null && loaded.Definition?.ManagedBy != owner) continue;
                items.Add(new JsonObject
                {
                    ["name"] = Path.GetFileNameWithoutExtension(file), ["version"] = VersionOf(text), ["valid"] = loaded.Ok,
                    ["engine"] = loaded.Definition?.Engine, ["access"] = loaded.Definition?.Access.ToString().ToLowerInvariant(), ["managed_by"] = loaded.Definition?.ManagedBy,
                    ["grants"] = GrantsView(loaded.Definition, group: false), ["policy"] = PolicyView(loaded.Definition, text, group: false),
                });
            }
        // the group's published data sources, after this instance's own (a name of its own wins)
        var own = items.Select(i => (string)i!["name"]!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in published?.All() ?? [])
        {
            if (own.Contains(name)) continue;
            var loaded = DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, name + ".yaml");
            if (owner is not null && loaded.Definition?.ManagedBy != owner) continue;
            items.Add(new JsonObject
            {
                ["name"] = name, ["version"] = VersionOf(text), ["valid"] = loaded.Ok, ["engine"] = loaded.Definition?.Engine, ["access"] = loaded.Definition?.Access.ToString().ToLowerInvariant(),
                ["managed_by"] = loaded.Definition?.ManagedBy, ["group"] = true, ["grants"] = GrantsView(loaded.Definition, group: true), ["policy"] = PolicyView(loaded.Definition, text, group: true),
            });
        }
        return new JsonObject { ["data_sources"] = items };
    }

    private sealed record SchemaRequest(string? Adopt = null);

    /// <summary>
    /// <c>POST /api/v1/data-sources/{name}/schema</c> (plan 33 SC20j, D20): what the data source's catalog holds (<see cref="DataSourceSchema"/>), read with the Gateway's own login so that the caller needs none. An administrator reads any; a publishing client
    /// the ones it made, and a data source made by hand only when the body says <c>{"adopt": "&lt;name&gt;"}</c> (the same explicit, recorded consent as a model on it, SC21f: who, when and which go into the audit and the data source's metadata); one another client made, never.
    /// </summary>
    private async Task<ManagementResult> SchemaAsync(string name, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (servingCatalog is null) return ManagementResult.Problem(404, "This Gateway cannot read a data source's catalog here.");
        SchemaRequest? request = null;
        try { if (!string.IsNullOrWhiteSpace(body)) request = JsonSerializer.Deserialize<SchemaRequest>(body, Json); }
        catch (JsonException) { return ManagementResult.Problem(400, "The body is empty or {\"adopt\": \"<data source>\"}."); }
        if (TextOf(name) is not { } text) return ManagementResult.Problem(404, $"There is no data source '{name}'.");
        var definition = DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, name + ".yaml").Definition;
        var adopting = false;
        if (!IsAdministrator(caller))
        {
            var client = OwnerScope.Stamp(caller.Token!);
            var owner = definition?.ManagedBy;
            if (owner != client)
            {
                if (!string.IsNullOrEmpty(owner))
                {
                    Record(caller, "data-source schema", name, "refused");
                    return ManagementResult.Problem(403, $"The data source '{name}' was made by another server client; a publishing client reads the catalog of its own data sources. Ask an administrator to hand it over.");
                }
                if (!string.Equals(request?.Adopt, name, StringComparison.Ordinal))
                {
                    Record(caller, "data-source schema", name, "refused");
                    return new ManagementResult(409, new JsonObject
                    {
                        ["error"] = $"The data source '{name}' was made by hand or by an administrator, not by this client. Ask again with \"adopt\": \"{name}\" to read its catalog (its tables and columns, never rows); the Gateway records who adopted it and when, in its audit and in the data source's metadata.",
                        ["adopt"] = new JsonObject { ["data_source"] = name, ["made_by"] = "hand", ["publish_adopt"] = true, ["field"] = "adopt" },
                    });
                }
                if (OwnerScope.AuthorizePublishingOver(caller.Token!, new ManagedObject(null)) is { Allowed: false } denied)
                    return ManagementResult.Problem(403, denied.Reason ?? "This client may not read that data source.");
                adopting = true;
            }
        }
        JsonObject answer;
        try { answer = await DataSourceSchema.ReadAsync(servingCatalog(), name, definition?.Engine ?? "", _clock.GetUtcNow(), cancellationToken); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Record(caller, "data-source schema", name, "failed");
            // the type of the failure, never the driver's text: it can carry the address or the login
            return ManagementResult.Problem(502, $"The catalog of the data source '{name}' could not be read ({ex.GetType().Name}). Test the data source first.");
        }
        if (adopting)
        {
            adoptions?.Record(name, OwnerScope.Stamp(caller.Token!), Who(caller), "", "schema-read", _clock.GetUtcNow());
            Record(caller, "data-source adopt", name, $"adopted by {OwnerScope.Stamp(caller.Token!)} ({Who(caller)}) to read its catalog");
        }
        else Record(caller, "data-source schema", name, "read");
        return ManagementResult.Ok(answer);
    }

    private ManagementResult Read(string name)
    {
        if (TextOf(name) is not { } text) return ManagementResult.Problem(404, $"There is no data source '{name}'.");
        var group = !File.Exists(FileOf(name));
        return ManagementResult.Ok(new JsonObject
        {
            ["name"] = name, ["text"] = text, ["version"] = VersionOf(text), ["group"] = group,
            ["grants"] = GrantsView(DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, name + ".yaml").Definition, group),
            ["policy"] = PolicyView(DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, name + ".yaml").Definition, text, group),
            // plan 33 SC21f: the publishing clients that put a model on this data source with `adopt` (who, when, for which model)
            ["adoptions"] = adoptions?.Read(name) ?? [],
        });
    }

    /// <summary>
    /// The serving policy of a data source and where it comes from, for a screen that shows it and changes nothing (plan 13c D19): <c>fields</c> are the fields of <c>extensions.gateway</c> that a definition carries (what a publish
    /// replaces), <c>blocked</c> the two a definition never carries (<c>allow_writes</c>, <c>allowed_directories</c>), shown when a file made by hand has them; a publish replaces the whole file and they are gone. <c>origin</c> is <c>definition</c> when a client
    /// publishes the data source (the group's, or one with a <c>managed_by</c>), else <c>file</c>. Null when the file did not load.
    /// </summary>
    internal static JsonObject? PolicyView(DbDataKit.Connections.ConnectionDefinition? definition, string text, bool group)
    {
        if (definition is null) return null;
        var fields = new JsonObject();
        var blocked = new JsonObject();
        if (DbDataKit.Connections.ServingPolicy.BlockOf(text) is { } block)
            foreach (var entry in block.Entries)
            {
                if (DbDataKit.Connections.ServingPolicy.GrantKeys.Contains(entry.Key.Value)) continue;   // who may use it is shown as grants
                (DbDataKit.Connections.ServingPolicy.Blocked.Contains(entry.Key.Value) ? blocked : fields)[entry.Key.Value] = DbDataKit.Connections.ServingPolicy.Describe(entry.Value);
            }
        return new JsonObject { ["origin"] = group || !string.IsNullOrEmpty(definition.ManagedBy) ? "definition" : "file", ["fields"] = fields, ["blocked"] = blocked };
    }

    /// <summary>
    /// Who may use a data source, for a screen that shows it and changes nothing (plan 22a GP03, owner decision of 2026-11-08): the groups and <c>everyone</c> the definition says (its <c>grants</c>, or in a file made by hand
    /// <c>extensions.gateway</c>). <c>source</c> is <c>definition</c> when a client publishes the data source (the group's, or one with a <c>managed_by</c>) or the file has <c>grants</c>: the grants are then the project's, a publish replaces them,
    /// and the Gateway adds none; otherwise <c>file</c>, the hand-made file's own policy. Null when the file did not load.
    /// </summary>
    internal static JsonObject? GrantsView(DbDataKit.Connections.ConnectionDefinition? definition, bool group)
    {
        if (definition is null) return null;
        var groups = new List<string>();
        var everyone = false;
        if (definition.Grants is { } grants) { groups.AddRange(grants.Groups); everyone = grants.Everyone; }
        else if (definition.Extensions.Items.TryGetValue("gateway", out var node) && node is DbDataKit.Config.Yaml.YamlMapping gateway)
        {
            if (gateway.Get("groups") is DbDataKit.Config.Yaml.YamlSequence list) groups.AddRange(list.Items.OfType<DbDataKit.Config.Yaml.YamlScalar>().Select(i => i.Value));
            everyone = gateway.Get("everyone") is DbDataKit.Config.Yaml.YamlScalar { Value: var e } && bool.TryParse(e, out var open) && open;
        }
        var fromDefinition = group || definition.Grants is not null || !string.IsNullOrEmpty(definition.ManagedBy);
        return new JsonObject { ["groups"] = new JsonArray([.. groups.Select(g => (JsonNode?)JsonValue.Create(g))]), ["everyone"] = everyone, ["source"] = fromDefinition ? "definition" : "file" };
    }

    private sealed record WriteRequest(
        string? Text, [property: System.Text.Json.Serialization.JsonPropertyName("expected_version")] string? ExpectedVersion,
        [property: System.Text.Json.Serialization.JsonPropertyName("adopt")] bool Adopt = false);

    private static WriteRequest? ReadWrite(string? body)
    {
        try { return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<WriteRequest>(body, Json); }
        catch (JsonException) { return null; }
    }

    /// <summary>Whether the data source exists and nothing manages it: made by hand, or by an administrator (no <c>managed_by</c>).</summary>
    private bool IsHandMade(string name) =>
        TextOf(name) is { } text && string.IsNullOrEmpty(DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, name + ".yaml").Definition?.ManagedBy);

    private ManagementResult? AdoptScope(ManagementCaller caller, string name)
    {
        var decision = OwnerScope.AuthorizePublishingOver(caller.Token!, new ManagedObject(null));
        return decision.Allowed ? null : ManagementResult.Problem(403, decision.Reason ?? "This client may not do this to that data source.");
    }

    private static string NotAdoptedSentence(string name) =>
        $"The data source '{name}' was made by hand on this Gateway, so a publishing client does not replace it unasked: its file, serving policy included, would be gone. Publish again with \"adopt\": true to take it over (the replaced file is kept in the history, "
        + "the answer lists every field the definition changed or dropped, and a file's allow_writes and allowed_directories are gone with it: a definition never carries them), or ask an administrator to hand it over.";

    private static string Describe(IEnumerable<string> fields) => string.Join(", ", fields);

    private async Task<ManagementResult> WriteAsync(string name, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        WriteRequest? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<WriteRequest>(body, Json); }
        catch (JsonException) { request = null; }
        if (request?.Text is not { Length: > 0 } text)
            return ManagementResult.Problem(400, "The body is a JSON object with the file's text and the version it is based on: {\"text\": \"schema: connection/1 ...\", \"expected_version\": \"<hash>\" or null to create}.");

        var existing = TextOf(name);
        var publishing = !IsAdministrator(caller);
        var adopting = publishing && request.Adopt && existing is not null && IsHandMade(name);
        if (publishing)
        {
            // what a publishing client may send: the serving policy of the definition (plan 13c D19), but not allow_writes or allowed_directories, and the file says whose it is (stamped here when it does not)
            var sent = DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, $"{name}.yaml").Definition;
            var client = OwnerScope.Stamp(caller.Token!);
            if (DbDataKit.Connections.ServingPolicy.BlockOf(text) is { } block && DbDataKit.Connections.ServingPolicy.Check(block) is { Count: > 0 } problems)
            {
                Record(caller, "data-source write", name, "refused");
                var blocked = problems.FirstOrDefault(p => p.IsBlocked);
                if (blocked is not null)
                    return ManagementResult.Problem(403, $"{blocked.Message.Replace("<name>", name, StringComparison.Ordinal)} It is on line {blocked.At.Line} of the data source text sent. {blocked.Fix}"
                        + (problems.Count > 1 ? $" Other problems: {string.Join(" ", problems.Where(p => !p.IsBlocked).Select(p => $"{p.Message} (line {p.At.Line})"))}" : ""));
                return ManagementResult.Problem(400, string.Join(" ", problems.Select(p => $"{p.Message} (line {p.At.Line}){(p.Fix is null ? "" : " " + p.Fix)}")));
            }
            if (!string.IsNullOrEmpty(sent?.ManagedBy) && sent.ManagedBy != client)
            {
                Record(caller, "data-source write", name, "refused");
                return ManagementResult.Problem(403, "The file names another client in managed_by.");
            }
            if (string.IsNullOrEmpty(sent?.ManagedBy)) text = (text.EndsWith('\n') ? text : text + "\n") + $"managed_by: {client}\n";
        }

        // The file as it would be, among the others: refused with the reasons if it would make the configuration invalid, before anything is written.
        var candidate = validation.ValidateFiles(new Dictionary<string, string> { [$"connections/{name}.yaml"] = text });
        if (candidate.Body["valid"]?.GetValue<bool>() != true)
            return new ManagementResult(400, new JsonObject { ["error"] = "The data source is not valid, so nothing was written.", ["valid"] = false, ["diagnostics"] = candidate.Body["diagnostics"]!.DeepClone() });

        var expectedVersion = adopting ? VersionOf(existing!) : request.ExpectedVersion;   // a publisher cannot read a file made by hand: adopting replaces the file it finds, and the answer says what that was
        var changes = existing is null ? new JsonArray() : DefinitionChanges.Between(existing, text);

        if (ToGroup(name, caller))
        {
            // the group's: in the state, served here at once and by every other instance when it next looks (plan 20 O04b)
            var was = published!.Text(name);
            WriteResult<string> grouped;
            try
            {
                grouped = await _guard.RunAsync(name, expectedVersion, () => Task.FromResult(published.Text(name) is { } now ? VersionOf(now) : null), () =>
                {
                    published.Publish(name, text, Who(caller));
                    return Task.FromResult(VersionOf(text));
                }, cancellationToken);
            }
            catch (PublishRefusedException groupRefused)
            {
                // the group cannot take this format (24a UC01): a live instance of it does not read it; the message names the instance
                Record(caller, "data-source write", name, "refused");
                return new ManagementResult(409, new JsonObject { ["error"] = groupRefused.Message });
            }
            if (!grouped.Applied)
            {
                Record(caller, "data-source write", name, "stale");
                return new ManagementResult(409, new JsonObject { ["error"] = "The data source changed since it was read; read it again and make the change on that.", ["current_version"] = grouped.CurrentVersion });
            }
            Record(caller, "data-source write", name, was is null ? "created" : "changed");
            if (adopting) Record(caller, "data-source adopt", name, $"adopted by {OwnerScope.Stamp(caller.Token!)}");
            return ManagementResult.Ok(new JsonObject
            {
                ["name"] = name, ["version"] = grouped.Value, ["created"] = was is null, ["group"] = true, ["adopted"] = adopting, ["changes"] = changes,
                ["diagnostics"] = new JsonArray([.. candidate.Body["diagnostics"]!.AsArray().Select(d => d!.DeepClone())]),
            });
        }

        var file = FileOf(name);
        var created = !File.Exists(file);
        // a file made by hand that a publisher replaces goes to the history as it was, whether or not anything had committed it before: nothing is lost
        if (adopting && !created) Committed(caller, $"Data source {name} as it was before {OwnerScope.Stamp(caller.Token!)} adopted it", [file]);
        var result = await _guard.RunAsync(name, expectedVersion, () => Task.FromResult(File.Exists(file) ? VersionOf(File.ReadAllText(file)) : null), () =>
        {
            Directory.CreateDirectory(Folder);
            // written beside and moved over, so a reader (the file watcher, the options monitor) never sees half a file
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(temp, text, new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
            return Task.FromResult(VersionOf(text));
        }, cancellationToken);

        if (!result.Applied)
        {
            Record(caller, "data-source write", name, "stale");
            return new ManagementResult(409, new JsonObject { ["error"] = "The data source changed since it was read; read it again and make the change on that.", ["current_version"] = result.CurrentVersion });
        }
        Committed(caller, adopting ? $"Data source {name} adopted by {OwnerScope.Stamp(caller.Token!)}: the definition replaces the file made by hand" : $"{(created ? "Add" : "Change")} data source {name}", [file]);
        Record(caller, "data-source write", name, created ? "created" : "changed");
        if (adopting) Record(caller, "data-source adopt", name, $"adopted by {OwnerScope.Stamp(caller.Token!)}");
        reload();
        return ManagementResult.Ok(new JsonObject
        {
            ["name"] = name, ["version"] = result.Value, ["created"] = created, ["adopted"] = adopting, ["changes"] = changes,
            ["diagnostics"] = new JsonArray([.. candidate.Body["diagnostics"]!.AsArray().Select(d => d!.DeepClone())]),   // warnings, such as a certificate check turned off
        });
    }

    private sealed record AdoptRequest(string? Client, [property: System.Text.Json.Serialization.JsonPropertyName("adopt")] bool Adopt = false);

    /// <summary>
    /// <c>POST /api/v1/data-sources/{name}/adopt</c> (<c>{"client": "&lt;id of a paired server client&gt;"}</c>), plan 13 W12 and T12: a person with the admin role (a console's signed-in administrator, or the operating-system user on the local channel) makes a data source
    /// that was made by hand, or by another client, one that client manages: its file gets <c>managed_by: &lt;client&gt;</c>, and from then on the owner rules let that client change and remove it. A server client is refused whatever its ceiling, and what the file says
    /// is otherwise not touched. A data source that carries a serving policy (<c>extensions.gateway</c>) is handed over only with <c>"adopt": true</c> in the body (plan 13c D19): the client's next publish replaces the policy with its definition's, so the refusal says so rather than let that happen unasked, and the file goes to the history first. The source is adopted after.
    /// </summary>
    private async Task<ManagementResult> AdoptAsync(string name, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        var decision = caller.Token is null ? null : OwnerScope.AuthorizeAdopt(caller.Token);
        if (decision is { Allowed: false })
        {
            Record(caller, "data-source adopt", name, "refused");
            return ManagementResult.Problem(403, decision.Reason ?? "Adopting a data source is a person's act with the admin role.");
        }
        AdoptRequest? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<AdoptRequest>(body, Json); }
        catch (JsonException) { request = null; }
        if (string.IsNullOrWhiteSpace(request?.Client))
            return ManagementResult.Problem(400, "The body is a JSON object naming the server client that takes the data source over: {\"client\": \"<client id>\"} (server clients lists them).");
        var client = findClient?.Invoke(request.Client);
        if (client is null || client.Kind != ClientKind.Server)
            return ManagementResult.Problem(404, $"There is no paired server client '{request.Client}'; only a Build server (or another server client) can take a data source over.");

        var file = FileOf(name);
        if (!File.Exists(file)) return ManagementResult.Problem(404, $"There is no data source '{name}'.");
        var text = File.ReadAllText(file);
        var current = DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, file).Definition;
        var policyFields = DbDataKit.Connections.ServingPolicy.BlockOf(text)?.Entries.Select(e => e.Key.Value).ToList() ?? [];
        if (policyFields.Count > 0 && request.Adopt != true)
        {
            Record(caller, "data-source adopt", name, "refused");
            return ManagementResult.Problem(409, $"The data source carries a serving policy (extensions.gateway: {Describe(policyFields)}), which the client's next publish replaces with the policy of its definition (allow_writes and allowed_directories of the file go with it: the definition never carries them, so the data source is read-only afterwards). "
                + "Say {\"adopt\": true} to hand it over knowing that (the file is kept in the history first), or let the client adopt it by publishing with \"adopt\": true, whose answer lists every field that changed or was dropped.");
        }
        if (string.Equals(current?.ManagedBy, client.Id, StringComparison.Ordinal))
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["managed_by"] = client.Id, ["adopted"] = false, ["version"] = VersionOf(text) });

        var adopted = ManagedByLine().IsMatch(text) ? ManagedByLine().Replace(text, $"managed_by: {client.Id}", 1) : (text.EndsWith('\n') ? text : text + "\n") + $"managed_by: {client.Id}\n";
        var candidate = validation.ValidateFiles(new Dictionary<string, string> { [$"connections/{name}.yaml"] = adopted });
        if (candidate.Body["valid"]?.GetValue<bool>() != true)
            return new ManagementResult(400, new JsonObject { ["error"] = "The data source with that owner is not valid, so nothing was written.", ["valid"] = false, ["diagnostics"] = candidate.Body["diagnostics"]!.DeepClone() });

        Committed(caller, $"Data source {name} as it was before {client.Name} ({client.Id}) adopted it", [file]);   // whether or not it was committed before, the file is in the history before it changes
        var result = await _guard.RunAsync(name, VersionOf(text), () => Task.FromResult(File.Exists(file) ? VersionOf(File.ReadAllText(file)) : null), () =>
        {
            var temp = file + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(temp, adopted, new UTF8Encoding(false));
            File.Move(temp, file, overwrite: true);
            return Task.FromResult(VersionOf(adopted));
        }, cancellationToken);
        if (!result.Applied)
        {
            Record(caller, "data-source adopt", name, "stale");
            return new ManagementResult(409, new JsonObject { ["error"] = "The data source changed while it was being adopted; try again.", ["current_version"] = result.CurrentVersion });
        }
        Committed(caller, $"Data source {name} adopted by {client.Name} ({client.Id})", [file]);
        Record(caller, "data-source adopt", name, $"adopted by {client.Id}");
        reload();
        return ManagementResult.Ok(new JsonObject
        {
            ["name"] = name, ["managed_by"] = client.Id, ["adopted"] = true, ["previous_owner"] = current?.ManagedBy, ["version"] = result.Value,
            ["replaced_by_next_publish"] = new JsonArray([.. policyFields.Select(f => (JsonNode?)JsonValue.Create(f))]),
        });
    }

    private async Task<ManagementResult> DeleteAsync(string name, string? expectedVersion, ManagementCaller caller, CancellationToken cancellationToken)
    {
        var file = FileOf(name);
        if (!File.Exists(file) && published?.Text(name) is not null)
        {
            var retired = await _guard.RunAsync(name, expectedVersion, () => Task.FromResult(published.Text(name) is { } now ? VersionOf(now) : null), () =>
            {
                published.Retire(name, Who(caller));
                return Task.FromResult(true);
            }, cancellationToken);
            if (!retired.Applied)
            {
                Record(caller, "data-source delete", name, "stale");
                return new ManagementResult(409, new JsonObject { ["error"] = "The data source changed since it was read; read it again.", ["current_version"] = retired.CurrentVersion });
            }
            Record(caller, "data-source delete", name, "removed");
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["removed"] = true, ["group"] = true });
        }
        if (!File.Exists(file)) return ManagementResult.Problem(404, $"There is no data source '{name}'.");
        var result = await _guard.RunAsync(name, expectedVersion, () => Task.FromResult(File.Exists(file) ? VersionOf(File.ReadAllText(file)) : null), () =>
        {
            File.Delete(file);
            return Task.FromResult(true);
        }, cancellationToken);
        if (!result.Applied)
        {
            Record(caller, "data-source delete", name, "stale");
            return new ManagementResult(409, new JsonObject { ["error"] = "The data source changed since it was read; read it again.", ["current_version"] = result.CurrentVersion });
        }
        Committed(caller, $"Remove data source {name}", [file]);
        Record(caller, "data-source delete", name, "removed");
        reload();
        return ManagementResult.Ok(new JsonObject { ["name"] = name, ["removed"] = true });
    }

    /// <summary>
    /// Connects to the data source and runs <c>SELECT 1</c>, as the gateway would for a client with access to everything. A data source that connects with the caller's own credentials cannot be tested without them, and says so.
    /// What comes back is the failure kind and code, never the driver's text.
    /// </summary>
    private async Task<ManagementResult> TestAsync(string name, CancellationToken cancellationToken)
    {
        if (TextOf(name) is null) return ManagementResult.Problem(404, $"There is no data source '{name}'.");
        var started = _clock.GetTimestamp();
        try
        {
            // a data source that connects with the caller's own credentials is refused here by the catalog, for want of credentials, with its usual sentence
            var resolved = catalog().Resolve(new GatewayPrincipal("management", "management", ["*"]), name);
            await using var result = await resolved.Executor.ExecuteQueryAsync(new DbDataGateway.Data.QueryRequest("SELECT 1", 1, 15), cancellationToken);
            await foreach (var _ in result.ReadBatchesAsync(cancellationToken)) break;
            return ManagementResult.Ok(new JsonObject { ["ok"] = true, ["elapsed_ms"] = (long)_clock.GetElapsedTime(started).TotalMilliseconds });
        }
        catch (Grpc.Core.RpcException ex)
        {
            return ManagementResult.Ok(new JsonObject { ["ok"] = false, ["message"] = ex.Status.Detail, ["elapsed_ms"] = (long)_clock.GetElapsedTime(started).TotalMilliseconds });
        }
        catch (DbDataKit.Connections.ConnectionFailureException ex)
        {
            // the fixed sentence for the kind of failure, and the engine's own code; the driver's text is in the server log only
            return ManagementResult.Ok(new JsonObject
            {
                ["ok"] = false, ["message"] = ex.Failure.PublicMessage, ["kind"] = ex.Failure.Kind.ToString(), ["code"] = ex.Failure.Code?.ToString(), ["engine"] = ex.Failure.Engine?.Value,
                ["elapsed_ms"] = (long)_clock.GetElapsedTime(started).TotalMilliseconds,
            });
        }
    }

    private static bool IsAdministrator(ManagementCaller caller) => caller.Holds(ServerRole.Admin);

    /// <summary>The client whose data sources a call may see: its own id for a publishing client, null (all) for an administrator.</summary>
    private static string? OwnedBy(ManagementCaller caller) => IsAdministrator(caller) ? null : OwnerScope.Stamp(caller.Token!);

    /// <summary>The owner rules for a call by a client that is not an administrator; null when the call may go on, else the refusal.</summary>
    private ManagementResult? Scope(ManagementCaller caller, ObjectAction action, string name)
    {
        if (IsAdministrator(caller)) return null;
        var text = TextOf(name);
        if (action != ObjectAction.Create && text is null) return ManagementResult.Problem(404, $"There is no data source '{name}'.");
        ManagedObject? target = action == ObjectAction.Create ? null : new ManagedObject(DbDataKit.Connections.ConnectionDefinitionLoader.Load(text!, name + ".yaml").Definition?.ManagedBy);
        var decision = OwnerScope.Authorize(caller.Token!, action, target);
        return decision.Allowed ? null : ManagementResult.Problem(403, decision.Reason ?? "This client may not do this to that data source.");
    }

    private void Committed(ManagementCaller caller, string message, IReadOnlyCollection<string> files)
    {
        try { git?.CommitChanges(files, message, caller.Author); }
        catch (Exception ex) when (ex is LibGit2Sharp.LibGit2SharpException or IOException) { /* the file is written and applied; the history missed this one */ }
    }

    private void Record(ManagementCaller caller, string command, string target, string outcome) =>
        audit.Append(caller.Audit(command, target, outcome, _clock.GetUtcNow()));

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9_.-]{0,63}$")]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"^managed_by:.*$", RegexOptions.Multiline)]
    private static partial Regex ManagedByLine();
}
