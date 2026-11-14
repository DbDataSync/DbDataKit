using System.Text.Json;
using System.Text.Json.Serialization;

namespace DbDataSync.Api.Runner;

/// <summary>Where the runner is that runs this server's replications: the paired runner (name or address, as <c>package publish --server</c> takes it), the channel and the package its replications are published to, and the directory of the client the pairing is kept in.</summary>
public sealed record RunnerLink(string Server, string Channel, string Package, string? ClientDirectory = null);

/// <summary>
/// The runner this server's replications run on (plan 28c E22, owner decision S2 of 2026-11-14): <c>runner-link.json</c> beside Sync's state. The Runner is Sync's only scheduler, so there is no
/// per-replication owner any more: every replication is the runner's, and a server with no link runs nothing (each action that would start work says so). The file is read at every use, so a link set
/// while the API runs takes effect at once.
/// <para>
/// A clean break: the file of the hand-over (<c>runner-owners.json</c>, which also held an owner per replication) is refused naming it, never read, so that a replication an operator had kept on the
/// old scheduler is not silently moved; the message says to delete it, set the runner again and publish once.
/// </para>
/// </summary>
public sealed class RunnerLinkFile(string path)
{
    public const string FileName = "runner-link.json";

    /// <summary>The file of the owner setting that E22 removed.</summary>
    public const string OldFileName = "runner-owners.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly object _gate = new();

    public string Path { get; } = path;

    private sealed record Content(RunnerLink? Link);

    /// <summary>The runner of the file, or null while none is set.</summary>
    public RunnerLink? Read()
    {
        lock (_gate)
        {
            return ReadLocked();
        }
    }

    private RunnerLink? ReadLocked()
    {
        RefuseOldFile();
        if (!File.Exists(Path))
            return null;
        try
        {
            return (JsonSerializer.Deserialize<Content>(File.ReadAllText(Path), Json) ?? new Content(null)).Link;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"{Path}: {ex.Message}", ex);
        }
    }

    private void RefuseOldFile()
    {
        var directory = System.IO.Path.GetDirectoryName(Path);
        var old = System.IO.Path.Combine(string.IsNullOrEmpty(directory) ? "." : directory, OldFileName);
        if (File.Exists(old))
            throw new InvalidOperationException(
                $"{old} is the file of the per-replication owner setting, which is gone: the Runner is Sync's only scheduler (plan 28c E22). It is not read. Delete the file, " +
                "set the runner again (PUT /api/runner, or the Runner card of a replication) and publish the replication set to it once (`dbdatasync package publish --to runner`).");
    }

    public void SetLink(RunnerLink? link)
    {
        lock (_gate)
        {
            RefuseOldFile();
            var directory = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            var temp = Path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(new Content(link), Json));
            File.Move(temp, Path, overwrite: true);
        }
    }
}
