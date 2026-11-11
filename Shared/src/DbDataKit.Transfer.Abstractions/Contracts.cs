namespace DbDataKit.Transfer;

/// <summary>What to read. <see cref="Position"/> is an opaque resume point produced by the same source earlier.</summary>
public sealed record ReadRequest(IReadOnlyList<string>? Columns = null, int BatchSize = 8192, IChangePosition? Position = null);

public interface IDataSource
{
    TransferSchema Schema { get; }
    IAsyncEnumerable<DataBatch> ReadAsync(ReadRequest request, CancellationToken ct);
}

/// <summary>Watermark, LSN or slot position. Opaque to shared code: only the producing source interprets the text.</summary>
public interface IChangePosition
{
    string Serialize();
}

/// <summary>A source that can say where it stopped (Sync change readers).</summary>
public interface IPositionedDataSource : IDataSource
{
    IChangePosition? CurrentPosition { get; }
}

/// <summary>The table a bulk writer fills and the columns (in the order the batches carry them).</summary>
public sealed record WriteTarget(TableName Table, TransferSchema Columns);

public enum TransactionMode { None, PerBatch, Single }

/// <summary>Common bulk options. Engines ignore options they have no equivalent for and say so in their README.</summary>
public sealed record BulkOptions
{
    public int BatchSize { get; init; } = 20_000;
    /// <summary>Zero means no limit.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.Zero;
    public TransactionMode Transaction { get; init; } = TransactionMode.None;
    public bool KeepIdentity { get; init; }
    public bool KeepNulls { get; init; } = true;
    public IProgress<long>? Progress { get; init; }
}

public interface IBulkWriter
{
    /// <summary>Writes the batches to the target and returns the number of rows written.</summary>
    Task<long> WriteAsync(WriteTarget target, IAsyncEnumerable<DataBatch> rows, BulkOptions options, CancellationToken ct);
}

public enum StagingLifecycle
{
    /// <summary>Drop after a successful apply, keep after a failure so the run can resume on the same table.</summary>
    DropOnSuccess,
    DropAlways,
    Retain,
}

/// <summary>A staging table as named and created by a provider.</summary>
public sealed record StagingTable(TableName Name, TransferSchema Schema, bool IsTemporary, StagingLifecycle Lifecycle);

public sealed record StagingRequest(TableName Target, TransferSchema Schema, string? RunId = null, bool PreferTemporary = false, StagingLifecycle Lifecycle = StagingLifecycle.DropOnSuccess);

public interface IStagingProvider
{
    Task<StagingTable> CreateAsync(StagingRequest request, CancellationToken ct);
    Task<long> LoadAsync(StagingTable table, IAsyncEnumerable<DataBatch> rows, CancellationToken ct);
    Task DropAsync(StagingTable table, CancellationToken ct);
}

/// <summary>Why a statement is being executed. The gate enforces permissions by <see cref="WriteKind"/>.</summary>
[Flags]
public enum WriteKind
{
    None = 0,
    /// <summary>Creates or drops objects.</summary>
    Ddl = 1,
    Insert = 2,
    Update = 4,
    Delete = 8,
    Truncate = 16,
    /// <summary>A bulk load route (SqlBulkCopy, COPY).</summary>
    Bulk = 32,
    /// <summary>The product's own bookkeeping tables.</summary>
    Tracking = 64,
    Data = Insert | Update | Delete | Truncate | Bulk,
    Any = Ddl | Data | Tracking,
}

public enum WriteRisk { Safe, Risky, Destructive }

/// <summary>What a write is for. Statements carry one; the gate refuses writes whose kind is not permitted.</summary>
public sealed record WriteIntent(string StepId, WriteKind Kind, string Target, WriteRisk Risk = WriteRisk.Safe);

public interface ITransferStateStore
{
    Task<string?> GetWatermarkAsync(string key, CancellationToken ct);
    Task SetWatermarkAsync(string key, string value, CancellationToken ct);
    Task RecordRunAsync(TransferRunOutcome outcome, CancellationToken ct);
    Task<string?> GetResumeTokenAsync(string key, CancellationToken ct);
    Task SetResumeTokenAsync(string key, string? token, CancellationToken ct);
}

public sealed record TransferRunOutcome(string RunId, string Key, bool Succeeded, long Rows, DateTimeOffset StartedUtc, DateTimeOffset EndedUtc, string? ErrorCode = null);

public enum VerificationMode { RowCount, KeyPresence, ColumnHash }

public sealed record VerificationResult(bool Match, long SourceRows, long TargetRows, IReadOnlyList<string> Differences);

/// <summary>Compares a source with a target. Differences name columns and counts, never values. Implementations are product-side or later packages.</summary>
public interface ITransferVerifier
{
    Task<VerificationResult> VerifyAsync(IDataSource source, TableName target, TransferSchema schema, VerificationMode mode, CancellationToken ct);
}
