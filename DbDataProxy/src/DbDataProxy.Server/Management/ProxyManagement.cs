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
public sealed class ProxyManagement(IConfigurationRoot configuration, string configDirectory, string instanceId, string version)
{
    public const string Prefix = "/api/v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public ManagementResult Handle(string method, string path, string? body, Func<ServerRole, bool> holds)
    {
        var route = path.Split('?')[0].TrimEnd('/');
        switch ((method.ToUpperInvariant(), route))
        {
            case ("GET", Prefix + "/about"):
                return holds(ServerRole.Viewer) ? ManagementResult.Ok(About()) : Denied(ServerRole.Viewer);
            case ("GET", Prefix + "/config"):
                return holds(ServerRole.Admin) ? ManagementResult.Ok(Config()) : Denied(ServerRole.Admin);
            case ("POST", Prefix + "/config/validate"):
                return holds(ServerRole.Admin) ? Validate(body) : Denied(ServerRole.Admin);
            default:
                return ManagementResult.Problem(404, "There is no such call.");
        }
    }

    private static ManagementResult Denied(ServerRole needed) => ManagementResult.Problem(403, $"This call needs the {needed.ToString().ToLowerInvariant()} role.");

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
        ValidateRequest? request;
        try { request = string.IsNullOrWhiteSpace(body) ? new ValidateRequest(new()) : JsonSerializer.Deserialize<ValidateRequest>(body, Json); }
        catch (JsonException) { return ManagementResult.Problem(400, "The body is a JSON object: {\"files\": {\"server.yaml\": \"...\"}}."); }
        var files = request?.Files ?? new();

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
