using System.Text.Json;
using System.Text.Json.Nodes;

namespace DbDataKit.Apps;

/// <summary>A Store server the apps host is paired with: where it is, its instance id (the audience of the tokens made for it) and the certificate fingerprint every call pins.</summary>
public sealed record AppStore(string Id, Uri Url, string Fingerprint, string Name);

/// <summary>The paired Store servers, kept in a JSON file next to the host's data so pairing needs no restart. The host's signing key is in the secret store; this file holds nothing secret.</summary>
public sealed class AppStores(string path)
{
    private readonly object _lock = new();

    public IReadOnlyList<AppStore> All()
    {
        lock (_lock)
        {
            if (!File.Exists(path)) return [];
            try
            {
                return [.. (JsonNode.Parse(File.ReadAllText(path)) as JsonArray ?? []).OfType<JsonObject>()
                    .Select(o => new AppStore((string)o["id"]!, new Uri((string)o["url"]!), (string)o["fingerprint"]!, (string?)o["name"] ?? (string)o["id"]!))];
            }
            catch (Exception ex) when (ex is JsonException or InvalidCastException or NullReferenceException or UriFormatException)
            {
                throw new InvalidOperationException($"{path} is not a list of paired stores: {ex.Message} Pair the stores again, or remove the file.");
            }
        }
    }

    public AppStore? Find(string id) => All().FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.Ordinal));

    /// <summary>Adds the store, or replaces the one with the same id (pairing again after a renewed certificate).</summary>
    public void Save(AppStore store)
    {
        lock (_lock)
        {
            var all = All().Where(s => s.Id != store.Id).Append(store).OrderBy(s => s.Id, StringComparer.Ordinal);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, new JsonArray(all.Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["url"] = s.Url.AbsoluteUri, ["fingerprint"] = s.Fingerprint, ["name"] = s.Name }).ToArray()).ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    public bool Remove(string id)
    {
        lock (_lock)
        {
            var all = All();
            if (all.All(s => s.Id != id)) return false;
            File.WriteAllText(path, new JsonArray(all.Where(s => s.Id != id).Select(s => (JsonNode)new JsonObject { ["id"] = s.Id, ["url"] = s.Url.AbsoluteUri, ["fingerprint"] = s.Fingerprint, ["name"] = s.Name }).ToArray()).ToJsonString());
            return true;
        }
    }
}
