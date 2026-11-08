using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Apps;
using DbDataKit.State;
using DbDataKit.Web.Trust;

namespace DbDataProxy.Server.Management;

/// <summary>
/// Apps through the management API (plan 26, AP26): what <c>apps publish --to</c>, <c>instance-save --to</c>, <c>rollback --to</c> and <c>approve --to</c> call, so a tester does not need to be on the machine that holds the Proxy's data directory.
/// The same stores and rules as the local verbs: the manifest is read strictly, a trusted app is published only with an approval standing for its exact bytes, and an approver is a user of the identity home who holds the Approver role and is not the author.
/// <list type="bullet">
/// <item><c>GET /api/v1/apps</c>: the published apps with the version each serves (publisher or admin).</item>
/// <item><c>PUT /api/v1/apps/{name}</c> (<c>{"manifest": "&lt;app.yaml&gt;", "files": {"path": "&lt;base64&gt;"}, "note": "..."}</c>): publish a version (publisher or admin).</item>
/// <item><c>POST /api/v1/apps/{name}/rollback</c> (<c>{"to": n}</c>, or empty for the version before): move the version an app serves (publisher or admin).</item>
/// <item><c>PUT /api/v1/app-instances/{name}</c> (<c>{"app", "pin", "title", "settings", "bindings"}</c>): make or change an instance (publisher or admin).</item>
/// <item><c>POST /api/v1/app-approvals</c> (<c>{"manifest", "files"}</c> or <c>{"app", "hash"}</c> or <c>{"app", "version"}</c>, and <c>"by"</c>, <c>"note"</c>, <c>"egress"</c>): approve exact bytes of a trusted app (the approver role, which a publisher pairing never has).</item>
/// </list>
/// The author recorded is the person the token names, or the paired client when none is: never the operating-system user of the machine the command ran on.
/// </summary>
public sealed class ProxyApps(DefinitionStore definitions, Func<IAppApprovers> approvers, IAuditLog audit, TimeProvider? clock = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly SemaphoreSlim _writing = new(1, 1);

    private sealed record PublishRequest(string? Manifest, Dictionary<string, string>? Files, string? Note);

    private sealed record InstanceRequest(string? App, int? Pin, string? Title, Dictionary<string, string>? Settings, Dictionary<string, string>? Bindings);

    private sealed record RollbackRequest(int? To);

    private sealed record ApproveRequest(string? Manifest, Dictionary<string, string>? Files, string? App, string? Hash, int? Version, string? By, string? Note, string? Egress);

    /// <summary>The refusal for a caller without the role the call needs; it says which pairing makes it.</summary>
    public static ManagementResult NeedsPublisher() =>
        ManagementResult.Problem(403, "This call needs the publisher role (or admin). Pair this client with a code made by 'dbdataproxy server pair --max-role publisher' on the proxy.");

    public static ManagementResult NeedsApprover() =>
        ManagementResult.Problem(403, "Approving an app needs the approver role, which this client's pairing does not allow. Pair again with a code made by 'dbdataproxy server pair --max-role approver' on the proxy, and name a user of the identity home who holds the Approver role with --by.");

    public async Task<ManagementResult> HandleAsync(string method, string kind, string[] rest, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        if (kind == "app-approvals")
        {
            if (!caller.Holds(ServerRole.Approver)) return NeedsApprover();
            return rest.Length == 0 && method == "POST" ? await WriteAsync(() => Approve(body, caller)) : ManagementResult.Problem(404, "There is no such call.");
        }
        if (!caller.Holds(ServerRole.Admin) && !caller.Holds(ServerRole.Publisher)) return NeedsPublisher();
        switch (kind, method, rest.Length)
        {
            case ("apps", "GET", 0): return List();
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

    private ManagementResult List()
    {
        var store = new AppInstanceStore(definitions);
        var apps = new JsonArray();
        foreach (var app in store.Apps())
            apps.Add(new JsonObject { ["name"] = app.Name, ["version"] = app.Version, ["versions"] = new JsonArray([.. store.AppVersions(app.Name).Select(v => (JsonNode)v.Version)]) });
        return ManagementResult.Ok(new JsonObject { ["apps"] = apps });
    }

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
        if (request?.By is not { Length: > 0 } by)
            return ManagementResult.Problem(400, "The body names who approves: {\"by\": \"<a user of the identity home who holds the Approver role>\", and one of \"manifest\" and \"files\", \"app\" and \"hash\", or \"app\" and \"version\"}.");
        var target = request.App ?? "(package)";
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
