// Picks up the CSV files that appear in a folder of a share and lands their rows in a staging table. The code is yours from here on: change the folders, the columns and the CSV options.
// What it uses of the kit: Files.ClaimAsync (a file whose content came before is not taken twice, whatever it is called), DelimitedReader, StagingLander (idempotent batches through the write gate).
using System.Security.Cryptography;
using DbDataKit.Dialect;
using DbDataKit.Runner.Kit.Formats;
using DbDataKit.Runner.Kit.Landing;
using DbDataKit.Transfer;

namespace CsvPickup;

public sealed class PickUpSlots : Slots
{
    /// <summary>The share the files arrive in (a folder the Runner's machine can read; the bound connection says which).</summary>
    public Slot<object> Share { get; } = new("share", kind: "folder");

    /// <summary>The database that holds the staging table.</summary>
    public Slot<object> Staging { get; } = new("staging", kind: "database");
}

/// <summary>Named after the job (<c>PickUpCsv</c> + <c>Counters</c>): that is how the kit finds what a job counts, for the manifest and the console.</summary>
public static class PickUpCsvCounters
{
    public static readonly Counter Files = new("files");
    public static readonly Counter Rows = new("rows");
    public static readonly Counter Skipped = new("skipped");
    public static readonly Counter Rejected = new("rejected");
}

public static class CustomersTable
{
    public const string Drop = "/drop";
    public const string Done = "/done";

    public static readonly TableName Table = new(null, "customers_raw");

    /// <summary>The columns of the file, by their names in its header line; a staging table takes what arrived and a model downstream enforces the rules.</summary>
    public static readonly TransferSchema Schema = new(
    [
        new("id", CanonicalType.Of(CanonicalKind.Int64), Nullable: false),
        new("name", CanonicalType.String(200)),
        new("email", CanonicalType.String(200)),
        new("joined", CanonicalType.Of(CanonicalKind.Date)),
    ]);
}

[RunnerTask("pick-up-csv")]
public sealed class PickUpCsv : RunnerJob<NoInput, NoProgress, PickUpSlots>
{
    protected override async Task RunAsync(CancellationToken ct)
    {
        var share = await Outside.OpenShareAsync(Slots.Share, ct);
        await using var gate = await Outside.OpenStagingAsync(Slots.Staging, "pick-up-csv", ct);
        var bad = new BadRows();                                                       // records that do not fit: their number and reason, never the value
        var lander = new StagingLander(gate, Outside.Dialect, CustomersTable.Table, CustomersTable.Schema, new LandOptions { OnBadRow = BadRowDecision.Collect, Bad = bad });
        await lander.EnsureTableAsync(ct);

        foreach (var file in (await share.ListAsync(CustomersTable.Drop, ct)).Where(f => f.Path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)))
        {
            if (!file.Complete) continue;                                              // a writer is still at it: next run
            var claim = await Files.ClaimAsync("share", file.Path, file.Size, file.Modified, file.Hash!, ct);
            if (claim is null) { Count(PickUpCsvCounters.Skipped, 1); continue; }         // this content came before, under whatever name

            var before = bad.Count;
            await using (var stream = await share.OpenReadAsync(file.Path, ct))
            {
                // the key names the file's content, so landing it again (a retry after the move failed) writes nothing twice
                var rows = DelimitedReader.ReadAsync(stream, DelimitedOptions.Csv, new ReadOptions { OnBadRow = BadRowDecision.Collect, Bad = bad, File = file.Path }, ct);
                var landed = await lander.LandAsync($"csv-{file.Hash}", rows, ct);
                Count(PickUpCsvCounters.Rows, landed.Rows);
                Count(PickUpCsvCounters.Rejected, bad.Count - before);
            }

            await claim.CompleteAsync(ct: ct);
            await share.MoveAsync(file.Path, CustomersTable.Done + file.Path[CustomersTable.Drop.Length..], ct);
            Count(PickUpCsvCounters.Files, 1);
        }
    }
}

/// <summary>What the job needs of a share. SFTP, FTPS, SMB and object storage clients implement the same three calls; a test hands in a fake.</summary>
public interface IShare
{
    Task<IReadOnlyList<ShareFile>> ListAsync(string folder, CancellationToken ct);

    Task<Stream> OpenReadAsync(string path, CancellationToken ct);

    Task MoveAsync(string from, string to, CancellationToken ct);
}

/// <summary>A file in a listing. <paramref name="Complete"/> is false while its writer may still be at it; <paramref name="Hash"/> is the SHA-256 of its content (hex).</summary>
public sealed record ShareFile(string Path, long Size, DateTimeOffset Modified, bool Complete, string? Hash);

/// <summary>A folder on the machine the Runner runs on (a mounted share). A file is complete when it has not changed for <see cref="Settle"/>.</summary>
public sealed class LocalFolderShare(string root) : IShare
{
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);

    private string Full(string path) => System.IO.Path.GetFullPath(System.IO.Path.Combine(root, path.TrimStart('/')));

    public Task<IReadOnlyList<ShareFile>> ListAsync(string folder, CancellationToken ct)
    {
        var directory = Full(folder);
        IReadOnlyList<ShareFile> files = !Directory.Exists(directory) ? [] :
        [
            .. Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal).Select(f =>
            {
                var info = new FileInfo(f);
                var complete = DateTimeOffset.UtcNow - info.LastWriteTimeUtc > Settle;
                // hashing reads the file; fine for the files this template is for, and only a complete one is hashed
                var hash = complete ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f))) : null;
                return new ShareFile(folder.TrimEnd('/') + "/" + info.Name, info.Length, info.LastWriteTimeUtc, complete, hash);
            }),
        ];
        return Task.FromResult(files);
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct) => Task.FromResult<Stream>(File.OpenRead(Full(path)));

    public Task MoveAsync(string from, string to, CancellationToken ct)
    {
        var target = Full(to);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
        File.Move(Full(from), target, overwrite: true);
        return Task.CompletedTask;
    }
}

/// <summary>Where the job meets the outside world, in one place (see the REST template): a slot opens to the grant of its bound connection, or, in a test, to a fake handed in through <c>RunSimulator</c>'s <c>slots:</c>.</summary>
public static class Outside
{
    /// <summary>The dialect of the staging database's engine (the statements the lander writes are made for it).</summary>
    public static ISqlDialect Dialect { get; } = DialectCatalog.For(EngineId.Postgres);

    public static async Task<IShare> OpenShareAsync(Slot<object> slot, CancellationToken ct) => await slot.OpenAsync(ct) switch
    {
        IShare ready => ready,
        ConnectionGrant grant => new LocalFolderShare(grant.Database ?? grant.ConnectionString ?? throw new InvalidOperationException($"The connection '{grant.Connection}' names no folder.")),
        var other => throw new InvalidOperationException($"The slot '{slot.Name}' opened a {other.GetType().Name}; expected a connection."),
    };

    public static async Task<WriteGate> OpenStagingAsync(Slot<object> slot, string command, CancellationToken ct) => await slot.OpenAsync(ct) switch
    {
        WriteGate ready => ready,
        ConnectionGrant grant => throw new NotSupportedException(
            $"This template does not open the database connection '{grant.Connection}' itself: opening one needs the driver of its engine, which the build does not carry. Add `#r \"nuget: Npgsql, <version>\"` (or Microsoft.Data.SqlClient) at the top of this file, "
            + "open the connection with the grant's host, database and login, and return `new WriteGate(new AdoWriteExecutor(connection, Dialect), new WriteGateOptions(command, new MemoryStatementLog()))`."),
        var other => throw new InvalidOperationException($"The slot '{slot.Name}' opened a {other.GetType().Name}; expected a connection."),
    };
}
