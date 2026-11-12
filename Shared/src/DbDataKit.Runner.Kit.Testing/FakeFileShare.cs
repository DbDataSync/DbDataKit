using System.Security.Cryptography;
using System.Text;

namespace DbDataKit.Runner.Kit.Testing;

/// <summary>A file on a <see cref="FakeFileShare"/>, as a listing shows it.</summary>
/// <param name="Path">The path from the share's root, with <c>/</c> and a leading one (<c>/drop/2026-11-07.csv</c>).</param>
/// <param name="Size">The bytes held so far.</param>
/// <param name="Modified">When it was last written.</param>
/// <param name="Complete">False while a writer has not finished it (<see cref="FakeFileShare.Begin"/>): a job must not pick it up.</param>
public sealed record FakeFile(string Path, long Size, DateTimeOffset Modified, bool Complete)
{
    /// <summary>The content hash a job gives <c>Files.Claim</c>: SHA-256 as lower-case hex, the same for the same bytes whatever the name.</summary>
    public string? Hash { get; init; }
}

/// <summary>
/// A file share in memory for a job's test (plan 27a IX15): what a job sees of a folder an outside system fills (list, read, write, move, delete) with the things that go wrong on a real one, and no network, no server, no disk. The shape is the small common part of SFTP, FTPS, SMB
/// and object storage (plan 27a IX04 to IX06); when those clients exist a job takes its share as the slot's handle and this class is the test's stand-in for it. It is deliberately not their interface: it adds the test's own controls (a clock, files that arrive half written, failures on demand, a
/// log of what the job did).
/// <list type="bullet">
/// <item>Paths use <c>/</c>; a file's folder is implied (no folders to make or remove).</item>
/// <item><see cref="Begin"/> starts a file a writer has not finished: it is listed with <see cref="FakeFile.Complete"/> false, reading it fails, and <see cref="Finish"/> makes it whole. <see cref="WriteAsync(string, byte[], CancellationToken)"/> is atomic (written aside, then in place), as a careful sender does.</item>
/// <item><see cref="FailNext"/> makes the next calls of one operation on a path (or any) throw: an <see cref="IOException"/> is a temporary failure for the runner's retry, a <see cref="FileNotFoundException"/> a permanent one.</item>
/// <item>Time is the share's <see cref="TimeProvider"/>, so a modification time is whatever the test says.</item>
/// </list>
/// </summary>
public sealed class FakeFileShare
{
    private sealed class Entry(byte[] content, DateTimeOffset modified, bool complete)
    {
        public byte[] Content { get; set; } = content;

        public DateTimeOffset Modified { get; set; } = modified;

        public bool Complete { get; set; } = complete;
    }

    private sealed class Failure(string operation, string? path, Exception exception, int remaining)
    {
        public string Operation { get; } = operation;

        public string? Path { get; } = path;

        public Exception Exception { get; } = exception;

        public int Remaining { get; set; } = remaining;
    }

    private readonly object _gate = new();
    private readonly SortedDictionary<string, Entry> _files = new(StringComparer.Ordinal);
    private readonly List<string> _log = [];
    private readonly List<Failure> _failures = [];
    private readonly TimeProvider _time;

    public FakeFileShare(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    /// <summary>What the job did, one line per call: <c>list /drop</c>, <c>read /drop/a.csv</c>, <c>write /out/a.csv</c>, <c>delete ...</c>, <c>move a to b</c>, in order, whether it succeeded or not.</summary>
    public IReadOnlyList<string> Log
    {
        get { lock (_gate) return [.. _log]; }
    }

    /// <summary>How many times the job did <paramref name="operation"/> (<c>list</c>, <c>read</c>, <c>write</c>, <c>delete</c>, <c>move</c>).</summary>
    public int Calls(string operation)
    {
        lock (_gate) return _log.Count(l => l.StartsWith(operation + " ", StringComparison.Ordinal));
    }

    // ---- the test puts files there

    /// <summary>A finished file, as an outside system left it. <paramref name="modified"/> defaults to the share's time.</summary>
    public FakeFileShare Put(string path, byte[] content, DateTimeOffset? modified = null)
    {
        lock (_gate) _files[Normal(path)] = new Entry(content, modified ?? _time.GetUtcNow(), complete: true);
        return this;
    }

    public FakeFileShare Put(string path, string text, DateTimeOffset? modified = null) => Put(path, Encoding.UTF8.GetBytes(text), modified);

    /// <summary>A file a writer has started and not finished: listed with <see cref="FakeFile.Complete"/> false, not readable.</summary>
    public FakeFileShare Begin(string path, byte[] partial, DateTimeOffset? modified = null)
    {
        lock (_gate) _files[Normal(path)] = new Entry(partial, modified ?? _time.GetUtcNow(), complete: false);
        return this;
    }

    /// <summary>The writer finished: the file holds <paramref name="content"/> (its bytes so far when null) and is whole.</summary>
    public FakeFileShare Finish(string path, byte[]? content = null)
    {
        lock (_gate)
        {
            var entry = Find(Normal(path));
            if (content is not null) entry.Content = content;
            entry.Complete = true;
            entry.Modified = _time.GetUtcNow();
        }

        return this;
    }

    /// <summary>The next <paramref name="times"/> calls of <paramref name="operation"/> (<c>list</c>, <c>read</c>, <c>write</c>, <c>delete</c>, <c>move</c>) on <paramref name="path"/> (any path when null) throw <paramref name="exception"/>.</summary>
    public FakeFileShare FailNext(string operation, Exception exception, int times = 1, string? path = null)
    {
        lock (_gate) _failures.Add(new Failure(operation, path is null ? null : Normal(path), exception, times));
        return this;
    }

    // ---- what the job does

    /// <summary>The files directly in <paramref name="folder"/> (or below it with <paramref name="recursive"/>), by path. Unfinished files are listed, marked.</summary>
    public Task<IReadOnlyList<FakeFile>> ListAsync(string folder, bool recursive = false, CancellationToken ct = default)
    {
        lock (_gate)
        {
            Record("list", Normal(folder));
            Fail("list", Normal(folder));
            var prefix = Normal(folder).TrimEnd('/') + "/";
            var found = _files
                .Where(f => f.Key.StartsWith(prefix, StringComparison.Ordinal) && (recursive || !f.Key[prefix.Length..].Contains('/')))
                .Select(f => new FakeFile(f.Key, f.Value.Content.LongLength, f.Value.Modified, f.Value.Complete) { Hash = f.Value.Complete ? Hash(f.Value.Content) : null })
                .ToList();
            return Task.FromResult<IReadOnlyList<FakeFile>>(found);
        }
    }

    public bool Exists(string path)
    {
        lock (_gate) return _files.ContainsKey(Normal(path));
    }

    /// <summary>The file's bytes. A missing file is a <see cref="FileNotFoundException"/> (permanent); an unfinished one an <see cref="IOException"/> (temporary: it will be whole later).</summary>
    public Task<byte[]> ReadAsync(string path, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var name = Normal(path);
            Record("read", name);
            Fail("read", name);
            var entry = Find(name);
            if (!entry.Complete) throw new IOException($"The file '{name}' is still being written.");
            return Task.FromResult(entry.Content.ToArray());
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct = default) => new MemoryStream(await ReadAsync(path, ct).ConfigureAwait(false), writable: false);

    /// <summary>Writes a file whole and in one step (never half visible).</summary>
    public Task WriteAsync(string path, byte[] content, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var name = Normal(path);
            Record("write", name);
            Fail("write", name);
            _files[name] = new Entry(content.ToArray(), _time.GetUtcNow(), complete: true);
            return Task.CompletedTask;
        }
    }

    public async Task WriteAsync(string path, Stream content, CancellationToken ct = default)
    {
        using var memory = new MemoryStream();
        await content.CopyToAsync(memory, ct).ConfigureAwait(false);
        await WriteAsync(path, memory.ToArray(), ct).ConfigureAwait(false);
    }

    public Task DeleteAsync(string path, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var name = Normal(path);
            Record("delete", name);
            Fail("delete", name);
            Find(name);
            _files.Remove(name);
            return Task.CompletedTask;
        }
    }

    /// <summary>Renames a file (an unfinished one cannot be moved). An existing target is replaced.</summary>
    public Task MoveAsync(string from, string to, CancellationToken ct = default)
    {
        lock (_gate)
        {
            var source = Normal(from);
            Record("move", $"{source} to {Normal(to)}");
            Fail("move", source);
            var entry = Find(source);
            if (!entry.Complete) throw new IOException($"The file '{source}' is still being written.");
            _files.Remove(source);
            _files[Normal(to)] = entry;
            return Task.CompletedTask;
        }
    }

    /// <summary>The bytes of a finished file for a test's assertions (no log line, no injected failure).</summary>
    public byte[] Content(string path)
    {
        lock (_gate) return Find(Normal(path)).Content.ToArray();
    }

    public string Text(string path) => Encoding.UTF8.GetString(Content(path));

    /// <summary>The paths of every file, finished or not.</summary>
    public IReadOnlyList<string> Paths
    {
        get { lock (_gate) return [.. _files.Keys]; }
    }

    public static string Hash(byte[] content) => Convert.ToHexStringLower(SHA256.HashData(content));

    // ---- internals (callers hold the lock)

    private void Record(string operation, string subject) => _log.Add($"{operation} {subject}");

    private void Fail(string operation, string path)
    {
        var failure = _failures.FirstOrDefault(f => f.Remaining > 0 && f.Operation == operation && (f.Path is null || f.Path == path));
        if (failure is null) return;
        failure.Remaining--;
        throw failure.Exception;
    }

    private Entry Find(string name) => _files.TryGetValue(name, out var entry) ? entry : throw new FileNotFoundException($"The file '{name}' does not exist on the share.", name);

    private static string Normal(string path)
    {
        var p = path.Replace('\\', '/');
        return p.StartsWith('/') ? p : "/" + p;
    }
}
