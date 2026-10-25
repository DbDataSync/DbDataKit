using System.Text;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Web.Trust;

/// <summary>
/// A list of flow mappings under one key of a small YAML file (<c>pairedClients</c> in a server's <c>server.yaml</c>, <c>instances</c> in a console's <c>instances.yaml</c>), one entry per line, each with an <c>id</c>.
/// The file's bytes are compared on every read, so a change made by another process is seen on the next call and the file is parsed only when it changed. Edits are surgical (the rest of the file, comments
/// and all, is untouched) and run under a lock other processes honour.
/// </summary>
public sealed class YamlListFile(string path, string key)
{
    private readonly object _gate = new();
    private byte[]? _seen;
    private IReadOnlyList<YamlMapping> _entries = [];
    private IReadOnlyList<string> _problems = [];

    public string Path { get; } = path;

    /// <summary>What was wrong with the file or its entries the last time it was read.</summary>
    public IReadOnlyList<string> Problems { get { Load(); return _problems; } }

    public IReadOnlyList<YamlMapping> Entries() { Load(); return _entries; }

    /// <summary>Adds the entry, or replaces the one with the same id.</summary>
    public void Upsert(string id, string flowMapping) =>
        Edit(text =>
        {
            var at = IndexOf(text, id);
            if (at >= 0) text = Apply(YamlEditor.Remove(text, $"{key}[{at}]", Path));
            if (!HasKey(text)) return text.TrimEnd('\r', '\n') + NewLine(text) + key + ":" + NewLine(text) + "  - " + flowMapping + NewLine(text);
            return Apply(YamlEditor.Insert(text, key, YamlValue.Raw(flowMapping), file: Path));
        });

    /// <summary>Removes the entry with this id; false when there was none.</summary>
    public bool Remove(string id)
    {
        var removed = false;
        Edit(text =>
        {
            var at = IndexOf(text, id);
            if (at < 0) return text;
            removed = true;
            return Apply(YamlEditor.Remove(text, $"{key}[{at}]", Path));
        });
        return removed;
    }

    private static string Apply(EditResult result) =>
        result.Text ?? throw new InvalidOperationException($"the file could not be edited: {result.Problem?.Message}");

    private static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    private bool HasKey(string text) => StrictYamlReader.Read(text, "", []) is YamlMapping map && map.Get(key) is not null;

    private int IndexOf(string text, string id)
    {
        if (StrictYamlReader.Read(text, Path, []) is not YamlMapping map || map.Get(key) is not YamlSequence list) return -1;
        for (var i = 0; i < list.Items.Count; i++)
            if (list.Items[i] is YamlMapping item && item.Get("id") is YamlScalar scalar && scalar.Value == id) return i;
        return -1;
    }

    private void Edit(Func<string, string> change)
    {
        var directory = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        Directory.CreateDirectory(directory);
        using var gate = FileLock.Take(Path + ".lock");
        var text = File.Exists(Path) ? ReadText() : "schemaVersion: 1\n";
        var changed = change(text);
        if (changed == text) return;
        var temp = Path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, changed, new UTF8Encoding(false));
        File.Move(temp, Path, overwrite: true);
    }

    private string ReadText()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private void Load()
    {
        byte[] bytes;
        try { bytes = File.Exists(Path) ? ReadBytes() : []; }
        catch (IOException) { return; } // being replaced right now: the last good list stands until the next call
        lock (_gate)
        {
            if (_seen is not null && bytes.AsSpan().SequenceEqual(_seen)) return;
            var problems = new List<string>();
            var diagnostics = new List<ConfigDiagnostic>();
            var root = StrictYamlReader.Read(Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF'), Path, diagnostics);
            foreach (var d in diagnostics.Where(d => d.Severity == ConfigSeverity.Error)) problems.Add($"{Path}:{d.Line}: {d.Message}");
            var entries = new List<YamlMapping>();
            if (root is YamlMapping map && map.Get(key) is YamlSequence list)
                foreach (var item in list.Items)
                {
                    if (item is YamlMapping entry) entries.Add(entry);
                    else problems.Add($"{Path}:{item.Line}: an entry of {key} is not a mapping");
                }
            (_entries, _problems, _seen) = (entries, problems, bytes);
        }
    }

    private byte[] ReadBytes()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}

/// <summary>A lock between processes on one file: held by opening it with no sharing (the file goes when it is released), with a wait for whoever holds it. The operating system lets go of it if the holder dies.</summary>
internal sealed class FileLock : IDisposable
{
    private readonly FileStream _stream;

    private FileLock(FileStream stream) => _stream = stream;

    public static FileLock Take(string path, TimeSpan? wait = null)
    {
        var until = DateTime.UtcNow + (wait ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            try { return new FileLock(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose)); }
            catch (IOException) when (DateTime.UtcNow < until) { Thread.Sleep(20); }
            catch (UnauthorizedAccessException) when (DateTime.UtcNow < until) { Thread.Sleep(20); }
        }
    }

    public void Dispose() => _stream.Dispose();
}
