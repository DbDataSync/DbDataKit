using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataKit.Config;
using DbDataKit.Config.Yaml;
using DbDataKit.Web.Trust;
using DbDataProxy.Server.Configuration;
using Microsoft.Extensions.Configuration;

namespace DbDataProxy.Server.Management;

/// <summary>What a management call answered: an HTTP status and a JSON body.</summary>
public sealed record ManagementResult(int Status, JsonNode Body)
{
    public static ManagementResult Ok(JsonNode body) => new(200, body);
    public static ManagementResult Problem(int status, string error) => new(status, new JsonObject { ["error"] = error });
}

/// <summary>
/// What the Proxy's management API does (plan 13, P03), apart from how a call arrives: the same answers whether a console asks over https with its token or the command line asks over the local channel. A transport
/// says who is calling and which roles they hold (<paramref name="holds"/>), and this checks the role each call needs.
/// <list type="bullet">
/// <item><c>GET /api/v1/about</c>: which server this is (viewer).</item>
/// <item><c>GET /api/v1/config</c>: every effective setting with where it came from, secrets masked, and what the validator says about the current configuration (admin).</item>
/// <item><c>POST /api/v1/config/validate</c>: body <c>{"files": {"server.yaml": "...", "connections/erp.yaml": "..."}}</c>: whether the configuration would be valid with those files in place of the current ones; nothing is written (admin).</item>
/// </list>
/// A secret is never part of an answer: the settings are shown the way <c>config show</c> shows them, and the sentinel test puts a value in every place a secret can be and looks for it in every answer.
/// </summary>
public sealed class ProxyManagement(IConfigurationRoot configuration, string configDirectory, string instanceId, string version, ProxyDataSources? dataSources = null, ProxyIdentities? identities = null, ProxyActivity? activity = null)
{
    public const string Prefix = "/api/v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ManagementResult> HandleAsync(string method, string path, string? body, ManagementCaller caller, CancellationToken cancellationToken = default)
    {
        var route = path.Split('?')[0].TrimEnd('/');
        var query = path.Contains('?') ? System.Web.HttpUtility.ParseQueryString(path[(path.IndexOf('?') + 1)..]) : new System.Collections.Specialized.NameValueCollection();
        method = method.ToUpperInvariant();
        switch ((method, route))
        {
            case ("GET", Prefix + "/about"):
                return caller.Holds(ServerRole.Viewer) ? ManagementResult.Ok(About()) : Denied(ServerRole.Viewer);
            case ("GET", Prefix + "/config"):
                return caller.Holds(ServerRole.Admin) ? ManagementResult.Ok(Config()) : Denied(ServerRole.Admin);
            case ("POST", Prefix + "/config/validate"):
                return caller.Holds(ServerRole.Admin) ? Validate(body) : Denied(ServerRole.Admin);
        }

        if (route == Prefix + "/data-sources" || route.StartsWith(Prefix + "/data-sources/", StringComparison.Ordinal))
        {
            if (dataSources is null) return ManagementResult.Problem(404, "There is no such call.");
            if (!caller.Holds(ServerRole.Admin)) return Denied(ServerRole.Admin);
            var rest = route.Length > (Prefix + "/data-sources").Length ? route[(Prefix + "/data-sources/").Length..].Split('/') : [];
            return await dataSources.HandleAsync(method, rest, body, query["expected_version"], caller, cancellationToken);
        }
        if (route is Prefix + "/identities" or Prefix + "/auth" || route.StartsWith(Prefix + "/users/", StringComparison.Ordinal))
        {
            if (identities is null) return ManagementResult.Problem(404, "There is no such call.");
            if (!caller.Holds(ServerRole.Admin)) return Denied(ServerRole.Admin);
            return await identities.HandleAsync(method, route[(Prefix.Length + 1)..].Split('/'), body, query["expected_version"], caller, cancellationToken);
        }
        var first = route.Length > Prefix.Length ? route[(Prefix.Length + 1)..].Split('/') : [];
        if (first.Length > 0 && first[0] is "sessions" or "queries" or "limits" or "throttle")
        {
            if (activity is null) return ManagementResult.Problem(404, "There is no such call.");
            if (!caller.Holds(ServerRole.Admin)) return Denied(ServerRole.Admin);
            return activity.Handle(method, first, query["client"], query["user"], caller);
        }
        return ManagementResult.Problem(404, "There is no such call.");
    }

    private static ManagementResult Denied(ServerRole needed) => ManagementResult.Problem(403, $"This call needs the {needed.ToString().ToLowerInvariant()} role.");

    internal const string CandidatePrefix = "";

    private JsonObject About() => new() { ["product"] = "dbdataproxy", ["version"] = version, ["instance_id"] = instanceId, ["api_version"] = ManagementApi.Version };

    private JsonObject Config()
    {
        var load = ProxyConfig.Load(configuration);
        return new JsonObject
        {
            ["valid"] = load.Ok,
            ["schema_version"] = load.Value?.SchemaVersion,
            ["values"] = new JsonArray([.. ProxyConfig.Show(load).Select(e => (JsonNode)new JsonObject
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
    public ManagementResult ValidateFiles(IReadOnlyDictionary<string, string> files)
    {
        // Only the files of the layout, and only inside it: a name that leaves the folder, or is not one of the layout's, is refused before anything is written even to the scratch copy.
        foreach (var name in files.Keys)
            if (!IsLayoutFile(name)) return ManagementResult.Problem(400, $"'{name}' is not a file of the configuration (server.yaml, proxy/auth.yaml, proxy/users.yaml, connections/<name>.yaml).");

        var scratch = Directory.CreateTempSubdirectory("dbdataproxy-validate-").FullName;
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
                var candidate = new ConfigurationBuilder().Add(new ProxyConfigDirectorySource(scratch)).Build();
                var load = ProxyConfig.Load(candidate);
                return ManagementResult.Ok(new JsonObject { ["valid"] = load.Ok, ["diagnostics"] = Diagnostics(load.Diagnostics) });
            }
            catch (InvalidDataException ex)
            {
                // a file that does not parse at all: the reader's own message, which names each file and line
                return ManagementResult.Ok(new JsonObject { ["valid"] = false, ["diagnostics"] = new JsonArray(new JsonObject { ["code"] = "DBP-101", ["severity"] = "error", ["message"] = ex.Message.Replace(scratch, "config") }) });
            }
        }
        finally
        {
            try { Directory.Delete(scratch, recursive: true); } catch (IOException) { }
        }
    }

    private static bool IsLayoutFile(string name) =>
        name is "server.yaml" or "proxy/auth.yaml" or "proxy/users.yaml"
        || (name.StartsWith("connections/", StringComparison.Ordinal) && name.EndsWith(".yaml", StringComparison.Ordinal) && name["connections/".Length..] is { Length: > 5 } stem && !stem.Contains('/') && !stem.Contains('\\') && !stem.Contains(".."));

    private static void CopyDirectory(string from, string to)
    {
        foreach (var directory in Directory.EnumerateDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, directory)));
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)), overwrite: true);
    }

    private static JsonArray Diagnostics(IEnumerable<ConfigDiagnostic> diagnostics) =>
        new([.. diagnostics.Select(d => (JsonNode)new JsonObject
        {
            ["code"] = d.Code, ["severity"] = d.Severity.ToString().ToLowerInvariant(), ["key"] = d.KeyPath, ["file"] = d.Path, ["line"] = d.Line, ["column"] = d.Column, ["message"] = d.Message, ["fix"] = d.Fix,
        })]);
}
