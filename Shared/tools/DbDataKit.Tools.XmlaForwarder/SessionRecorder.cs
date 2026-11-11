using System.Text;
using System.Text.Json;

namespace DbDataKit.Tools.XmlaForwarder;

/// <summary>Which way a chunk of bytes travelled.</summary>
public enum Direction { Client, Server }

/// <summary>
/// Writes a recording into a folder. For each connection <c>n</c> (numbered from 1 in the order they were accepted): <c>conn-000n.client.bin</c> and
/// <c>conn-000n.server.bin</c> hold every byte each side sent, exactly as it crossed the forwarder; when the connection closes, <c>conn-000n.client.txt</c> and
/// <c>conn-000n.server.txt</c> list the readable text found in them (<see cref="StreamText"/>). <c>session.jsonl</c> has one line per event (a connection
/// opened or closed, a chunk of bytes with its time, direction, offset in the stream file and length), so the order of requests and responses can be rebuilt.
/// The bytes are never changed, and nothing here is filtered: a recording holds whatever the clients sent, passwords and tokens included.
/// </summary>
public sealed class SessionRecorder : IDisposable
{
    private readonly string _folder;
    private readonly object _gate = new();
    private readonly StreamWriter _index;
    private readonly Dictionary<(int, Direction), FileStream> _streams = [];
    private readonly Dictionary<(int, Direction), long> _offsets = [];
    private int _connections;

    public SessionRecorder(string folder)
    {
        _folder = Directory.CreateDirectory(folder).FullName;
        _index = new StreamWriter(new FileStream(Path.Combine(_folder, "session.jsonl"), FileMode.Create, FileAccess.Write, FileShare.Read), new UTF8Encoding(false)) { AutoFlush = true };
    }

    public string Folder => _folder;

    /// <summary>How many connections have been opened so far.</summary>
    public int Connections { get { lock (_gate) return _connections; } }

    /// <summary>The names of the files a connection's stream is written to.</summary>
    public static string StreamFile(int connection, Direction direction) => $"conn-{connection:D4}.{direction.ToString().ToLowerInvariant()}.bin";

    public int Open(string client, string target)
    {
        lock (_gate)
        {
            var id = ++_connections;
            foreach (var d in new[] { Direction.Client, Direction.Server })
            {
                _streams[(id, d)] = new FileStream(Path.Combine(_folder, StreamFile(id, d)), FileMode.Create, FileAccess.Write, FileShare.Read);
                _offsets[(id, d)] = 0;
            }
            Line(new Dictionary<string, object?> { ["event"] = "open", ["conn"] = id, ["client"] = client, ["target"] = target });
            return id;
        }
    }

    public void Chunk(int connection, Direction direction, ReadOnlySpan<byte> bytes)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue((connection, direction), out var file)) return;
            var offset = _offsets[(connection, direction)];
            file.Write(bytes);
            file.Flush();
            _offsets[(connection, direction)] = offset + bytes.Length;
            Line(new Dictionary<string, object?> { ["event"] = "data", ["conn"] = connection, ["dir"] = direction.ToString().ToLowerInvariant(), ["offset"] = offset, ["length"] = bytes.Length });
        }
    }

    /// <summary>Closes a connection's files and writes its text views.</summary>
    public void Close(int connection, string? error = null)
    {
        lock (_gate)
        {
            foreach (var d in new[] { Direction.Client, Direction.Server })
            {
                if (!_streams.Remove((connection, d), out var file)) return;
                file.Dispose();
                var bin = Path.Combine(_folder, StreamFile(connection, d));
                File.WriteAllText(Path.ChangeExtension(bin, ".txt"), StreamText.Describe(File.ReadAllBytes(bin)), new UTF8Encoding(false));
            }
            Line(new Dictionary<string, object?> { ["event"] = "close", ["conn"] = connection, ["error"] = error });
        }
    }

    private void Line(Dictionary<string, object?> fields)
    {
        var all = new Dictionary<string, object?> { ["t"] = DateTime.UtcNow.ToString("O") };
        foreach (var (k, v) in fields) all[k] = v;
        _index.WriteLine(JsonSerializer.Serialize(all));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var id in _streams.Keys.Select(k => k.Item1).Distinct().ToList()) Close(id, "the recording ended");
            _index.Dispose();
        }
    }
}
