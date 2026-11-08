using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.Config.Yaml;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataGateway.Server.Management;

/// <summary>
/// Apps through the management API (plan 26, AP26): what <c>apps publish --to</c>, <c>instance-save --to</c>, <c>rollback --to</c> and <c>approve --to</c> call, so a tester does not need to be on the machine that holds the Gateway's data directory.
/// The same stores and rules as the local verbs: the manifest is read strictly, a trusted app is published only with an approval standing for its exact bytes, and an approver is a user of the identity home who holds the Approver role and is not the author.
/// <list type="bullet">
/// <item><c>GET /api/v1/apps</c>: the published apps with the version each serves and its title, trust and publisher (viewer or publisher, so also admin).</item>
/// <item><c>GET /api/v1/apps/{name}</c>: one app: every version (who published it and when, whether it serves, the approval standing for its exact bytes) and what its manifest declares, the "about" data of AP18 (viewer or publisher).</item>
/// <item><c>GET /api/v1/app-instances</c>: the instances with the app version each runs (pinned, or following the channel), their settings and bindings, whether it runs as its manifest asks, its address, the portals that mount it and the navigation sets that list it (viewer or publisher).</item>
/// <item><c>GET /api/v1/app-navigation</c>: the navigation sets (viewer or publisher). <c>GET /api/v1/app-approvals</c>: every approval with its hash, approver, time, what it was for and whether it is revoked (viewer or publisher).</item>
/// <item><c>DELETE /api/v1/apps/{name}</c> and <c>DELETE /api/v1/app-instances/{name}</c>: stop serving an app or an instance, when the store's rules allow (a refusal is a 409 with the store's message; publisher or admin).</item>
/// <item><c>POST /api/v1/app-approvals/{id}/revoke</c> (<c>{"by", "note"}</c>): end an approval; like approving it needs the approver role, and <c>by</c> is a user of the identity home who holds the Approver role.</item>
/// <item><c>PUT /api/v1/apps/{name}</c> (<c>{"manifest": "&lt;app.yaml&gt;", "files": {"path": "&lt;base64&gt;"}, "note": "..."}</c>): publish a version (publisher or admin).</item>
/// <item><c>POST /api/v1/apps/check</c> (<c>{"manifest", "files"}</c>, the body of a publish): what publishing these files would do, without doing it: the manifest's own diagnostics (and those of the portal's home views, the view specs and the files it names), the version it would be, its trust, the content hash, whether an approval stands for these exact files and what the manifest declares (publisher or admin; nothing is stored or audited).</item>
/// <item><c>POST /api/v1/apps/{name}/rollback</c> (<c>{"to": n}</c>, or empty for the version before): move the version an app serves (publisher or admin).</item>
/// <item><c>PUT /api/v1/app-instances/{name}</c> (<c>{"app", "pin", "title", "settings", "bindings"}</c>): make or change an instance (publisher or admin).</item>
/// <item><c>POST /api/v1/app-approvals</c> (<c>{"manifest", "files"}</c> or <c>{"app", "hash"}</c> or <c>{"app", "version"}</c>, and <c>"by"</c>, <c>"note"</c>, <c>"egress"</c>): approve exact bytes of a trusted app (the approver role, which a publisher pairing never has).</item>
/// </list>
/// Every answer to a read says what the caller may do (<c>caller</c>: <c>can_publish</c>, <c>can_approve</c>), so a screen can say why a button is off; the roles are enforced here on every call, never by the screen.
/// The author recorded is the person the token names, or the paired client when none is: never the operating-system user of the machine the command ran on.
/// </summary>
public sealed class GatewayApps(DefinitionStore definitions, Func<IAppApprovers> approvers, IAuditLog audit, TimeProvider? clock = null, Func<Uri?>? appsAddress = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _writing = new(1, 1);

    private sealed record PublishRequest(string? Manifest, Dictionary<string, string>? Files, string? Note);

    private sealed record InstanceRequest(string? App, int? Pin, string? Title, Dictionary<string, string>? Settings, Dictionary<string, string>? Bindings);

    private sealed record RollbackRequest(int? To);

    private sealed record RevokeRequest(string? By, string? Note);

    private sealed record ApproveRequest(string? Manifest, Dictionary<string, string>? Files, string? App, string? Hash, int? Version, string? By, string? Note, string? Egress);

    /// <summary>The refusal for a caller without the role the call needs; it says which pairing makes it.</summary>
    public static ManagementResult NeedsPublisher() =>
        ManagementResult.Problem(403, "This call needs the publisher role (or admin). Pair this client with a code made by 'dbdatagateway server pair --max-role publisher' on the gateway.");

    public static ManagementResult NeedsApprover() =>
        ManagementResult.Problem(403, "Approving an app needs the approver role, which this client's pairing does not allow. Pair again with a code made by 'dbdatagateway server pair --max-role approver' on the gateway, and name a user of the identity home who holds the Approver role with --by.");

    public static ManagementResult NeedsViewer() =>
        ManagementResult.Problem(403, "This call needs the viewer role (or publisher).");

    private static bool CanRead(ManagementCaller caller) => caller.Holds(ServerRole.Viewer) || caller.Holds(ServerRole.Publisher);

    private static bool CanPublish(ManagementCaller caller) => caller.Holds(ServerRole.Admin) || caller.Holds(ServerRole.Publisher);

    public async Task<ManagementResult> HandleAsync(string method, string kind, string[] rest, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (method == "GET" && rest.Length == 0 && kind is "app-approvals" or "app-instances" or "app-navigation" or "apps")
        {
            if (!CanRead(caller)) return NeedsViewer();
            return kind switch { "app-approvals" => Approvals(caller), "app-instances" => Instances(caller), "app-navigation" => Navigation(caller), _ => List(caller) };
        }
        if (method == "GET" && rest.Length == 1 && kind == "apps") return CanRead(caller) ? Detail(Uri.UnescapeDataString(rest[0]), caller) : NeedsViewer();
        if (kind == "app-approvals")
        {
            if (!caller.Holds(ServerRole.Approver)) return NeedsApprover();
            if (rest.Length == 2 && method == "POST" && rest[1] == "revoke") return await WriteAsync(() => Revoke(Uri.UnescapeDataString(rest[0]), body, caller));
            return rest.Length == 0 && method == "POST" ? await WriteAsync(() => Approve(body, caller)) : ManagementResult.Problem(404, "There is no such call.");
        }
        if (!CanPublish(caller)) return NeedsPublisher();
        switch (kind, method, rest.Length)
        {
            case ("apps", "DELETE", 1): return await WriteAsync(() => RemoveApp(Uri.UnescapeDataString(rest[0]), caller));
            case ("app-instances", "DELETE", 1): return await WriteAsync(() => RemoveInstance(Uri.UnescapeDataString(rest[0]), caller));
            case ("apps", "POST", 1) when rest[0] == "check": return Check(body, caller);
            case ("apps", "PUT", 1): return await WriteAsync(() => Publish(Uri.UnescapeDataString(rest[0]), body, caller));
            case ("apps", "POST", 2) when rest[1] == "rollback": return await WriteAsync(() => Rollback(Uri.UnescapeDataString(rest[0]), body, caller));
            case ("app-instances", "PUT", 1): return await WriteAsync(() => SaveInstance(Uri.UnescapeDataString(rest[0]), body, caller));
            default: return ManagementResult.Problem(404, "There is no such call.");
        }
    }

    private async Task<ManagementResult> WriteAsync(Func<ManagementResult> write)
    {
        await _writing.WaitAsync();
        try { return write(); }
        finally { _writing.Release(); }
    }

    private static T? Read<T>(string? body) where T : class
    {
        try { return string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<T>(body, Json); }
        catch (JsonException) { return null; }
    }

    private static JsonObject Capabilities(ManagementCaller caller) => new() { ["can_publish"] = CanPublish(caller), ["can_approve"] = caller.Holds(ServerRole.Approver), ["you"] = CallerName(caller) };

    private static string CallerName(ManagementCaller caller) => caller.Token is { } t ? t.SubjectName ?? t.SubjectId : caller.Author.Name;

    private static AppManifest? ManifestOf(DefinitionVersion version) => AppManifestLoader.Load(AppPackage.Parse(version.Content).ManifestYaml).Manifest;

    private ManagementResult List(ManagementCaller caller)
    {
        var store = new AppInstanceStore(definitions);
        var instances = store.List();
        var apps = new JsonArray();
        foreach (var app in store.Apps())
        {
            var manifest = ManifestOf(app);
            apps.Add(new JsonObject
            {
                ["name"] = app.Name, ["version"] = app.Version, ["versions"] = new JsonArray([.. store.AppVersions(app.Name).Select(v => (JsonNode)v.Version)]),
                ["title"] = manifest?.Title, ["manifest_version"] = manifest?.Version, ["description"] = manifest?.Description, ["kind"] = manifest?.Kind.ToString().ToLowerInvariant(), ["trust"] = manifest?.Trust.ToString().ToLowerInvariant(),
                ["published_by"] = app.PublishedBy, ["published_at"] = app.PublishedAt, ["instances"] = new JsonArray([.. instances.Where(i => i.App == app.Name).Select(i => (JsonNode)i.Name)]),
            });
        }
        return ManagementResult.Ok(new JsonObject { ["apps"] = apps, ["caller"] = Capabilities(caller) });
    }

    /// <summary>One app: its versions, and the data access its serving version declares.</summary>
    private ManagementResult Detail(string name, ManagementCaller caller)
    {
        var store = new AppInstanceStore(definitions);
        var versions = store.AppVersions(name);
        if (versions.Count == 0) return ManagementResult.Problem(404, $"The app '{name}' has no published version.");
        var serving = store.Apps().FirstOrDefault(a => a.Name == name)?.Version;
        var approvals = new AppApprovalStore(definitions).ForApp(name);
        var rows = new JsonArray();
        foreach (var version in versions.OrderByDescending(v => v.Version))
        {
            var package = AppPackage.Parse(version.Content);
            var manifest = AppManifestLoader.Load(package.ManifestYaml).Manifest;
            var (bundle, manifestHash) = AppApprovalStore.Hashes(package);
            var standing = manifest is null ? null : AppApprovals.Find(approvals, manifest, bundle, manifestHash);
            rows.Add(new JsonObject
            {
                ["version"] = version.Version, ["manifest_version"] = manifest?.Version, ["trust"] = manifest?.Trust.ToString().ToLowerInvariant(), ["published_by"] = version.PublishedBy, ["published_at"] = version.PublishedAt, ["note"] = version.Note,
                ["serving"] = version.Version == serving, ["hash"] = AppApprovals.ContentHash(bundle, manifestHash), ["approval_id"] = standing?.Id, ["files"] = package.Files.Count,
                ["about"] = manifest is null ? null : About(manifest, version.PublishedBy, version.PublishedAt, standing),
            });
        }
        return ManagementResult.Ok(new JsonObject { ["name"] = name, ["serving"] = serving, ["versions"] = rows, ["caller"] = Capabilities(caller) });
    }

    /// <summary>What an app's manifest declares it can reach, as the host's about page lists it (AP18): the models and what it does with each entity, the outside addresses, the storage, who published it and who approved these bytes.</summary>
    private static JsonObject About(AppManifest manifest, string publishedBy, DateTimeOffset publishedAt, AppApproval? standing)
    {
        var access = new JsonArray();
        foreach (var use in manifest.Models)
            foreach (var (entity, operations) in use.Entities.OrderBy(e => e.Key, StringComparer.Ordinal))
                access.Add(new JsonObject { ["model"] = use.Model, ["entity"] = entity, ["operations"] = operations.HasFlag(DataOperations.Write) ? "read and write" : operations.HasFlag(DataOperations.Read) ? "read" : "nothing" });
        var egress = new JsonArray();
        foreach (var (key, _, origins) in (manifest.Egress ?? AppEgress.None).Directives().Where(d => d.Origins.Count > 0))
            egress.Add(new JsonObject { ["directive"] = key, ["origins"] = new JsonArray([.. origins.Select(o => (JsonNode)o)]) });
        var storage = manifest.StorageUse;
        return new JsonObject
        {
            ["title"] = manifest.Title, ["description"] = manifest.Description, ["kind"] = manifest.Kind.ToString().ToLowerInvariant(), ["trust"] = manifest.Trust.ToString().ToLowerInvariant(),
            ["roles"] = new JsonArray([.. manifest.Roles.Select(r => (JsonNode)r)]), ["models"] = access, ["semantic_models"] = new JsonArray([.. manifest.SemanticModels.Select(m => (JsonNode)m)]), ["egress"] = egress,
            ["storage"] = storage.User || storage.Instance ? new JsonObject { ["user"] = storage.User, ["instance"] = storage.Instance, ["quota_mb"] = storage.QuotaMb } : null,
            ["published_by"] = publishedBy, ["published_at"] = publishedAt,
            ["approval"] = standing is null ? null : new JsonObject { ["id"] = standing.Id, ["approved_by"] = standing.ApprovedBy, ["approved_at"] = standing.ApprovedAt, ["note"] = standing.Note },
        };
    }

    private ManagementResult Instances(ManagementCaller caller)
    {
        var store = new AppInstanceStore(definitions);
        var approvals = new AppApprovalStore(definitions);
        var sets = store.NavigationSets();
        var current = definitions.CurrentOfKind(AppInstanceStore.InstanceKind).ToDictionary(v => v.Name);
        var address = appsAddress?.Invoke();
        var resolved = new Dictionary<string, ResolvedInstance?>();
        foreach (var instance in store.List()) resolved[instance.Name] = store.Resolve(instance.Name);
        var rows = new JsonArray();
        foreach (var instance in store.List())
        {
            var run = resolved[instance.Name];
            var row = new JsonObject
            {
                ["name"] = instance.Name, ["app"] = instance.App, ["title"] = instance.Title, ["pin"] = instance.Pin, ["follows"] = instance.Pin is null,
                ["instance_version"] = current[instance.Name].Version, ["published_by"] = current[instance.Name].PublishedBy, ["published_at"] = current[instance.Name].PublishedAt,
                ["path"] = $"/apps/{instance.Name}/", ["address"] = address is null ? null : new Uri(address, $"/apps/{instance.Name}/").AbsoluteUri,
                ["bindings"] = new JsonObject(instance.Bindings.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => KeyValuePair.Create(b.Key, (JsonNode?)b.Value))),
                ["settings"] = new JsonObject(instance.Settings.OrderBy(b => b.Key, StringComparer.Ordinal).Select(b => KeyValuePair.Create(b.Key, (JsonNode?)b.Value))),
                ["navigation"] = new JsonArray([.. sets.Where(s => s.Entries.Any(e => e.Instance == instance.Name)).Select(s => (JsonNode)s.Name)]),
                ["mounted_by"] = new JsonArray([.. resolved.Values.Where(r => r is not null && r.Instance.Name != instance.Name && r.Manifest.Portal?.Mounts.Any(m => m.App == instance.Name) == true).Select(r => (JsonNode)r!.Instance.Name)]),
            };
            if (run is null) row["problem"] = $"The app '{instance.App}' {(instance.Pin is { } p ? $"has no version {p}" : "is not published")}, so this instance serves nothing.";
            else
            {
                var gate = approvals.Gate(run);
                row["app_version"] = run.AppVersion.Version; row["manifest_version"] = run.Manifest.Version; row["kind"] = run.Manifest.Kind.ToString().ToLowerInvariant(); row["trust"] = run.Manifest.Trust.ToString().ToLowerInvariant();
                row["runs_as_asked"] = gate.Allowed; row["approval_id"] = gate.Approval?.Id;
                row["serving_version"] = definitions.Current(AppInstanceStore.AppKind, instance.App, DefinitionStore.Production)?.Version;
                row["mounts"] = new JsonArray([.. (run.Manifest.Portal?.Mounts ?? []).Select(m => (JsonNode)new JsonObject { ["instance"] = m.App, ["path"] = m.Path })]);
            }
            rows.Add(row);
        }
        return ManagementResult.Ok(new JsonObject { ["instances"] = rows, ["apps_address"] = address?.AbsoluteUri, ["caller"] = Capabilities(caller) });
    }

    private ManagementResult Navigation(ManagementCaller caller) =>
        ManagementResult.Ok(new JsonObject
        {
            ["navigation"] = new JsonArray([.. new AppInstanceStore(definitions).NavigationSets().Select(s => (JsonNode)new JsonObject
            {
                ["name"] = s.Name, ["title"] = s.Title,
                ["entries"] = new JsonArray([.. s.Entries.Select(e => (JsonNode)new JsonObject { ["instance"] = e.Instance, ["label"] = e.Label, ["icon"] = e.Icon })]),
            })]),
            ["caller"] = Capabilities(caller),
        });

    private ManagementResult Approvals(ManagementCaller caller) =>
        ManagementResult.Ok(new JsonObject
        {
            ["approvals"] = new JsonArray([.. new AppApprovalStore(definitions).All().Select(a => (JsonNode)new JsonObject
            {
                ["id"] = a.Id, ["app"] = a.App, ["version"] = a.Version, ["hash"] = a.ContentHash, ["approved_by"] = a.ApprovedBy, ["approved_at"] = a.ApprovedAt, ["note"] = a.Note,
                ["published_version"] = a.PublishedVersion, ["revoked"] = a.RevokedAt is not null, ["revoked_at"] = a.RevokedAt,
                ["operations"] = new JsonArray([.. a.Operations.SelectMany(o => o.Entities.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => (JsonNode)new JsonObject { ["model"] = o.Model, ["entity"] = e.Key, ["operations"] = e.Value.HasFlag(DataOperations.Write) ? "read and write" : "read" }))]),
                ["egress"] = new JsonArray([.. a.Egress.Directives().Where(d => d.Origins.Count > 0).Select(d => (JsonNode)new JsonObject { ["directive"] = d.Key, ["origins"] = new JsonArray([.. d.Origins.Select(o => (JsonNode)o)]) })]),
            })]),
            ["caller"] = Capabilities(caller),
        });

    private ManagementResult RemoveApp(string name, ManagementCaller caller)
    {
        try
        {
            if (!new AppInstanceStore(definitions).RetireApp(name, Who(caller), DefinitionStore.Production)) return Refused(caller, "app remove", name, 404, $"The app '{name}' is not served.");
            Record(caller, "app remove", name, "retired");
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["removed"] = true });
        }
        catch (AppInstanceException ex) { return Refused(caller, "app remove", name, 409, ex.Message); }
    }

    private ManagementResult RemoveInstance(string name, ManagementCaller caller)
    {
        try
        {
            if (!new AppInstanceStore(definitions).RetireInstance(name, Who(caller), DefinitionStore.Production)) return Refused(caller, "app instance-remove", name, 404, $"The instance '{name}' is not served.");
            Record(caller, "app instance-remove", name, "retired");
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["removed"] = true });
        }
        catch (AppInstanceException ex) { return Refused(caller, "app instance-remove", name, 409, ex.Message); }
    }

    private ManagementResult Revoke(string id, string? body, ManagementCaller caller)
    {
        var request = Read<RevokeRequest>(body);
        if (ApproverName(request?.By, caller) is not { Length: > 0 } by)
            return ManagementResult.Problem(400, "The body names who revokes: {\"by\": \"<a user of the identity home who holds the Approver role>\"}.");
        try
        {
            var revoked = new AppApprovalStore(definitions, approvers: approvers()).Revoke(id, by);
            Record(caller, "app approval-revoke", revoked.App, $"{revoked.Id} by {by}");
            return ManagementResult.Ok(new JsonObject { ["id"] = revoked.Id, ["app"] = revoked.App, ["version"] = revoked.Version, ["revoked"] = true, ["revoked_at"] = revoked.RevokedAt, ["revoked_by"] = by });
        }
        catch (AppInstanceException ex) { return Refused(caller, "app approval-revoke", id, 400, ex.Message); }
    }

    /// <summary>The approver a call names; a person signed in to a console who names none is the approver (the identity home still has to know them and their role).</summary>
    private static string? ApproverName(string? by, ManagementCaller caller) =>
        !string.IsNullOrWhiteSpace(by) ? by : caller.Token is { IsConsole: false, Client.Kind: ClientKind.Console } t ? t.SubjectName ?? t.SubjectId : null;

    private ManagementResult Publish(string name, string? body, ManagementCaller caller)
    {
        var request = Read<PublishRequest>(body);
        if (request?.Manifest is not { Length: > 0 } manifest)
            return ManagementResult.Problem(400, "The body is a JSON object: {\"manifest\": \"<app.yaml>\", \"files\": {\"<path>\": \"<base64>\"}, \"note\": \"<text>\"}.");
        var by = Who(caller);
        try
        {
            var package = Package(manifest, request.Files);
            var load = AppManifestLoader.Load(package.ManifestYaml);
            if (load.Ok && load.Manifest!.Name != name) return Refused(caller, "app publish", name, 400, $"The manifest is named '{load.Manifest.Name}' and the call names '{name}'; they are the same name.");
            var version = new AppInstanceStore(definitions).PublishApp(package, by, note: request.Note, approvals: new AppApprovalStore(definitions));
            Record(caller, "app publish", name, $"version {version.Version}");
            return ManagementResult.Ok(new JsonObject { ["name"] = version.Name, ["version"] = version.Version, ["files"] = package.Files.Count, ["published_by"] = by });
        }
        catch (Exception ex) when (ex is AppInstanceException or AppPackageException)
        {
            return Refused(caller, "app publish", name, 400, ex.Message);
        }
    }

    /// <summary>What a publish of these files would meet, said without publishing: the diagnostics the screen shows before it sends the files, and what the approver needs to choose (the origins the manifest asks for).</summary>
    private ManagementResult Check(string? body, ManagementCaller caller)
    {
        var request = Read<PublishRequest>(body);
        if (request?.Manifest is not { Length: > 0 } manifestText)
            return ManagementResult.Problem(400, "The body is a JSON object: {\"manifest\": \"<app.yaml>\", \"files\": {\"<path>\": \"<base64>\"}}.");
        AppPackage package;
        try { package = Package(manifestText, request.Files); }
        catch (AppPackageException ex) { return ManagementResult.Problem(400, ex.Message); }

        var load = AppManifestLoader.Load(package.ManifestYaml);
        var diagnostics = new List<ConfigDiagnostic>(load.Diagnostics);
        var result = new JsonObject { ["files"] = package.Files.Count, ["ok"] = load.Ok };
        if (load.Manifest is { } manifest)
        {
            string? Text(string path) => package.Files.TryGetValue(path, out var bytes) ? Encoding.UTF8.GetString(bytes) : null;
            diagnostics.AddRange(AppChecks.FilesWhere(manifest, path => package.Files.ContainsKey(path)));
            diagnostics.AddRange(AppChecks.HomeViews(manifest, Text));
            diagnostics.AddRange(AppChecks.ViewSpecs(manifest, Text));
            var store = new AppInstanceStore(definitions);
            var (bundle, manifestHash) = AppApprovalStore.Hashes(package);
            var standing = AppApprovals.Find(new AppApprovalStore(definitions).ForApp(manifest.Name), manifest, bundle, manifestHash);
            result["name"] = manifest.Name;
            result["manifest_version"] = manifest.Version;
            result["trust"] = manifest.Trust.ToString().ToLowerInvariant();
            result["hash"] = AppApprovals.ContentHash(bundle, manifestHash);
            result["approval_id"] = standing?.Id;
            result["next_version"] = store.AppVersions(manifest.Name).Select(v => v.Version).DefaultIfEmpty(0).Max() + 1;
            result["about"] = About(manifest, CallerName(caller), _clock.GetUtcNow(), standing);
            result["ok"] = !diagnostics.Any(d => d.Severity == ConfigSeverity.Error);
        }
        result["diagnostics"] = new JsonArray([.. diagnostics.Select(d => (JsonNode)new JsonObject
        {
            ["code"] = d.Code, ["severity"] = d.Severity.ToString().ToLowerInvariant(), ["file"] = d.Path, ["line"] = d.Line, ["column"] = d.Column, ["message"] = d.Message, ["fix"] = d.Fix,
        })]);
        return ManagementResult.Ok(result);
    }

    private ManagementResult Rollback(string name, string? body, ManagementCaller caller)
    {
        var request = Read<RollbackRequest>(body);
        try
        {
            var moved = new AppInstanceStore(definitions).MoveApp(name, Who(caller), request?.To);
            Record(caller, "app rollback", name, $"version {moved.Version}");
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["version"] = moved.Version });
        }
        catch (AppInstanceException ex)
        {
            return Refused(caller, "app rollback", name, 400, ex.Message);
        }
    }

    private ManagementResult SaveInstance(string name, string? body, ManagementCaller caller)
    {
        var request = Read<InstanceRequest>(body);
        if (request?.App is not { Length: > 0 } app) return ManagementResult.Problem(400, "The body is a JSON object: {\"app\": \"<app>\", \"pin\": <version>, \"title\": \"<text>\", \"settings\": {}, \"bindings\": {}}.");
        try
        {
            var saved = new AppInstanceStore(definitions).Save(new AppInstance(name, app, request.Pin, request.Title, request.Settings, request.Bindings), Who(caller));
            Record(caller, "app instance-save", name, $"version {saved.Version}");
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["app"] = app, ["version"] = saved.Version });
        }
        catch (AppInstanceException ex)
        {
            return Refused(caller, "app instance-save", name, 400, ex.Message);
        }
    }

    private ManagementResult Approve(string? body, ManagementCaller caller)
    {
        var request = Read<ApproveRequest>(body);
        if (ApproverName(request?.By, caller) is not { Length: > 0 } by)
            return ManagementResult.Problem(400, "The body names who approves: {\"by\": \"<a user of the identity home who holds the Approver role>\", and one of \"manifest\" and \"files\", \"app\" and \"hash\", or \"app\" and \"version\"}.");
        // a package approval is audited under the app its manifest names, as a version approval is under the app it names
        var target = request!.App ?? (request.Manifest is { Length: > 0 } text ? AppManifestLoader.Load(text).Manifest?.Name : null) ?? "(package)";
        try
        {
            var approvalStore = new AppApprovalStore(definitions, approvers: approvers());
            var egress = AppApprovalStore.ParseEgress(request.Egress);
            var author = Who(caller);
            AppApproval approval;
            if (request.Manifest is { Length: > 0 } manifest) approval = approvalStore.Approve(Package(manifest, request.Files), author, by, request.Note, egress: egress);
            else if (request.Hash is { Length: > 0 } hash && request.App is { Length: > 0 } app) approval = approvalStore.Approve(app, hash, author, by, request.Note, egress);
            else if (request.Version is { } version && request.App is { Length: > 0 } published) approval = approvalStore.Approve(published, version, by, request.Note, egress: egress);
            else return ManagementResult.Problem(400, "Name what to approve: the package (\"manifest\" and \"files\"), \"app\" with \"hash\", or \"app\" with \"version\".");
            Record(caller, "app approve", approval.App, $"{approval.Version} by {approval.ApprovedBy}");
            return ManagementResult.Ok(new JsonObject { ["id"] = approval.Id, ["app"] = approval.App, ["version"] = approval.Version, ["hash"] = approval.ContentHash, ["approved_by"] = approval.ApprovedBy, ["author"] = author });
        }
        catch (Exception ex) when (ex is AppInstanceException or AppPackageException)
        {
            return Refused(caller, "app approve", target, 400, ex.Message);
        }
    }

    private static AppPackage Package(string manifest, Dictionary<string, string>? files)
    {
        var decoded = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, content) in files ?? [])
        {
            try { decoded[path] = Convert.FromBase64String(content); }
            catch (FormatException) { throw new AppPackageException($"The file '{path}' is not base64."); }
        }
        return new AppPackage(manifest, decoded);
    }

    private string Who(ManagementCaller caller) { var entry = caller.Audit("", null, "", _clock.GetUtcNow()); return entry.UserName ?? entry.UserId; }

    private ManagementResult Refused(ManagementCaller caller, string command, string target, int status, string error)
    {
        Record(caller, command, target, "refused");
        return ManagementResult.Problem(status, error);
    }

    private void Record(ManagementCaller caller, string command, string target, string outcome) => audit.Append(caller.Audit(command, target, outcome, _clock.GetUtcNow()));
}
