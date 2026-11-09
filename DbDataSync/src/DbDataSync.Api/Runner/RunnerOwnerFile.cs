using System.Text.Json;
using System.Text.Json.Serialization;
using DbDataKit.State;

namespace DbDataSync.Api.Runner;

/// <summary>Where the runner is that a replication moves to: the paired runner (name or address, as <c>package publish --server</c> takes it), the channel and the package its replications are published to, and the directory of the client the pairing is kept in.</summary>
public sealed record RunnerLink(string Server, string Channel, string Package, string? ClientDirectory = null);

/// <summary>The file's content: the link and, per replication, who schedules it (<c>old</c> is the default and is not written).</summary>
public sealed record RunnerSettings(RunnerLink? Link, IReadOnlyDictionary<string, string> Owners)
{
    public static readonly RunnerSettings Empty = new(null, new Dictionary<string, string>());
}

/// <summary>
/// Who schedules each replication while the move to a runner is made one replication at a time (plan 28c E12, ED6): <c>runner-owners.json</c> beside Sync's state. <c>old</c> (the default) is this API's scheduler and workers; <c>observe</c> is
/// the same, with the runner recording what it would have queued; <c>runner</c> is the runner's drain, and this API starts no worker for the replication and queues nothing for it. The file is read at every use, so a change takes effect
/// on a running API at once. A file that does not read, or an owner that is none of the three, is refused naming the file.
/// </summary>
public sealed class RunnerOwnerFile(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _gate = new();

    public string Path { get; } = path;

    public RunnerSettings Read()
    {
        lock (_gate)
        {
            return ReadLocked();
        }
    }

    private RunnerSettings ReadLocked()
    {
        if (!File.Exists(Path))
            return RunnerSettings.Empty;
        try
        {
            var read = JsonSerializer.Deserialize<RunnerSettings>(File.ReadAllText(Path), Json) ?? RunnerSettings.Empty;
            foreach (var (replication, owner) in read.Owners)
                if (!TaskOwners.IsOwner(owner))
                    throw new InvalidOperationException($"{Path}: the owner of '{replication}' is `{owner}`, which is none of {string.Join(", ", TaskOwners.All)}.");
            return read with { Owners = read.Owners ?? new Dictionary<string, string>() };
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{Path}: {ex.Message}", ex);
        }
    }

    public string OwnerOf(string replication) => Read().Owners.TryGetValue(replication, out var owner) ? owner : TaskOwners.Old;

    public void SetOwner(string replication, string owner)
    {
        if (!TaskOwners.IsOwner(owner))
            throw new ArgumentException($"'{owner}' is not an owner ({string.Join(", ", TaskOwners.All)}).", nameof(owner));
        lock (_gate)
        {
            var current = ReadLocked();
            var owners = new Dictionary<string, string>(current.Owners, StringComparer.Ordinal);
            if (owner == TaskOwners.Old) owners.Remove(replication);
            else owners[replication] = owner;
            Write(current with { Owners = owners });
        }
    }

    public void SetLink(RunnerLink? link)
    {
        lock (_gate)
        {
            Write(ReadLocked() with { Link = link });
        }
    }

    private void Write(RunnerSettings settings)
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temp = Path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, Json));
        File.Move(temp, Path, overwrite: true);
    }
}
