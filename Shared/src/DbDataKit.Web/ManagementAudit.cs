using System.Text;
using System.Text.Json;

namespace DbDataKit.Web.Trust;

/// <summary>
/// One change made through the management API: who (the user the console vouched for, and which console), what (the command and its target), and how it ended.
/// Never a value: a secret or a file's content has no place in an audit line.
/// </summary>
public sealed record AuditEntry(
    DateTimeOffset At, string ConsoleId, string ConsoleName, string UserId, string? UserName, bool ByConsole,
    string Command, string? Target, string Outcome, string TokenId);

public interface IAuditLog
{
    void Append(AuditEntry entry);

    /// <summary>The newest <paramref name="limit"/> entries, newest last.</summary>
    IReadOnlyList<AuditEntry> Read(int limit = 100);
}

/// <summary>An append-only file of JSON lines, one entry each; entries are written whole, one at a time.</summary>
public sealed class FileAuditLog(string path) : IAuditLog
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly object _gate = new();

    public void Append(AuditEntry entry)
    {
        var line = JsonSerializer.Serialize(entry, Json) + "\n";
        lock (_gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using var stream = new FileStream(path, options);
            stream.Write(Encoding.UTF8.GetBytes(line));
        }
    }

    public IReadOnlyList<AuditEntry> Read(int limit = 100)
    {
        lock (_gate)
        {
            if (!File.Exists(path)) return [];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var lines = new Queue<AuditEntry>();
            while (reader.ReadLine() is { Length: > 0 } line)
            {
                if (JsonSerializer.Deserialize<AuditEntry>(line, Json) is not { } entry) continue;
                lines.Enqueue(entry);
                if (lines.Count > limit) lines.Dequeue();
            }
            return [.. lines];
        }
    }
}

/// <summary>
/// The identity a change is recorded under, from the token that authorised it: the audit entry, and the name and email a configuration commit is
/// authored by, so the history of a config repository says who changed what and through which console (plan 13, W12).
/// </summary>
public static class ManagementIdentity
{
    public static AuditEntry Entry(ValidatedToken token, string command, string? target, string outcome, DateTimeOffset at) =>
        new(at, token.Client.Id, token.Client.Name, token.SubjectId, token.SubjectName, token.IsConsole, command, target, outcome, token.TokenId);

    /// <summary>The user's name, and an address that names the user and the console and cannot be mistaken for a real mailbox (<c>user-7@console-a.console.invalid</c>).</summary>
    public static (string Name, string Email) CommitAuthor(ValidatedToken token)
    {
        var name = Clean(string.IsNullOrWhiteSpace(token.SubjectName) ? token.SubjectId : token.SubjectName!);
        var via = token.IsConsole ? " (console " + Clean(token.Client.Name) + ")" : "";
        return (name + via, $"{Address(token.SubjectId)}@{Address(token.Client.Id)}.console.invalid");
    }

    /// <summary>A change made on the server's own machine, through the local channel, by the operating system's user.</summary>
    public static (string Name, string Email) LocalAuthor(string operatingSystemUser) =>
        (Clean(operatingSystemUser), $"{Address(operatingSystemUser)}@local.invalid");

    // git refuses angle brackets and line breaks in a name or an address, and an address with anything but these is not one
    private static string Clean(string text) => new(text.Where(c => !char.IsControl(c) && c is not ('<' or '>')).ToArray());

    private static string Address(string text)
    {
        var cleaned = new string(text.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '-').ToArray()).Trim('-');
        return cleaned.Length == 0 ? "unknown" : cleaned;
    }
}
