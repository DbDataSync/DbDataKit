namespace DbDataKit.State.Verification;

/// <summary>One module as a source holds it.</summary>
/// <param name="Key">The module's name and, for a module kept per group or service, its scope (<c>runner_progress/main</c>): what two sources are matched on.</param>
/// <param name="Version">How many migrations the source has applied.</param>
/// <param name="Checksum">The checksum of those migrations, or null where the source does not know it.</param>
/// <param name="Tables">The module's tables, by name.</param>
public sealed record DigestModule(string Key, string Name, int Version, string? Checksum, IReadOnlyList<string> Tables);

/// <summary>A table's row count and its digest: lower-case hex of the sum modulo 2^256 of the SHA-256 of every row's snapshot line, independent of row order: the manifest's <c>rows_digest</c> (see <c>SnapshotValues</c>).</summary>
public sealed record TableDigest(long Rows, string Sha256);

/// <summary>The hash of one row and its key, for finding which rows differ once two tables' digests do.</summary>
/// <param name="Key">The key's neutral values (a <see cref="long"/> for an integer, text for everything else), in key order.</param>
/// <param name="KeyText">The key as people read it: the values joined with <c>/</c>.</param>
public sealed record RowDigest(IReadOnlyList<object> Key, string KeyText, byte[] Hash);

/// <summary>
/// Something a table's digest can be had from: a live state database (<see cref="DatabaseDigestSource"/>), and later a snapshot's manifest. The verifier compares two of them; where both can also
/// say which rows differ (<see cref="IRowDigestSource"/>) the report names the keys, and otherwise it says that the digests differ.
/// </summary>
public interface ITableDigestSource
{
    /// <summary>What the source is, for messages: a database and its engine, or a snapshot's directory.</summary>
    string Description { get; }

    /// <summary>The modules the source holds.</summary>
    IReadOnlyList<DigestModule> Modules();

    /// <summary>The row count and digest of one table, read in bounded batches. <paramref name="onRows"/> is told how many rows have been read so far.</summary>
    TableDigest Digest(string moduleKey, string table, VerifyOptions options, CancellationToken cancellationToken, Action<long>? onRows = null);
}

/// <summary>A source that can also list a table's row hashes in key order, a page at a time: a database, not a manifest.</summary>
public interface IRowDigestSource : ITableDigestSource
{
    /// <summary>Up to <paramref name="take"/> rows after the key <paramref name="after"/> (from the start when null), in key order.</summary>
    IReadOnlyList<RowDigest> Rows(string moduleKey, string table, IReadOnlyList<object>? after, int take, VerifyOptions options, CancellationToken cancellationToken);
}
