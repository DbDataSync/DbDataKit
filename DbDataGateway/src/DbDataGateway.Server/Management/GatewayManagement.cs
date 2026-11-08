using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config;
using DbDataKit.Config.Yaml;
using DbDataKit.Web.Trust;
using DbDataGateway.Server.Configuration;
using Microsoft.Extensions.Configuration;

namespace DbDataGateway.Server.Management;

/// <summary>What a management call answered: an HTTP status and a JSON body.</summary>
public sealed record ManagementResult(int Status, JsonNode Body)
{
    public static ManagementResult Ok(JsonNode body) => new(200, body);
    public static ManagementResult Problem(int status, string error) => new(status, new JsonObject { ["error"] = error });
}

/// <summary>
/// What the Gateway's management API does (plan 13, P03), apart from how a call arrives: the same answers whether a console asks over https with its token or the command line asks over the local channel. A transport
/// says who is calling and which roles they hold (<paramref name="holds"/>), and this checks the role each call needs.
/// <list type="bullet">
/// <item><c>GET /api/v1/about</c>: which server this is (viewer).</item>
/// <item><c>GET /api/v1/config</c>: every effective setting with where it came from, secrets masked, and what the validator says about the current configuration (admin).</item>
/// <item><c>GET /api/v1/clients</c>: the paired server clients, by id and name, for handing a data source over (admin).</item>
/// <item><c>GET /api/v1/state</c> (viewer), <c>GET</c> and <c>POST /api/v1/state/maintenance</c> (viewer, admin): the state and its pending migrations, and the maintenance mode (<c>DbDataKit.State.Api</c>).</item>
/// <item><c>GET</c>, <c>PUT</c> <c>/api/v1/config/server.yaml</c> and <c>POST .../check</c>, <c>.../edit</c>: the serving settings with secrets masked, checked as a draft and written as a commit (admin; <see cref="GatewayServerSettings"/>).</item>
/// <item><c>POST /api/v1/config/validate</c>: body <c>{"files": {"server.yaml": "...", "connections/erp.yaml": "..."}}</c>: whether the configuration would be valid with those files in place of the current ones; nothing is written (admin).</item>
/// </list>
/// A secret is never part of an answer: the settings are shown the way <c>config show</c> shows them, and the sentinel test puts a value in every place a secret can be and looks for it in every answer.
/// </summary>
public sealed partial class GatewayManagement(IConfigurationRoot configuration, string configDirectory, string instanceId, string version, GatewayDataSources? dataSources = null, GatewayIdentities? identities = null, GatewayActivity? activity = null, GatewayCertificate? certificate = null, DbDataKit.Config.Git.GitCommitService? git = null, GatewaySecrets? secrets = null, IPairingCodeStore? pairingCodes = null, GatewaySemanticModels? semanticModels = null, Func<IReadOnlyList<PairedClient>>? pairedClients = null, DbDataKit.Identity.IdentityAdminApi? identityAdmin = null, GatewayEntityModels? entityModels = null, DbDataKit.State.Api.StateManagement? stateApi = null, GatewayApps? apps = null, GatewayViewsManagement? views = null, IAuditLog? audit = null, GatewayServerSettings? serverSettings = null)
{
    public const string Prefix = "/api/v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ManagementResult> HandleAsync(string method, string path, string? body, ManagementCaller caller, CancellationToken cancellationToken = default)
    {
        var route = path.Split('?')[0].TrimEnd('/');
        var query = path.Contains('?') ? System.Web.HttpUtility.ParseQueryString(path[(path.IndexOf('?') + 1)..]) : new System.Collections.Specialized.NameValueCollection();
        method = method.ToUpperInvariant();
        // the state of this server and its maintenance mode (plan 29b, C05): the shared endpoints, with the same roles and audit as everywhere
        if (stateApi is not null && DbDataKit.State.Api.StateManagement.Handles(route))
        {
            var answer = await stateApi.HandleAsync(method, route, body, caller.ToStateCaller(), cancellationToken);
            return new ManagementResult(answer.Status, answer.Body);
        }
        if (views is not null && GatewayViewsManagement.Handles(route))
            return await views.HandleAsync(method, route, path.Contains('?') ? path[(path.IndexOf('?') + 1)..] : null, body, caller, cancellationToken);
        switch ((method, route))
        {
            case ("GET", Prefix + "/about"):
                return caller.Holds(ServerRole.Viewer) ? ManagementResult.Ok(About()) : Denied(ServerRole.Viewer);
            case ("GET", Prefix + "/config"):
                return caller.Holds(ServerRole.Admin) ? ManagementResult.Ok(Config()) : Denied(ServerRole.Admin);
            case ("GET", Prefix + "/clients") when pairedClients is not null:
                // T13: the server clients (a Build server) a data source can be handed to; ids and names only, never a key
                return caller.Holds(ServerRole.Admin)
                    ? ManagementResult.Ok(new JsonObject { ["clients"] = new JsonArray([.. pairedClients().Where(c => c.Kind == ClientKind.Server).Select(c => (JsonNode)new JsonObject { ["id"] = c.Id, ["name"] = c.Name })]) })
                    : Denied(ServerRole.Admin);
            case ("POST", PairingCodeIssuer.Path) when pairingCodes is not null:
            {
                // T11: the code a console asks for so that another server (a Build server) can pair as a publisher without anyone typing it
                var (status, answer) = PairingCodeIssuer.Issue(pairingCodes, caller.Holds(ServerRole.Admin), body);
                return new ManagementResult(status, answer);
            }
            case ("POST", Prefix + "/config/validate"):
                return caller.Holds(ServerRole.Admin) ? Validate(body) : Denied(ServerRole.Admin);
            case (_, Prefix + "/config/server.yaml" or Prefix + "/config/server.yaml/check" or Prefix + "/config/server.yaml/edit"):
                return serverSettings is null ? ManagementResult.Problem(404, "There is no such call.") : await serverSettings.HandleAsync(method, route, body, caller, cancellationToken);
            case ("GET", Prefix + "/config/history"):
                return caller.Holds(ServerRole.Admin) ? History(int.TryParse(query["limit"], out var limit) ? Math.Clamp(limit, 1, 200) : 50) : Denied(ServerRole.Admin);
        }

        if (method == "GET" && route.StartsWith(Prefix + "/config/history/", StringComparison.Ordinal))
            return caller.Holds(ServerRole.Admin) ? Commit(route[(Prefix + "/config/history/").Length..]) : Denied(ServerRole.Admin);

        if (route == DbDataKit.Identity.IdentityAdminApi.Prefix || route.StartsWith(DbDataKit.Identity.IdentityAdminApi.Prefix + "/", StringComparison.Ordinal))
        {
            // the identity home this gateway serves (13a U21): only when its identity module is on
            if (identityAdmin is null) return ManagementResult.Problem(404, "This gateway serves no identity home (identity.enabled is off).");
            JsonNode? json;
            try { json = string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body); }
            catch (JsonException) { return ManagementResult.Problem(400, "The body is not JSON."); }
            if (json is not null and not JsonObject) return ManagementResult.Problem(400, "The body is a JSON object.");
            var rest = path[DbDataKit.Identity.IdentityAdminApi.Prefix.Length..];
            var answer = identityAdmin.Handle(method, rest, json, caller.Holds(ServerRole.Admin), caller.Token?.SubjectId ?? "local:" + caller.Author.Name);
            if (method != "GET" && audit is not null && answer.Status != 403) AuditIdentityChange(caller, method, rest, json, answer.Status);
            return new ManagementResult(answer.Status, answer.Body);
        }
        if (route == Prefix + "/data-sources" || route.StartsWith(Prefix + "/data-sources/", StringComparison.Ordinal))
        {
            if (dataSources is null) return ManagementResult.Problem(404, "There is no such call.");
            // an administrator may do everything; a publishing server client (a Build server) only what the owner rules let it do to its own data sources, which GatewayDataSources checks
            if (!caller.Holds(ServerRole.Admin) && !caller.Holds(ServerRole.Publisher)) return Denied(ServerRole.Admin);
            var rest = route.Length > (Prefix + "/data-sources").Length ? route[(Prefix + "/data-sources/").Length..].Split('/') : [];
            return await dataSources.HandleAsync(method, rest, body, query["expected_version"], caller, cancellationToken);
        }
        if (route == Prefix + "/semantic-models" || route.StartsWith(Prefix + "/semantic-models/", StringComparison.Ordinal))
        {
            if (semanticModels is null) return ManagementResult.Problem(404, "There is no such call.");
            // an administrator may do everything; a publishing server client only what the owner rules let it do to its own models, which GatewaySemanticModels checks
            if (!caller.Holds(ServerRole.Admin) && !caller.Holds(ServerRole.Publisher)) return Denied(ServerRole.Admin);
            var rest = route.Length > (Prefix + "/semantic-models").Length ? route[(Prefix + "/semantic-models/").Length..].Split('/') : [];
            return await semanticModels.HandleAsync(method, rest, body, caller, cancellationToken);
        }
        if (route == Prefix + "/entity-models" || route.StartsWith(Prefix + "/entity-models/", StringComparison.Ordinal))
        {
            if (entityModels is null) return ManagementResult.Problem(404, "There is no such call.");
            // a DbDataStore server publishes as a publishing client, within the owner rules GatewayEntityModels applies; an administrator does everything (plan 20 O10)
            if (!caller.Holds(ServerRole.Admin) && !caller.Holds(ServerRole.Publisher)) return Denied(ServerRole.Admin);
            var rest = route.Length > (Prefix + "/entity-models").Length ? route[(Prefix + "/entity-models/").Length..].Split('/') : [];
            return await entityModels.HandleAsync(method, rest, body, caller, cancellationToken);
        }
        foreach (var kind in new[] { "apps", "app-instances", "app-approvals", "app-navigation" })
            if (route == Prefix + "/" + kind || route.StartsWith(Prefix + "/" + kind + "/", StringComparison.Ordinal))
            {
                if (apps is null) return ManagementResult.Problem(404, "There is no such call.");
                // a publishing client (a tester's machine, a Build server) publishes apps and makes their instances; approving is the approver role's, which no publisher pairing holds (plan 26, AP26)
                var rest = route.Length > (Prefix + "/" + kind).Length ? route[(Prefix + "/" + kind + "/").Length..].Split('/') : [];
                return await apps.HandleAsync(method, kind, rest, body, caller, cancellationToken);
            }
        if (route == Prefix + "/secrets" || route.StartsWith(Prefix + "/secrets/", StringComparison.Ordinal))
        {
            if (secrets is null) return ManagementResult.Problem(404, "There is no such call.");
            // a publishing client keeps the secrets of its own data sources here; an administrator does too, under their own name
            if (!caller.Holds(ServerRole.Admin) && !caller.Holds(ServerRole.Publisher)) return Denied(ServerRole.Admin);
            return await secrets.HandleAsync(method, route[(Prefix.Length + 1)..].Split('/'), body, caller, cancellationToken);
        }
        if (route is Prefix + "/identities" or Prefix + "/auth" || route.StartsWith(Prefix + "/users/", StringComparison.Ordinal))
        {
            if (identities is null) return ManagementResult.Problem(404, "There is no such call.");
            if (!caller.Holds(ServerRole.Admin)) return Denied(ServerRole.Admin);
            return await identities.HandleAsync(method, route[(Prefix.Length + 1)..].Split('/'), body, query["expected_version"], caller, cancellationToken);
        }
        var first = route.Length > Prefix.Length ? route[(Prefix.Length + 1)..].Split('/') : [];
        if (first.Length > 0 && first[0] is "overview" or "sessions" or "queries" or "limits" or "throttle" or "notifications")
        {
            if (activity is null) return ManagementResult.Problem(404, "There is no such call.");
            // the feed is what a monitoring view reads, so a viewer may; the rest of what the gateway is doing is for an administrator
            var needed = first[0] is "notifications" or "overview" ? ServerRole.Viewer : ServerRole.Admin;
            if (!caller.Holds(needed)) return Denied(needed);
            return activity.Handle(method, first, query["client"], query["user"], caller, long.TryParse(query["since_id"], out var since) ? since : null);
        }
        if (first.Length > 0 && first[0] is "certificate" or "failures")
        {
            if (certificate is null) return ManagementResult.Problem(404, "There is no such call.");
            if (!caller.Holds(ServerRole.Admin)) return Denied(ServerRole.Admin);
            return certificate.Handle(method, first, body, caller);
        }
        return ManagementResult.Problem(404, "There is no such call.");
    }

    /// <summary>
    /// A change to the identity home through the management API is in the audit: what kind of thing, which one (an id from the path, never a body), and whether it was done. An attribute mapping names its attribute, the group and the value, which are
    /// settings and not secrets; a token or a password is never in the body of an entry (the answer of a token's creation is not recorded).
    /// </summary>
    private void AuditIdentityChange(ManagementCaller caller, string method, string rest, JsonNode? body, int status)
    {
        var parts = rest.Split('?')[0].Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var verb = method == "DELETE" ? "remove" : "change";
        var resource = parts[0];
        string target = string.Join('/', parts.Skip(1));
        string outcome = status < 300 ? "done" : status == 404 ? "not found" : "refused";
        if (resource == "attribute-mappings" && method == "POST")
        {
            verb = "add";
            string Say(string key) => body?[key] is JsonValue v && v.TryGetValue<string>(out var text) ? text.Trim() : "";
            target = Say("attribute");
            if (status < 300) outcome = $"{Say("source")}:{Say("group")} gives {Say("attribute")}={Say("value")}";
        }
        else if (method == "POST" && parts.Length == 1) verb = "add";
        audit!.Append(caller.Audit($"identity {resource} {verb}", target, outcome, DateTimeOffset.UtcNow));
    }

    private static ManagementResult Denied(ServerRole needed) => ManagementResult.Problem(403, $"This call needs the {needed.ToString().ToLowerInvariant()} role.");

    // ---- history ----

    private ManagementResult History(int limit)
    {
        if (git is null) return ManagementResult.Ok(new JsonObject { ["commits"] = new JsonArray() });
        return ManagementResult.Ok(new JsonObject
        {
            ["commits"] = new JsonArray([.. git.GetHistory("", limit).Select(c => (JsonNode)new JsonObject
            {
                ["sha"] = c.Sha, ["message"] = c.Message, ["author"] = c.AuthorName, ["email"] = c.AuthorEmail, ["when"] = c.WhenUtc,
            })]),
        });
    }

    private ManagementResult Commit(string sha)
    {
        if (git is null || sha.Length < 7 || !sha.All(char.IsAsciiHexDigit)) return ManagementResult.Problem(404, "There is no such commit.");
        try
        {
            var diff = git.GetCommitDiff("", sha);
            return ManagementResult.Ok(new JsonObject
            {
                ["sha"] = diff.Sha, ["message"] = diff.Message, ["truncated"] = diff.Truncated,
                ["changes"] = new JsonArray([.. diff.Changes.Select(c => (JsonNode)new JsonObject
                {
                    ["path"] = c.Path, ["kind"] = c.Kind.ToString().ToLowerInvariant(), ["before"] = c.Before is null ? null : Redact(c.Before), ["after"] = c.After is null ? null : Redact(c.After),
                })]),
            });
        }
        catch (DbDataKit.Config.Git.GitCommitNotFoundException) { return ManagementResult.Problem(404, "There is no such commit."); }
    }

    /// <summary>A password hash, a signing key and a certificate password are not shown, in a diff any more than anywhere else: the line says that the value is there, and that it changed.</summary>
    internal static string Redact(string text) => SecretLine().Replace(text, "${key}${sep}********");

    [System.Text.RegularExpressions.GeneratedRegex(@"(?<key>\b(?:passwordHash|signingKey|certificatePassword)\b)(?<sep>\s*:\s*)(?:'[^']*'|""[^""]*""|[^,}\r\n]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex SecretLine();

    private JsonObject About() => new() { ["product"] = "dbdatagateway", ["version"] = version, ["instance_id"] = instanceId, ["api_version"] = ManagementApi.Version };

    private JsonObject Config()
    {
        var load = GatewayConfig.Load(configuration);
        return new JsonObject
        {
            ["valid"] = load.Ok,
            ["schema_version"] = load.Value?.SchemaVersion,
            ["values"] = new JsonArray([.. GatewayConfig.Show(load).Select(e => (JsonNode)new JsonObject
            {
                ["path"] = e.Path, ["value"] = e.Value, ["source"] = e.Source, ["kind"] = e.Kind.ToString().ToLowerInvariant(), ["line"] = e.Line, ["secret"] = e.Secret,
            })]),
            ["diagnostics"] = Diagnostics(load.Diagnostics),
        };
    }

    private sealed record ValidateRequest(Dictionary<string, string>? Files);

    private ManagementResult Validate(string? body)
    {
        var files = ParseFiles(body, out var problem);
        return problem is not null ? ManagementResult.Problem(400, problem) : ValidateFiles(files!);
    }

    private static Dictionary<string, string>? ParseFiles(string? body, out string? problem)
    {
        problem = null;
        try
        {
            var request = string.IsNullOrWhiteSpace(body) ? new ValidateRequest(new()) : JsonSerializer.Deserialize<ValidateRequest>(body, Json);
            return request?.Files ?? new();
        }
        catch (JsonException) { problem = "The body is a JSON object: {\"files\": {\"server.yaml\": \"...\"}}."; return null; }
    }

    /// <summary>Whether the configuration would be valid with these files (layout names, relative to the configuration folder) in place of the current ones. Nothing is written.</summary>
    public ManagementResult ValidateFiles(IReadOnlyDictionary<string, string> files) =>
        WithCandidate(files, load => ManagementResult.Ok(new JsonObject { ["valid"] = load.Ok, ["diagnostics"] = Diagnostics(load.Diagnostics) }));

    /// <summary>The configuration as it would be with these files in place of the current ones, handed to <paramref name="use"/> (the typed settings and the validator's diagnostics); nothing is written to the real folder.</summary>
    public ManagementResult WithCandidate(IReadOnlyDictionary<string, string> files, Func<GatewayConfigLoad, ManagementResult> use)
    {
        // Only the files of the layout, and only inside it: a name that leaves the folder, or is not one of the layout's, is refused before anything is written even to the scratch copy.
        foreach (var name in files.Keys)
            if (!IsLayoutFile(name)) return ManagementResult.Problem(400, $"'{name}' is not a file of the configuration (server.yaml, gateway/auth.yaml, gateway/users.yaml, connections/<name>.yaml).");

        var scratch = Directory.CreateTempSubdirectory("dbdatagateway-validate-").FullName;
        try
        {
            if (Directory.Exists(configDirectory)) CopyDirectory(configDirectory, scratch);
            foreach (var (name, text) in files)
            {
                var target = Path.Combine(scratch, name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.WriteAllText(target, text);
            }
            try
            {
                var candidate = new ConfigurationBuilder().Add(new GatewayConfigDirectorySource(scratch)).Build();
                return use(GatewayConfig.Load(candidate));
            }
            catch (InvalidDataException ex)
            {
                // a file that does not parse at all: the reader's own message, which names each file and line
                return ManagementResult.Ok(new JsonObject { ["valid"] = false, ["diagnostics"] = new JsonArray(new JsonObject { ["code"] = "DBP-101", ["severity"] = "error", ["message"] = ex.Message.Replace(scratch, "config") }), ["unreadable"] = true });
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    private static bool IsLayoutFile(string name) =>
        name is "server.yaml" or "gateway/auth.yaml" or "gateway/users.yaml"
        || (name.StartsWith("connections/", StringComparison.Ordinal) && name.EndsWith(".yaml", StringComparison.Ordinal) && name["connections/".Length..] is { Length: > 5 } stem && !stem.Contains('/') && !stem.Contains('\\') && !stem.Contains(".."));

    /// <summary>Copies the files of the layout and nothing else. The folder is a git repository, whose objects are read-only files that Windows will not delete again, and validation reads none of it.</summary>
    private static void CopyDirectory(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, file);
            if (!IsLayoutFile(relative.Replace(Path.DirectorySeparatorChar, '/'))) continue;
            var target = Path.Combine(to, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    internal static JsonArray DiagnosticsOf(IEnumerable<ConfigDiagnostic> diagnostics) => Diagnostics(diagnostics);

    private static JsonArray Diagnostics(IEnumerable<ConfigDiagnostic> diagnostics) =>
        new([.. diagnostics.Select(d => (JsonNode)new JsonObject
        {
            ["code"] = d.Code, ["severity"] = d.Severity.ToString().ToLowerInvariant(), ["key"] = d.KeyPath, ["file"] = d.Path, ["line"] = d.Line, ["column"] = d.Column, ["message"] = d.Message, ["fix"] = d.Fix,
        })]);
}
