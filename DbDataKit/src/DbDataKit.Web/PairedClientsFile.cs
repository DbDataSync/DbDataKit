using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using DbDataKit.Config.Yaml;

namespace DbDataKit.Web.Trust;

/// <summary>
/// The paired clients in the <c>pairedClients</c> list of <c>server.yaml</c> (plan 13, W12): one flow mapping per client, so the list stays one line each and a person can read it. The file is read again on
/// every <see cref="Find"/> (it is small, and the bytes are compared with the last ones read so it is parsed only when it changed), so a pairing or an <c>unpair</c> made by the command line in another process
/// takes effect on the server's next call. A damaged entry is never trusted: it is skipped and named in <see cref="Problems"/>. Writes go through the surgical YAML editor, so the rest of the file, comments and all, is untouched.
/// </summary>
public sealed class PairedClientsFile(string path, TimeProvider? clock = null) : IPairedClients
{
    public const string Key = "pairedClients";

    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();
    private byte[]? _seen;
    private IReadOnlyList<PairedClient> _clients = [];
    private IReadOnlyList<string> _problems = [];

    public string Path { get; } = path;

    /// <summary>What was wrong with the entries skipped the last time the file was read.</summary>
    public IReadOnlyList<string> Problems { get { Load(); return _problems; } }

    public PairedClient? Find(string clientId) => All().FirstOrDefault(c => string.Equals(c.Id, clientId, StringComparison.Ordinal));

    public IReadOnlyList<PairedClient> All()
    {
        Load();
        return _clients;
    }

    /// <summary>Adds the client, or replaces the entry with the same id (pairing again with a new key).</summary>
    public void Upsert(PairedClient client)
    {
        var entry = Render(client with { PairedAt = client.PairedAt ?? _clock.GetUtcNow() });
        Edit(text =>
        {
            var at = IndexOf(text, client.Id);
            if (at >= 0) text = Apply(YamlEditor.Remove(text, $"{Key}[{at}]", Path));
            if (!HasKey(text)) return text.TrimEnd('\r', '\n') + NewLine(text) + Key + ":" + NewLine(text) + "  - " + entry + NewLine(text);
            return Apply(YamlEditor.Insert(text, Key, YamlValue.Raw(entry), file: Path));
        });
    }

    /// <summary>Removes the client with this id; false when there was none.</summary>
    public bool Remove(string clientId)
    {
        var removed = false;
        Edit(text =>
        {
            var at = IndexOf(text, clientId);
            if (at < 0) return text;
            removed = true;
            return Apply(YamlEditor.Remove(text, $"{Key}[{at}]", Path));
        });
        return removed;
    }

    private static string Apply(EditResult result) =>
        result.Text ?? throw new InvalidOperationException($"server.yaml could not be edited: {result.Problem?.Message}");

    private static string NewLine(string text) => text.Contains("\r\n") ? "\r\n" : "\n";

    private static bool HasKey(string text) => StrictYamlReader.Read(text, "", []) is YamlMapping map && map.Get(Key) is not null;

    private int IndexOf(string text, string id)
    {
        if (StrictYamlReader.Read(text, Path, []) is not YamlMapping { } map || map.Get(Key) is not YamlSequence list) return -1;
        for (var i = 0; i < list.Items.Count; i++)
            if (list.Items[i] is YamlMapping item && item.Get("id") is YamlScalar scalar && scalar.Value == id) return i;
        return -1;
    }

    private static string Render(PairedClient c) =>
        $"{{id: {YamlText.FlowScalar(c.Id)}, name: {YamlText.FlowScalar(c.Name)}, kind: {c.Kind.ToString().ToLowerInvariant()}, publicKey: {YamlText.FlowScalar(Base64Url.Encode(c.PublicKey))}, " +
        $"ceiling: {YamlText.FlowList(ServerRoles.Names(c.Ceiling))}, paired: {YamlText.FlowScalar(c.PairedAt!.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture))}}}";

    /// <summary>Reads, edits and replaces the file while holding a lock other processes honour, so two pairings (or a pairing and an unpair) do not lose each other's change.</summary>
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
            (_clients, _problems) = Parse(Encoding.UTF8.GetString(bytes), Path);
            _seen = bytes;
        }
    }

    private byte[] ReadBytes()
    {
        using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static (IReadOnlyList<PairedClient>, IReadOnlyList<string>) Parse(string text, string file)
    {
        var clients = new List<PairedClient>();
        var problems = new List<string>();
        var diagnostics = new List<ConfigDiagnostic>();
        var root = StrictYamlReader.Read(text.TrimStart('﻿'), file, diagnostics);
        foreach (var d in diagnostics.Where(d => d.Severity == ConfigSeverity.Error)) problems.Add($"{file}:{d.Line}: {d.Message}");
        if (root is not YamlMapping map || map.Get(Key) is not YamlSequence list) return (clients, problems);
        foreach (var item in list.Items)
        {
            if (item is not YamlMapping entry) { problems.Add($"{file}:{item.Line}: a paired client is not a mapping"); continue; }
            var (client, problem) = ParseOne(entry);
            if (client is null) problems.Add($"{file}:{item.Line}: paired client skipped: {problem}");
            else if (clients.Any(c => c.Id == client.Id)) problems.Add($"{file}:{item.Line}: paired client '{client.Id}' is listed twice; the first entry stands");
            else clients.Add(client);
        }
        return (clients, problems);
    }

    private static (PairedClient?, string) ParseOne(YamlMapping entry)
    {
        string? Text(string name) => entry.Get(name) is YamlScalar { Value.Length: > 0 } s ? s.Value : null;
        var id = Text("id");
        if (id is null) return (null, "no id");
        var key = Text("publicKey") is { } encoded ? Base64Url.Decode(encoded) : null;
        if (key is not { Length: ConsoleSigningKey.PublicKeyLength }) return (null, $"'{id}' has no valid publicKey");
        if (!Enum.TryParse<ClientKind>(Text("kind") ?? "console", ignoreCase: true, out var kind) || !Enum.IsDefined(kind)) return (null, $"'{id}' has an unknown kind");
        if (entry.Get("ceiling") is not YamlSequence roles || !ServerRoles.TryParse(roles.Items.OfType<YamlScalar>().Select(s => s.Value), out var ceiling) || ceiling == ServerRole.None)
            return (null, $"'{id}' has no valid ceiling");
        DateTimeOffset? paired = DateTimeOffset.TryParse(Text("paired"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var when) ? when : null;
        return (new PairedClient(id, Text("name") ?? id, key, ceiling, kind, paired), "");
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
