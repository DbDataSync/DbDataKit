using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DbDataKit.Config.Git;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Data;
using Microsoft.Extensions.Configuration;

namespace DbDataProxy.Server.Management;

/// <summary>
/// Data sources through the management API (plan 13, P04): list, read, create or change, delete and test the files of <c>connections/</c>. A write is a whole <c>connection/1</c> file, validated with the rest of the configuration as it would be
/// once written (a file that would make the configuration invalid is refused with the diagnostics and nothing is written), checked against the version the caller read (a stale write is a 409 with the current version), written, committed to the
/// configuration folder's git history as the person who made it, noted in the audit, and applied at once: the options monitor is told, and the next query sees the change without a restart.
/// </summary>
public sealed partial class ProxyDataSources(
    string configDirectory, ProxyManagement validation, Action reload, Func<DataSourceCatalog> catalog, IAuditLog audit, GitCommitService? git, TimeProvider? clock = null, Func<string, PairedClient?>? findClient = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly WriteGuard _guard = new();
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;

    private string Folder => Path.Combine(configDirectory, "connections");

    private string FileOf(string name) => Path.Combine(Folder, name + ".yaml");

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

        // a publishing client reaches only the data sources it made (the owner rules, plan 13 W12); an administrator reaches all
        var action = method switch { "GET" or "POST" => ObjectAction.Change, "DELETE" => ObjectAction.Delete, _ => File.Exists(FileOf(name)) ? ObjectAction.Change : ObjectAction.Create };
        if (Scope(caller, action, name) is { } refused) { Record(caller, $"data-source {method.ToLowerInvariant()}", name, "refused"); return refused; }

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
                });
            }
        return new JsonObject { ["data_sources"] = items };
    }

    private ManagementResult Read(string name)
    {
        var file = FileOf(name);
        if (!File.Exists(file)) return ManagementResult.Problem(404, $"There is no data source '{name}'.");
        var text = File.ReadAllText(file);
        return ManagementResult.Ok(new JsonObject { ["name"] = name, ["text"] = text, ["version"] = VersionOf(text) });
    }

    private sealed record WriteRequest(string? Text, [property: System.Text.Json.Serialization.JsonPropertyName("expected_version")] string? ExpectedVersion);

    private async Task<ManagementResult> WriteAsync(string name, string? body, ManagementCaller caller, CancellationToken cancellationToken)
    {
        WriteRequest? request;
        try { request = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<WriteRequest>(body, Json); }
        catch (JsonException) { request = null; }
        if (request?.Text is not { Length: > 0 } text)
            return ManagementResult.Problem(400, "The body is a JSON object with the file's text and the version it is based on: {\"text\": \"schema: connection/1 ...\", \"expected_version\": \"<hash>\" or null to create}.");

        // The file as it would be, among the others: refused with the reasons if it would make the configuration invalid, before anything is written.
        var candidate = validation.ValidateFiles(new Dictionary<string, string> { [$"connections/{name}.yaml"] = text });
        if (candidate.Body["valid"]?.GetValue<bool>() != true)
            return new ManagementResult(400, new JsonObject { ["error"] = "The data source is not valid, so nothing was written.", ["valid"] = false, ["diagnostics"] = candidate.Body["diagnostics"]!.DeepClone() });

        if (!IsAdministrator(caller))
        {
            // what a publishing client may send: no serving policy (extensions.proxy), and the file says whose it is (stamped here when it does not)
            var sent = DbDataKit.Connections.ConnectionDefinitionLoader.Load(text, $"{name}.yaml").Definition;
            var client = OwnerScope.Stamp(caller.Token!);
            if (sent?.Extensions.Items.ContainsKey("proxy") == true)
            {
                Record(caller, "data-source write", name, "refused");
                return ManagementResult.Problem(403, "The server's own policy (extensions.proxy) is not something a publishing client sets; an administrator does.");
            }
            if (!string.IsNullOrEmpty(sent?.ManagedBy) && sent.ManagedBy != client)
            {
                Record(caller, "data-source write", name, "refused");
                return ManagementResult.Problem(403, "The file names another client in managed_by.");
            }
            if (string.IsNullOrEmpty(sent?.ManagedBy)) text = (text.EndsWith('\n') ? text : text + "\n") + $"managed_by: {client}\n";
        }

        var file = FileOf(name);
        var created = !File.Exists(file);
        var result = await _guard.RunAsync(name, request.ExpectedVersion, () => Task.FromResult(File.Exists(file) ? VersionOf(File.ReadAllText(file)) : null), () =>
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
        Committed(caller, $"{(created ? "Add" : "Change")} data source {name}", [file]);
        Record(caller, "data-source write", name, created ? "created" : "changed");
        reload();
        return ManagementResult.Ok(new JsonObject
        {
            ["name"] = name, ["version"] = result.Value, ["created"] = created,
            ["diagnostics"] = new JsonArray([.. candidate.Body["diagnostics"]!.AsArray().Select(d => d!.DeepClone())]),   // warnings, such as a certificate check turned off
        });
    }

    private sealed record AdoptRequest(string? Client);

    /// <summary>
    /// <c>POST /api/v1/data-sources/{name}/adopt</c> (<c>{"client": "&lt;id of a paired server client&gt;"}</c>), plan 13 W12 and T12: a person with the admin role (a console's signed-in administrator, or the operating-system user on the local channel) makes a data source
    /// that was made by hand, or by another client, one that client manages: its file gets <c>managed_by: &lt;client&gt;</c>, and from then on the owner rules let that client change and remove it. A server client is refused whatever its ceiling, and what the file says
    /// is otherwise not touched. A data source that carries a serving policy (<c>extensions.proxy</c>) is not adopted: a client writes the whole file, which would silently drop the policy, so the policy goes first (an administrator moves it out) and the source is adopted after.
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
        if (current?.Extensions.Items.ContainsKey("proxy") == true)
        {
            Record(caller, "data-source adopt", name, "refused");
            return ManagementResult.Problem(409, "The data source carries a serving policy (extensions.proxy), which a publishing client's write would drop. Move the policy out first, then adopt it.");
        }
        if (string.Equals(current?.ManagedBy, client.Id, StringComparison.Ordinal))
            return ManagementResult.Ok(new JsonObject { ["name"] = name, ["managed_by"] = client.Id, ["adopted"] = false, ["version"] = VersionOf(text) });

        var adopted = ManagedByLine().IsMatch(text) ? ManagedByLine().Replace(text, $"managed_by: {client.Id}", 1) : (text.EndsWith('\n') ? text : text + "\n") + $"managed_by: {client.Id}\n";
        var candidate = validation.ValidateFiles(new Dictionary<string, string> { [$"connections/{name}.yaml"] = adopted });
        if (candidate.Body["valid"]?.GetValue<bool>() != true)
            return new ManagementResult(400, new JsonObject { ["error"] = "The data source with that owner is not valid, so nothing was written.", ["valid"] = false, ["diagnostics"] = candidate.Body["diagnostics"]!.DeepClone() });

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
        return ManagementResult.Ok(new JsonObject { ["name"] = name, ["managed_by"] = client.Id, ["adopted"] = true, ["previous_owner"] = current?.ManagedBy, ["version"] = result.Value });
    }

    private async Task<ManagementResult> DeleteAsync(string name, string? expectedVersion, ManagementCaller caller, CancellationToken cancellationToken)
    {
        var file = FileOf(name);
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
    /// Connects to the data source and runs <c>SELECT 1</c>, as the proxy would for a client with access to everything. A data source that connects with the caller's own credentials cannot be tested without them, and says so.
    /// What comes back is the failure kind and code, never the driver's text.
    /// </summary>
    private async Task<ManagementResult> TestAsync(string name, CancellationToken cancellationToken)
    {
        if (!File.Exists(FileOf(name))) return ManagementResult.Problem(404, $"There is no data source '{name}'.");
        var started = _clock.GetTimestamp();
        try
        {
            // a data source that connects with the caller's own credentials is refused here by the catalog, for want of credentials, with its usual sentence
            var resolved = catalog().Resolve(new ProxyPrincipal("management", "management", ["*"]), name);
            await using var result = await resolved.Executor.ExecuteQueryAsync(new DbDataProxy.Data.QueryRequest("SELECT 1", 1, 15), cancellationToken);
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
        var file = FileOf(name);
        if (action != ObjectAction.Create && !File.Exists(file)) return ManagementResult.Problem(404, $"There is no data source '{name}'.");
        ManagedObject? target = action == ObjectAction.Create ? null : new ManagedObject(DbDataKit.Connections.ConnectionDefinitionLoader.Load(File.ReadAllText(file), file).Definition?.ManagedBy);
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
