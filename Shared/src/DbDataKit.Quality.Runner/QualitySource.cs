using System.Data.Common;
using DbDataKit.Connections;
using DbDataKit.Dialect;
using DbDataKit.Lineage;

namespace DbDataKit.Quality;

/// <summary>
/// One dataset with checks, as the evaluation sees it: its identity (plan 23a D3), its checks, the engine and the way to open a read-only connection to it, and what only the host knows about it (its key columns, the tables its checks name, when it was last loaded).
/// </summary>
/// <param name="Identity">The dataset's lineage identity: what a result, a status and a block are stored under.</param>
/// <param name="Checks">Its checks.</param>
/// <param name="Dialect">The engine's dialect.</param>
/// <param name="OpenReadOnly">Opens a connection that cannot write (<see cref="ReadOnlyConnections.Opener"/>); the evaluator disposes it.</param>
/// <param name="ResolveTable">The table of a dataset a check names (its own or a referential target), as the parts of its name; null when unknown.</param>
/// <param name="Aliases">The other names a person or a check uses for the dataset (the table's name inside its definition); <c>quality run --dataset</c> accepts them.</param>
/// <param name="KeyColumns">The columns that identify a row, for the sample keys (<see cref="EvaluationContext.KeyColumns"/>).</param>
/// <param name="LastLoadedAt">When the dataset was last loaded successfully, from run metadata (QF2).</param>
public sealed record QualityDataset(string Identity, IReadOnlyList<CheckDefinition> Checks, ISqlDialect Dialect, Func<CancellationToken, Task<DbConnection>> OpenReadOnly,
    Func<string, IReadOnlyList<string>?> ResolveTable, IReadOnlyList<string>? Aliases = null, IReadOnlyList<string>? KeyColumns = null, DateTimeOffset? LastLoadedAt = null)
{
    public bool Is(string name) => string.Equals(Identity, name, StringComparison.Ordinal) || (Aliases ?? []).Contains(name, StringComparer.OrdinalIgnoreCase);
}

/// <summary>Where a host's datasets with checks come from: Build's model files, Sync's replications, DbDataStore's entities, a serving layer's data sources (FQ09 to FQ12 each bring one). The shared command and the runner task read only this.</summary>
public interface IQualitySource
{
    Task<IReadOnlyList<QualityDataset>> DatasetsAsync(CancellationToken ct = default);
}

/// <summary>A fixed list of datasets.</summary>
public sealed class StaticQualitySource(IReadOnlyList<QualityDataset> datasets) : IQualitySource
{
    public Task<IReadOnlyList<QualityDataset>> DatasetsAsync(CancellationToken ct = default) => Task.FromResult(datasets);
}

/// <summary>
/// The datasets of connection definitions that carry <c>checks</c> (format 2 of <c>connection/N</c>: the serving layer's data sources, FQ05): each table a check names is one dataset, identified as <see cref="LineageIdentity.Dataset"/> makes it
/// from the connection, and its checks run over the provider's read-only connection with the read login (<see cref="ReadOnlyConnections.Opener"/>). A check's <c>dataset</c> is the table's name inside the connection (<c>orders</c>, <c>sales.orders</c>), split with the engine's dialect;
/// a check that already names an identity is kept under it and resolved through the table the connection's own names made.
/// </summary>
public sealed class ConnectionFilesQualitySource(IReadOnlyList<ConnectionDefinition> connections, ProviderRegistry providers, Func<ConnectionDefinition, CancellationToken, ValueTask<ResolvedLogin?>> login) : IQualitySource
{
    public Task<IReadOnlyList<QualityDataset>> DatasetsAsync(CancellationToken ct = default)
    {
        var datasets = new List<QualityDataset>();
        foreach (var connection in connections)
        {
            if (connection.CheckDefinitions is not { Count: > 0 } checks) continue;
            var provider = providers.For(connection);
            var dialect = provider.Dialect(connection);
            var parts = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
            var identities = new Dictionary<string, string>(StringComparer.Ordinal);

            IReadOnlyList<string> Split(string name) => dialect.SplitQualifiedName(name);
            string Identity(string name)
            {
                if (identities.TryGetValue(name, out var known)) return known;
                var table = Split(name);
                var identity = LineageIdentity.Dataset(connection, table.Count > 1 ? table[^2] : null, table[^1]).ToString();
                parts[name] = table;
                parts[identity] = table;
                return identities[name] = identity;
            }

            foreach (var check in checks)
            {
                Identity(check.Attachment.Dataset);
                if (check.Parameters is ReferentialParameters target) Identity(target.Dataset);
            }

            IReadOnlyList<string>? Resolve(string name) => parts.GetValueOrDefault(name);
            Func<CancellationToken, Task<DbConnection>> open = async token =>
            {
                var resolved = await login(connection, token).ConfigureAwait(false);
                try { return await provider.OpenAsync(new ConnectionContext { Definition = connection, Login = resolved, Role = LoginRole.Read }, token).ConfigureAwait(false); }
                finally { resolved?.Dispose(); }
            };

            foreach (var group in checks.GroupBy(c => Identity(c.Attachment.Dataset), StringComparer.Ordinal))
                datasets.Add(new QualityDataset(group.Key, [.. group], dialect, open, Resolve, Aliases: [.. group.Select(c => c.Attachment.Dataset).Distinct(StringComparer.Ordinal)]));
        }

        return Task.FromResult<IReadOnlyList<QualityDataset>>(datasets);
    }
}
