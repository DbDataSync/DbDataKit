using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DbDataKit.Web.Trust;

namespace DbDataConsole.Server;

/// <summary>A server that announced itself on the console's private port and waits for an administrator to confirm it. The code is the single-use code it made for this console; it is never shown.</summary>
public sealed record PendingInstance(string Product, string InstanceId, string Name, Uri Url, string Code, DateTimeOffset Expires, DateTimeOffset LastSeen);

/// <summary>
/// The servers waiting to be confirmed (plan 13a, L03): what the announcements on the private port amount to. Nothing here is trusted: an announcement is a claim made by anything on the network the console is attached to, so it only
/// puts a line in a list an administrator reads, and the console contacts nothing until one is confirmed. The list is a file (<c>pending.json</c> next to the registry) so the command line sees what the running console heard; it holds codes that expire in
/// minutes and work once, and is kept like the console's other files. An entry whose code has lapsed is dropped; a server still waiting announces again every half minute.
/// </summary>
public sealed partial class PendingInstances(string path, TimeProvider? clock = null)
{
    public const int MaxEntries = 100;
    private static readonly string[] Products = ["dbdatabuild", "dbdatasync", "dbdataproxy", "dbdatastore"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _clock = clock ?? TimeProvider.System;
    private readonly object _gate = new();

    /// <summary>Records an announcement. Returns why it was not taken, or null.</summary>
    public string? Announce(Announcement announcement)
    {
        if (!Products.Contains(announcement.Product)) return "the product is not one this console manages";
        if (announcement.InstanceId is not { } id || !IdPattern().IsMatch(id)) return "the instance id is missing or not valid";
        if (string.IsNullOrWhiteSpace(announcement.Name) || announcement.Name.Length > 100 || announcement.Name.Any(char.IsControl)) return "the name is missing, too long or has control characters";
        if (!Uri.TryCreate(announcement.Url, UriKind.Absolute, out var url) || url.Scheme != Uri.UriSchemeHttps || url.Host.Length == 0 || url.AbsoluteUri.Length > 300) return "the address is not an https address";
        if (announcement.Code is not { Length: > 0 and <= 40 }) return "the code is missing";
        var now = _clock.GetUtcNow();
        if (announcement.Expires <= now) return "the code has already expired";

        lock (_gate)
        {
            var list = Read();
            list.RemoveAll(p => p.InstanceId == id);
            if (list.Count >= MaxEntries) return "too many servers are waiting";
            list.Add(new PendingInstance(announcement.Product, id, announcement.Name, url, announcement.Code, announcement.Expires, now));
            Write(list);
        }
        return null;
    }

    /// <summary>The servers waiting, oldest first.</summary>
    public IReadOnlyList<PendingInstance> All()
    {
        lock (_gate) return [.. Read().OrderBy(p => p.LastSeen)];
    }

    /// <summary>The waiting server with this instance id or this name; null when there is none or the name is not unique.</summary>
    public PendingInstance? Find(string idOrName)
    {
        var all = All();
        return all.FirstOrDefault(p => p.InstanceId == idOrName)
            ?? (all.Where(p => string.Equals(p.Name, idOrName, StringComparison.OrdinalIgnoreCase)).ToList() is [var only] ? only : null);
    }

    public bool Remove(string instanceId)
    {
        lock (_gate)
        {
            var list = Read();
            if (list.RemoveAll(p => p.InstanceId == instanceId) == 0) return false;
            Write(list);
            return true;
        }
    }

    private List<PendingInstance> Read()
    {
        if (!File.Exists(path)) return [];
        List<PendingInstance>? list;
        try { list = JsonSerializer.Deserialize<List<PendingInstance>>(File.ReadAllText(path), Json); }
        catch (JsonException) { return []; } // a damaged file holds nothing; the servers announce again
        var now = _clock.GetUtcNow();
        return list?.Where(p => p.Expires > now).ToList() ?? [];
    }

    private void Write(List<PendingInstance> list)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temp = path + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, Json), new UTF8Encoding(false));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        File.Move(temp, path, overwrite: true);
    }

    [GeneratedRegex("^[A-Za-z0-9._:-]{1,64}$")]
    private static partial Regex IdPattern();
}
