using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DbDataKit.Connections;
using DbDataKit.Connections.Files;
using DbDataKit.Runner.Triggers;
using DbDataKit.Secrets;
using TriggerFileEntry = DbDataKit.Runner.Triggers.FileEntry;

namespace DbDataKit.Runner.FileConnections;

/// <summary>
/// A file store of a connection (<see cref="IFileStore"/>: SFTP, FTPS, SMB, S3, Azure Blob) as the folder a file trigger watches (<see cref="IFileSource"/>, plan 27a IX07). The store is opened when the look starts and let go when it ends (<see cref="DisposeAsync"/>, which the trigger engine calls),
/// so a trigger holds no connection between looks. It only lists and reads: what happens to a file after the run is the task's. A listing leaves out names that mean "being written" (<c>.part</c>, <c>.tmp</c>, ...), and the engine leaves a file modified within its settle time for the next look; a file that is
/// seen twice (a re-sent file, the same content under another name) is recognised by the engine's ledger by its content, so it runs once. The server's modified time is compared with the runner's clock: a server whose clock is far ahead makes every file look "just written".
/// </summary>
public sealed class ConnectionFileSource(Func<CancellationToken, Task<IFileStore>> open) : IFileSource, IAsyncDisposable
{
    private IFileStore? _store;

    private async Task<IFileStore> StoreAsync(CancellationToken ct) => _store ??= await open(ct).ConfigureAwait(false);

    public async IAsyncEnumerable<TriggerFileEntry> ListAsync(string directory, string pattern, [EnumeratorCancellation] CancellationToken ct)
    {
        var store = await StoreAsync(ct).ConfigureAwait(false);
        var matcher = Glob(pattern);
        IReadOnlyList<DbDataKit.Connections.Files.FileEntry> entries;
        try { entries = await store.ListAsync("/" + directory.Trim('/'), false, ct).ConfigureAwait(false); }
        catch (ConnectionFailureException e) when (e.Failure.Kind == ConnectionFailureKind.NotFound) { yield break; }   // a folder that is not there yet has no files, as with a local folder
        foreach (var entry in entries.Where(e => !e.IsDirectory && matcher.IsMatch(e.Name)).OrderBy(e => e.Modified).ThenBy(e => e.Name, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            yield return new TriggerFileEntry(directory.Length == 0 ? entry.Name : directory.TrimEnd('/') + "/" + entry.Name, entry.Size, entry.Modified);
        }
    }

    public async Task<Stream> OpenReadAsync(string path, CancellationToken ct) => await (await StoreAsync(ct).ConfigureAwait(false)).OpenReadAsync("/" + path.TrimStart('/'), ct).ConfigureAwait(false);

    public async ValueTask DisposeAsync()
    {
        if (_store is { } store) { _store = null; await store.DisposeAsync().ConfigureAwait(false); }
    }

    /// <summary>A file name pattern with <c>*</c> and <c>?</c> (what a trigger's <c>path</c> says), matched on the whole name, case-sensitively as a Unix server does.</summary>
    internal static Regex Glob(string pattern) => new("^" + Regex.Escape(pattern).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$", RegexOptions.CultureInvariant | RegexOptions.Singleline);
}

/// <summary>
/// Finds the file source behind a connection slot of a task by the connection the slot is bound to in the task's release channel (plan 27a IX07, the binding of plan 21 and <c>docs/channel-bindings.md</c>): the `bound` delegate answers the connection for <c>(task, slot)</c> (the runner's binding store
/// and the connection files are what it reads; it is a delegate so this library depends on neither), or null when the slot is bound to nothing here. A connection that is a file store becomes a <see cref="ConnectionFileSource"/>; anything else, or no binding, is left to the fallback
/// (the local folders of <see cref="LocalFileSources"/>), so both kinds of slot work in one runner. A slot bound differently in two release channels gives each channel its own source: the trigger never reads production's folder from a test channel.
/// </summary>
public sealed class ConnectionFileSources : IFileSourceProvider
{
    private readonly Func<TaskRef, string, Func<CancellationToken, Task<IFileStore>>?> _opener;
    private readonly IFileSourceProvider? _fallback;

    public ConnectionFileSources(Func<TaskRef, string, ConnectionDefinition?> bound, ISecretResolver secrets, IFileSourceProvider? fallback = null)
    {
        ArgumentNullException.ThrowIfNull(bound);
        _fallback = fallback;
        _opener = (task, slot) => bound(task, slot) is { } definition && ConnectionKinds.IsFileStore(definition.KindName) ? ct => FileStores.OpenAsync(definition, secrets, ct) : null;
    }

    /// <summary>The same, with the opening of the store given directly: what a host with its own way of making a store (or a test with a store of its own) passes.</summary>
    public ConnectionFileSources(Func<TaskRef, string, Func<CancellationToken, Task<IFileStore>>?> opener, IFileSourceProvider? fallback = null)
    {
        (_opener, _fallback) = (opener ?? throw new ArgumentNullException(nameof(opener)), fallback);
    }

    public IFileSource? Open(TaskRef task, string connectionSlot)
    {
        ArgumentNullException.ThrowIfNull(task);
        return _opener(task, connectionSlot) is { } open ? new ConnectionFileSource(open) : _fallback?.Open(task, connectionSlot);
    }
}
