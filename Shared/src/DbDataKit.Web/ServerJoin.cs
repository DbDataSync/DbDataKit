using System.Net;
using System.Text.Json;
using DbDataKit.State;

namespace DbDataKit.Web.Trust;

/// <summary>A join the server's join step could not make: <see cref="Console"/> is the console's address from the join request, <see cref="Answer"/> what it said when it refused (null when it did not answer).</summary>
public sealed class ServerJoinException(string console, string message, string? answer, Exception inner) : Exception(message, inner)
{
    public string Console { get; } = console;

    public string? Answer { get; } = answer;
}

/// <summary>
/// The consoles a server joined, kept in its group's settings (the <c>setting</c> table of the <c>trust</c> module): each console's address and the
/// thumbprint of its certificate, as the join command named them. The console's signing key and ceiling are the paired client the join made.
/// </summary>
public sealed class JoinedConsoles(IGroupSettings settings)
{
    private const string Prefix = "joined-console:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A console as kept: its address, the certificate it presented at the join and when it joined.</summary>
    public sealed record Pin(string ConsoleId, Uri Url, string CertificateFingerprint, DateTimeOffset JoinedAt);

    private sealed record Stored(string? Url, string? Thumbprint, DateTimeOffset JoinedAt);

    public void Save(JoinedConsole console)
    {
        var name = Prefix + console.ConsoleId;
        var value = JsonSerializer.Serialize(new Stored(JoinRequestFile.Text(console.Url), Thumbprints.Format(console.CertificateFingerprint), console.JoinedAt), Json);
        // another instance of the group may write at the same moment: the last join's pin is the group's
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var was = settings.Get(name);
            if (settings.Put(name, value, was) == value) return;
        }
    }

    public Pin? Find(string consoleId)
    {
        if (settings.Get(Prefix + consoleId) is not { } text) return null;
        try
        {
            return JsonSerializer.Deserialize<Stored>(text, Json) is { Url: { } url, Thumbprint: { } thumbprint } stored
                && Uri.TryCreate(url, UriKind.Absolute, out var uri) && Thumbprints.Parse(thumbprint) is { } fingerprint
                ? new Pin(consoleId, uri, fingerprint, stored.JoinedAt)
                : null;
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The join step of a server's start: when <c>join-request.yaml</c> is in its data folder, the server joins the console it names (<see cref="JoinClient"/>),
/// keeps the console as a paired client and its certificate's thumbprint (<see cref="JoinedConsoles"/>), and deletes the request. The server's HTTPS listener
/// is already up, so it answers the console's callback (<see cref="JoinCallbackEndpoint"/>) during the join. A join that is refused or cannot be made
/// leaves the request in place and throws <see cref="ServerJoinException"/>, so the start fails naming the console and its answer.
/// Design: docs/install.md#pairing
/// </summary>
public static class ServerJoin
{
    /// <summary>Where the join request of a server whose data folder is <paramref name="dataFolder"/> is.</summary>
    public static string RequestPath(string dataFolder) => Path.Combine(dataFolder, JoinRequestFile.FileName);

    /// <summary>
    /// The address the console calls a server back at, from the address it listens on: a listener on every address (<c>0.0.0.0</c>, <c>[::]</c>,
    /// <c>*</c>, <c>+</c>) is called at this machine's name; any other is called as it is.
    /// </summary>
    public static Uri AddressFor(Uri listen) =>
        listen.Host is "0.0.0.0" or "[::]" or "::" or "*" or "+" ? new UriBuilder(listen) { Host = Dns.GetHostName(), Path = "" }.Uri : new Uri(listen.GetLeftPart(UriPartial.Authority));

    /// <summary>
    /// Joins the console when a join request waits; null when there is none. <paramref name="self"/> is asked only when there is a request, so a server
    /// that has none does not make a key for nothing. <c>keep</c> keeps what the console handed over about the server's group (its key, its ports) before the console
    /// becomes a client; when it fails, the token is spent, so the request is deleted and the join fails naming what could not be kept.
    /// </summary>
    /// <exception cref="JoinRequestFileException">The request cannot be used; the message names the file.</exception>
    /// <exception cref="ServerJoinException">The console refused the join, or could not be reached.</exception>
    public static async Task<JoinedConsole?> RunAsync(string dataFolder, Func<JoiningServer> self, PendingJoins pending, IPairedClientStore clients, JoinedConsoles consoles,
        CancellationToken ct = default, TimeSpan? timeout = null, Action<JoinedConsole>? keep = null)
    {
        var path = RequestPath(dataFolder);
        if (JoinRequestFile.Read(path) is not { } file) return null;
        JoinedConsole joined;
        var console = JoinRequestFile.Text(file.Console);
        try { joined = await JoinClient.JoinAsync(file, self(), pending, ct, timeout: timeout).ConfigureAwait(false); }
        catch (JoinFailedException ex) { throw new ServerJoinException(console, ex.Message, ex.Answer, ex); }
        try { keep?.Invoke(joined); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            JoinRequestFile.Delete(path);
            throw new ServerJoinException(console, $"{console} accepted the join, but this server could not keep what it handed over about its group: {ex.Message} " +
                "The join token is used; make a new command on the console once this is fixed.", null, ex);
        }
        clients.Upsert(joined.AsPairedClient());
        consoles.Save(joined);
        JoinRequestFile.Delete(path);
        return joined;
    }
}
