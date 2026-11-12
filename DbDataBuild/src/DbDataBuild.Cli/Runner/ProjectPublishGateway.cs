using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DbDataBuild.Cli.Server;
using DbDataBuild.Core;
using DbDataBuild.Semantic;

namespace DbDataBuild.Cli.Runner;

/// <summary>
/// `dbdatabuild project publish --to gateway --out &lt;folder&gt;` (plan 33 SC20e): what a publish of the project to a Gateway would send, written to a folder and sent nowhere, **with no database login read or written**. A Workspace that holds no logins (D20)
/// publishes this folder to a Gateway with its publisher key; Build's own server publishes the same models through <see cref="ProxyPublisher"/>, which does send the read login of a connection.
/// <para>
/// The folder: <c>gateway.json</c> (<see cref="Schema"/>, the models and the data sources they read), <c>models/&lt;name&gt;.catalog.json</c> (the compiled catalog of each semantic model: the file the Gateway's
/// <c>PUT /api/v1/semantic-models/{name}</c> takes as <c>catalog</c>) and <c>data-sources/&lt;name&gt;.yaml</c> (the read-only <c>connection/2</c> document of each connection a model reads, grants and serving policy as the project says, **without
/// <c>connection_string_secret</c>**: the login stays where it is kept, and the data source is one the Gateway already has).
/// </para>
/// </summary>
internal static class ProjectPublishGateway
{
    public const string Schema = "gateway-publish/1";
    public const string ManifestFile = "gateway.json";

    public static int Run(CommandSpec spec, string root, string? outFolder, TextWriter output, TextWriter error)
    {
        if (outFolder is null)
        {
            error.WriteLine("--to gateway writes what a Gateway publish would send and sends nothing: give --out <directory>. The login of a connection never leaves its server, so a Workspace (or Build's server) sends it.");
            return CliApp.ExitUsage;
        }

        var ctx = ProjectContext.Load(root);
        var problems = ctx.Diagnostics.Where(d => d.Severity == Severity.Error && d.Location.File.StartsWith(SemanticProject.Folder + "/", StringComparison.Ordinal)).ToList();
        foreach (var d in problems) error.Diag(d);
        var broken = ctx.Semantic.Models.Where(m => m.Compilation is null).ToList();
        if (broken.Count > 0)
        {
            output.WriteLine($"{broken.Count} semantic file(s) do not compile ({string.Join(", ", broken.Select(m => m.File))}): fix them (`{ProductInfo.Cli} project compile` shows the findings). Nothing was written.");
            output.Next("project compile");
            return CliApp.ExitFindings;
        }
        if (ctx.Semantic.Models.Count == 0)
        {
            error.WriteLine($"The project has no semantic model ({SemanticProject.Folder}/*.yml), so there is nothing for a Gateway to serve. Nothing was written.");
            return CliApp.ExitFindings;
        }

        // every connection a model reads must be one a Gateway serves (its providers); the first that is not ends the publish before anything is written
        var connections = ctx.Semantic.Models.SelectMany(m => m.Connections.Values).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
        foreach (var connection in connections)
        {
            if (!ctx.Config.Connections.TryGetValue(connection, out var config)) { error.WriteLine($"A semantic model reads the connection '{connection}', which the project does not define."); return CliApp.ExitFindings; }
            if (ProxyPublisher.ProxyEngineOf(config) is null)
            {
                error.WriteLine($"The connection '{connection}' is {config.Kind.Describe()}; a Gateway serves {ProxyPublisher.ProxyEngineList} connections, not {config.Kind.Driver}. Nothing was written.");
                return CliApp.ExitFindings;
            }
        }

        var folder = Path.GetFullPath(outFolder);
        Directory.CreateDirectory(Path.Combine(folder, "models"));
        Directory.CreateDirectory(Path.Combine(folder, "data-sources"));
        var utf8 = new UTF8Encoding(false);
        var models = new JsonArray();
        foreach (var model in ctx.Semantic.Models.OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            var file = $"models/{model.Name}.catalog.json";
            File.WriteAllText(Path.Combine(folder, file), model.Compilation!.Json.ToJsonString(), utf8);
            var sources = new JsonObject();
            foreach (var (source, connection) in model.Connections.OrderBy(c => c.Key, StringComparer.Ordinal)) sources[source] = connection;
            models.Add(new JsonObject { ["name"] = model.Name, ["file"] = file, ["sources"] = sources, ["data_sources"] = new JsonArray([.. model.Connections.Values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Select(c => (JsonNode?)c)]) });
        }
        var dataSources = new JsonArray();
        foreach (var connection in connections)
        {
            var config = ctx.Config.Connections[connection];
            var engine = ProxyPublisher.ProxyEngineOf(config)!;
            var file = $"data-sources/{connection}.yaml";
            File.WriteAllText(Path.Combine(folder, file), ProxyPublisher.DataSourceText(connection, engine, null, config.Grants, config.GatewayPolicy), utf8);
            dataSources.Add(new JsonObject { ["name"] = connection, ["engine"] = engine, ["file"] = file, ["access"] = "readonly" });
        }
        var manifest = new JsonObject { ["schema"] = Schema, ["models"] = models, ["data_sources"] = dataSources };
        File.WriteAllText(Path.Combine(folder, ManifestFile), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", utf8);

        var names = ctx.Semantic.Models.Select(m => m.Name).Order(StringComparer.Ordinal).ToList();
        output.Payload("written_to", folder);
        output.Payload("models", names);
        output.Payload("data_sources", connections);
        output.WriteLine($"Wrote what a Gateway publish sends to {folder}: {models.Count} model(s) ({string.Join(", ", names)}) over {dataSources.Count} data source(s) ({string.Join(", ", connections)}). No login was read; nothing was sent. The data sources are the ones the Gateway already has.");
        return CliApp.ExitOk;
    }
}
