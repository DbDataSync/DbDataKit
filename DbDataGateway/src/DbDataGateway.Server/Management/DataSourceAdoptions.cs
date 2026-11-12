using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataGateway.Server.Management;

/// <summary>
/// The record of a publishing client putting a model on a data source it did not make (plan 33 SC21f): the request said <c>adopt: &lt;data source&gt;</c>, the Gateway let it, and kept <b>who</b> (client id and the person the call vouched for),
/// <b>when</b>, <b>which data source</b> and for which model, here (<c>adoptions/&lt;data source&gt;.json</c> of the configuration folder, the data source's metadata: a data source file is the definition and is not changed by it) and in the audit.
/// A data source's own read (<c>GET /api/v1/data-sources/{name}</c>) lists them. A record is never rewritten, only appended.
/// </summary>
public sealed class DataSourceAdoptions(string configDirectory)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly object _gate = new();

    private string FileOf(string dataSource) => Path.Combine(configDirectory, "adoptions", dataSource + ".json");

    /// <summary>Appends the record of one adoption.</summary>
    public void Record(string dataSource, string clientId, string person, string model, string kind, DateTimeOffset at)
    {
        lock (_gate)
        {
            var list = ReadArray(dataSource);
            list.Add(new JsonObject { ["data_source"] = dataSource, ["client"] = clientId, ["by"] = person, ["at"] = at, ["model"] = model, ["kind"] = kind });
            Directory.CreateDirectory(Path.GetDirectoryName(FileOf(dataSource))!);
            var temp = FileOf(dataSource) + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
            File.WriteAllText(temp, list.ToJsonString(Json), new UTF8Encoding(false));
            File.Move(temp, FileOf(dataSource), overwrite: true);
        }
    }

    /// <summary>What was recorded for the data source, oldest first.</summary>
    public JsonArray Read(string dataSource)
    {
        lock (_gate) return ReadArray(dataSource);
    }

    private JsonArray ReadArray(string dataSource)
    {
        try { return File.Exists(FileOf(dataSource)) ? JsonNode.Parse(File.ReadAllText(FileOf(dataSource))) as JsonArray ?? [] : []; }
        catch (JsonException) { return []; }
    }
}
