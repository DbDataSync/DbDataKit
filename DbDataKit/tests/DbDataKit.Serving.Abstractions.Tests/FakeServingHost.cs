using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace DbDataKit.Serving.Tests;

/// <summary>
/// A host with a few tables of integers and text, two users and the limits a real host has, for the tests of anything written against <see cref="IServingHost"/> (the protocol libraries and their conformance suite, plan 19 N02 and N03).
/// A query's text is the name of a table; <c>admin</c> sees everything, <c>reader</c> only the tables of the <c>public</c> schema and is held to <see cref="ReaderRowLimit"/> rows.
/// </summary>
public sealed class FakeServingHost(bool withWrites = false) : IServingHost
{
    public const long ReaderRowLimit = 3;

    private readonly Dictionary<string, (string Password, string Role)> _users = new() { ["admin"] = ("admin-pw", "admin"), ["reader"] = ("reader-pw", "reader") };
    private readonly Dictionary<string, List<(int Id, string Name)>> _tables = new()
    {
        ["public.items"] = [(1, "a"), (2, "b"), (3, "c"), (4, "d"), (5, "e")],
        ["secret.salaries"] = [(1, "x")],
    };
    private readonly FakeWrites? _writes = withWrites ? new FakeWrites() : null;

    public IServingWrites? Writes => _writes;

    /// <summary>What the host was asked to write, for the tests that check a write reached it.</summary>
    public IReadOnlyList<string> Written => _writes?.Statements ?? [];

    private static Schema ColumnsOfTable { get; } = new Schema.Builder().Field(new Field("id", Int32Type.Default, false)).Field(new Field("name", StringType.Default, true)).Build();

    public ValueTask<ServingCaller?> AuthenticateAsync(ServingCredentials credentials, CancellationToken cancellationToken = default)
    {
        ServingCaller? caller = credentials switch
        {
            ServingCredentials.Password(var user, var secret) when _users.TryGetValue(user, out var known) && known.Password == secret => Caller(user, known.Role),
            ServingCredentials.Bearer(var token) when token.StartsWith("token-", StringComparison.Ordinal) && _users.TryGetValue(token["token-".Length..], out var known) => Caller(token["token-".Length..], known.Role),
            ServingCredentials.Windows(var account) when account.EndsWith("\\admin", StringComparison.Ordinal) => Caller("admin", "admin"),
            _ => null,
        };
        return ValueTask.FromResult(caller);
    }

    private static ServingCaller Caller(string user, string role) => new(user, user, new HashSet<string> { role }, new Dictionary<string, string>());

    private static bool Sees(ServingCaller caller, string table) => caller.Roles.Contains("admin") || table.StartsWith("public.", StringComparison.Ordinal);

    public async IAsyncEnumerable<ServingTable> DescribeAsync(ServingCaller caller, ServingCatalogRequest request, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var name in _tables.Keys.Order(StringComparer.Ordinal))
        {
            var (schema, table) = (name.Split('.')[0], name.Split('.')[1]);
            if (!Sees(caller, name) || (request.Schema is not null && request.Schema != schema) || (request.Table is not null && request.Table != table)) continue;
            yield return new ServingTable(null, schema, table, "TABLE", ColumnsOfTable);
            await Task.Yield();
        }
    }

    public ServingLimits LimitsFor(ServingCaller caller, string source) =>
        caller.Roles.Contains("admin") ? new ServingLimits() : new ServingLimits(MaxRows: ReaderRowLimit, Timeout: TimeSpan.FromSeconds(30), MaxBatchRows: 2);

    public Task<IServingResult> QueryAsync(ServingCaller caller, ServingQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Source != "main") throw new ServingNotFoundException($"There is no source '{query.Source}'.");
        if (string.IsNullOrWhiteSpace(query.Text)) throw new ServingInvalidException("The query is empty.");
        if (!_tables.TryGetValue(query.Text.Trim(), out var rows)) throw new ServingNotFoundException($"There is no table '{query.Text}'.");
        if (!Sees(caller, query.Text.Trim())) throw new ServingDeniedException($"'{caller.Name}' may not query '{query.Text}'.");

        var held = LimitsFor(caller, query.Source);
        var maxRows = new[] { held.MaxRows, query.Asked?.MaxRows }.Where(v => v is not null).Min();
        var batchRows = new[] { held.MaxBatchRows, query.Asked?.MaxBatchRows }.Where(v => v is not null).Min() ?? 1000;
        return Task.FromResult<IServingResult>(new Result(rows, maxRows, batchRows));
    }

    private sealed class Result(List<(int Id, string Name)> rows, long? maxRows, int batchRows) : IServingResult
    {
        public Schema Schema => ColumnsOfTable;

        public async IAsyncEnumerable<RecordBatch> ReadBatchesAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            long sent = 0;
            for (var start = 0; start < rows.Count; start += batchRows)
            {
                var chunk = rows.Skip(start).Take(batchRows).ToList();
                if (maxRows is { } limit && sent + chunk.Count > limit)
                {
                    chunk = [.. chunk.Take((int)(limit - sent))];
                    if (chunk.Count > 0) yield return Batch(chunk);
                    throw new ServingLimitException($"The query reached the limit of {limit} rows.", "rows");
                }
                sent += chunk.Count;
                yield return Batch(chunk);
                await Task.Yield();
            }
        }

        private static RecordBatch Batch(List<(int Id, string Name)> chunk) => new(ColumnsOfTable,
            [new Int32Array.Builder().AppendRange(chunk.Select(r => r.Id)).Build(), new StringArray.Builder().AppendRange(chunk.Select(r => r.Name)).Build()], chunk.Count);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeWrites : IServingWrites
    {
        public List<string> Statements { get; } = [];

        public Task<ServingWriteResult> ExecuteAsync(ServingCaller caller, ServingQuery statement, CancellationToken cancellationToken = default)
        {
            if (!caller.Roles.Contains("admin")) throw new ServingDeniedException($"'{caller.Name}' may not write.");
            Statements.Add(statement.Text);
            return Task.FromResult(new ServingWriteResult(1));
        }

        public async Task<ServingWriteResult> IngestAsync(ServingCaller caller, string source, string table, IAsyncEnumerable<RecordBatch> batches, CancellationToken cancellationToken = default)
        {
            if (!caller.Roles.Contains("admin")) throw new ServingDeniedException($"'{caller.Name}' may not write.");
            long rows = 0;
            await foreach (var batch in batches.WithCancellation(cancellationToken)) rows += batch.Length;
            Statements.Add($"ingest {table}");
            return new ServingWriteResult(rows);
        }
    }
}
