using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DbDataKit.Transfer;

/// <summary>How much of a statement the log keeps. The default keeps no text and no values.</summary>
public enum StatementLogDetail
{
    /// <summary>Statement hash, parameter names, canonical types and salted value hashes. No text, no values.</summary>
    HashAndTypes,
    /// <summary>Adds the statement text (identifiers only; statements are parameterised).</summary>
    WithText,
    /// <summary>Adds parameter values (redacted). Opt-in: values are data.</summary>
    WithValues,
}

/// <summary>One parameter as logged. <see cref="Value"/> is null unless the gate runs at <see cref="StatementLogDetail.WithValues"/>.</summary>
public sealed record LoggedParameter(string Name, string Type, string ValueHash, string? Value);

public sealed record StatementLogEntry(
    Guid RunId, int Ordinal, DateTime Utc, string Command, string Phase, string StepId, string Kind, string StatementHash,
    string? Text, IReadOnlyList<LoggedParameter>? Parameters, string? Outcome);

public interface IStatementLog
{
    /// <summary>Appends one entry durably. Throws IOException/UnauthorizedAccessException when it cannot; the gate then refuses to execute the statement.</summary>
    void Append(StatementLogEntry entry);
}

/// <summary>For tests and dry runs that must not touch the disk.</summary>
public sealed class MemoryStatementLog : IStatementLog
{
    public List<StatementLogEntry> Entries { get; } = [];
    public void Append(StatementLogEntry entry) { lock (Entries) Entries.Add(entry); }
}

/// <summary>
/// Append-only JSON lines, one file per run, flushed to disk before <see cref="Append"/> returns. On Unix the file is created
/// owner-only (0600); on Windows it inherits the directory's ACL, so point it at a restricted directory (see README).
/// </summary>
public sealed class FileStatementLog : IStatementLog, IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly FileStream _stream;

    public string Path { get; }

    public FileStatementLog(string directory, string command, Guid runId)
    {
        Directory.CreateDirectory(directory);
        var safe = string.Concat(command.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '-'));
        Path = System.IO.Path.Combine(directory, $"{DateTime.UtcNow.ToString("yyyyMMddTHHmmss", CultureInfo.InvariantCulture)}-{safe}-{runId:N}.jsonl");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Read };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        _stream = new FileStream(Path, options);
    }

    public void Append(StatementLogEntry e)
    {
        var line = JsonSerializer.Serialize(new
        {
            run = e.RunId, n = e.Ordinal, utc = e.Utc.ToString("O", CultureInfo.InvariantCulture), command = e.Command, phase = e.Phase,
            step = e.StepId, kind = e.Kind, hash = e.StatementHash, text = e.Text,
            parameters = e.Parameters?.Select(p => new { name = p.Name, type = p.Type, hash = p.ValueHash, value = p.Value }), outcome = e.Outcome,
        }, Json);
        var bytes = Encoding.UTF8.GetBytes(line + "\n");
        lock (_stream)
        {
            _stream.Write(bytes);
            _stream.Flush(flushToDisk: true);
        }
    }

    public void Dispose() => _stream.Dispose();
}
