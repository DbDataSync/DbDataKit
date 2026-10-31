using DbDataKit.Serving;
using System.Runtime.CompilerServices;
using Apache.Arrow;
using Apache.Arrow.Types;
using DbDataKit.Dialect;
using DbDataKit.Metadata;
using DbDataProxy.Data;
using DbDataProxy.Server.Auth;
using DbDataProxy.Server.Configuration;
using DbDataProxy.Server.Data;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace DbDataProxy.Server.Serving;

public sealed partial class ProxyServing
{
    /// <summary>
    /// What a data source's catalog contains, from the shared catalog readers behind its <see cref="IMetadataProvider"/> (the ODBC catalog functions for ODBC sources, including the 32-bit worker, INFORMATION_SCHEMA or the engine's own views for the others),
    /// and what the source says about itself. Filtering is the protocol's, in C#, never by pasting client text into SQL.
    /// </summary>
    private sealed class CatalogService(DataSourceCatalog sources, ILogger log) : IServingCatalog
    {
        public async Task<ServingSourceFacts> FactsAsync(ServingContext context, string source, CancellationToken cancellationToken = default)
        {
            var resolved = Resolve(context, source);
            string product = "";
            string? version = null, quote = null;
            bool? caseInsensitive = null, nullsLow = null;
            switch (resolved.Options.Provider.ToLowerInvariant())
            {
                case "sqlserver":
                    (product, quote, caseInsensitive, nullsLow) = ("Microsoft SQL Server", "\"", true, true);
                    break;
                case "duckdb":
                    (product, quote, caseInsensitive, nullsLow) = ("DuckDB", "\"", true, false);
                    break;
                case "postgres":
                    // names fold to lower case unquoted and text compares exactly; NULLs sort last in an ascending order
                    (product, quote, caseInsensitive, nullsLow) = ("PostgreSQL", "\"", false, false);
                    break;
            }

            // Ask the database itself for what a static table cannot know: the version, and for ODBC the product and the driver's identifier quote. A database that cannot be reached still gets the static answers.
            if (resolved.Executor is IMetadataProvider provider)
            {
                try
                {
                    var dialect = await provider.GetDialectAsync(cancellationToken).ConfigureAwait(false);
                    if (dialect.DbmsName is { Length: > 0 } name) product = name;
                    if (dialect.DbmsVersion is { Length: > 0 } v) version = v;
                    if (dialect.Quote is { } q) quote = q.Prefix;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    log.LogDebug(ex, "Could not read the dialect of data source {Source}", resolved.Name);
                }
            }
            return new ServingSourceFacts(product, version, ReadOnly: !resolved.Options.AllowWrites, quote, caseInsensitive, nullsLow);
        }

        public async IAsyncEnumerable<ServingObject> DescribeAsync(ServingContext context, IReadOnlyList<string> under, bool withColumns = false, bool withPrimaryKeys = false, string source = "",
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            var resolved = Resolve(context, source);
            var metadata = resolved.Executor as IMetadataProvider
                ?? throw new RpcException(new Status(StatusCode.Unimplemented, $"Data source '{resolved.Name}' cannot list its catalog."));

            var schemata = await TranslateAsync(() => metadata.ListSchemasAsync(resolved.Options.CommandTimeoutSeconds, cancellationToken)).ConfigureAwait(false);
            foreach (var s in schemata)
            {
                var path = Path(s.Catalog, s.Name);
                if (UnderPrefix(path, under)) yield return new ServingObject(ServingObjectKind.Schema, path, Catalog: s.Catalog);
            }

            var tables = await TranslateAsync(() => metadata.ListTablesAsync(resolved.Options.CommandTimeoutSeconds, cancellationToken)).ConfigureAwait(false);
            ISqlDialect? dialect = null;
            DeclaredColumns? declared = null;
            foreach (var t in tables)
            {
                var path = Path(t.Catalog, t.Schema, t.Name);
                if (!UnderPrefix(path, under)) continue;

                Schema? columns = null;
                if (withColumns)
                {
                    dialect ??= await LoadQuotingDialectAsync(resolved, cancellationToken).ConfigureAwait(false);
                    declared ??= LoadDeclaredColumns(resolved);
                    columns = await TableSchemaAsync(resolved, dialect, declared, t.Catalog, t.Schema, t.Name, cancellationToken).ConfigureAwait(false);
                }
                IReadOnlyList<string>? key = null;
                string? keyName = null;
                if (withPrimaryKeys)
                {
                    var keys = await TranslateAsync(() => metadata.ListPrimaryKeysAsync(t.Catalog, t.Schema, t.Name, cancellationToken)).ConfigureAwait(false);
                    key = [.. keys.OrderBy(k => k.Sequence).Select(k => k.Column)];
                    keyName = keys.Select(k => k.KeyName).FirstOrDefault(n => n is not null);
                }
                yield return new ServingObject(string.Equals(t.Type, "VIEW", StringComparison.OrdinalIgnoreCase) ? ServingObjectKind.View : ServingObjectKind.Table, path,
                    Columns: columns, Properties: t.Type is null ? null : new Dictionary<string, string> { ["type"] = t.Type }, PrimaryKey: key, Catalog: t.Catalog, SchemaName: t.Schema, PrimaryKeyName: keyName);
            }
        }

        private ResolvedDataSource Resolve(ServingContext context, string requested)
        {
            try { return sources.Resolve(PrincipalOf(context.Caller), requested); }
            catch (Exception ex) when (ProxyFailures.ToServing(ex) is var serving && !ReferenceEquals(serving, ex)) { throw serving; }
        }

        private static Task<T> TranslateAsync<T>(Func<Task<T>> work) => ProxyFailures.TranslateAsync(work);

        private static IReadOnlyList<string> Path(params string?[] parts) => [.. parts.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!)];

        private static bool UnderPrefix(IReadOnlyList<string> path, IReadOnlyList<string> under) => under.Count == 0 || path.Take(under.Count).SequenceEqual(under);

        /// <summary>Looks up the declared columns of one table, by name; null when the database cannot say.</summary>
        private delegate Task<IReadOnlyDictionary<string, ColumnEntry>?> DeclaredColumns(string? catalogName, string? schemaName, string table, CancellationToken cancellationToken);

        /// <summary>
        /// A zero-row query cannot always say how a column was declared (DuckDB reports every decimal as 0/0, nothing says NOT NULL), so precision, scale and nullability come from the catalog reader, one table at a time. A source whose catalog
        /// cannot be read keeps what the zero-row query reported.
        /// </summary>
        private static DeclaredColumns LoadDeclaredColumns(ResolvedDataSource source)
        {
            if (source.Executor is not IMetadataProvider provider) return (_, _, _, _) => Task.FromResult<IReadOnlyDictionary<string, ColumnEntry>?>(null);
            return async (catalogName, schemaName, table, ct) =>
            {
                try { return ByName(await provider.ListColumnsAsync(catalogName, schemaName, table, ct).ConfigureAwait(false)); }
                catch (Exception ex) when (ex is not OperationCanceledException) { return null; }
            };
        }

        private static Dictionary<string, ColumnEntry> ByName(IEnumerable<ColumnEntry> columns)
        {
            var map = new Dictionary<string, ColumnEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in columns) map[c.Name] = c;
            return map;
        }

        /// <summary>The Arrow schema of a table (from a zero-row query) with the declared nullability and precision laid over it; a table whose schema cannot be read (permissions, exotic types) has an empty one.</summary>
        private static async Task<Schema> TableSchemaAsync(ResolvedDataSource source, ISqlDialect dialect, DeclaredColumns declared, string? catalogName, string? schemaName, string table, CancellationToken cancellationToken)
        {
            // Some ODBC drivers report a catalog that is not valid in SQL (the text driver reports the directory path), so fall back from the fully qualified name to schema.table and finally the bare table name.
            string?[][] candidates = [[catalogName, schemaName, table], [schemaName, table], [table]];
            foreach (var parts in candidates.DistinctBy(p => string.Join('\0', p)))
            {
                try
                {
                    // Quote inside the try: a name the dialect refuses (control characters, S-9) just moves on to the next candidate.
                    string name = dialect.QuoteQualified(parts.Where(p => !string.IsNullOrEmpty(p)).Select(p => p!));
                    await using var result = await source.Executor
                        .ExecuteQueryAsync(new QueryRequest($"SELECT * FROM {name} WHERE 1 = 0", 1, source.Options.CommandTimeoutSeconds), cancellationToken)
                        .ConfigureAwait(false);
                    var columns = await declared(catalogName, schemaName, table, cancellationToken).ConfigureAwait(false);
                    return ApplyDeclaredColumns(result.Schema, columns);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Try the next, less qualified name.
                }
            }
            return new Schema([], null);
        }

        /// <summary>Overlays declared nullability and decimal precision/scale on a schema read from a zero-row query.</summary>
        internal static Schema ApplyDeclaredColumns(Schema schema, IReadOnlyDictionary<string, ColumnEntry>? declared)
        {
            if (declared is null) return schema;
            var fields = schema.FieldsList.Select(field =>
            {
                if (!declared.TryGetValue(field.Name, out var column)) return field;
                var type = field.DataType;
                if (type is Decimal128Type && column.Precision is > 0 and <= 38 && column.Scale is >= 0 && column.Scale <= column.Precision)
                    type = new Decimal128Type(column.Precision.Value, column.Scale.Value);
                return new Field(field.Name, type, column.Nullable, field.Metadata);
            }).ToList();
            return new Schema(fields, schema.Metadata);
        }

        /// <summary>
        /// The dialect used to quote table names in schema probes. ODBC sources use the driver's own identifier quote (some use backticks or brackets, some none, in which case only plain names are emitted); other providers use their built-in dialect,
        /// and an ODBC driver that cannot say gets the generic double-quote dialect.
        /// </summary>
        private static async Task<ISqlDialect> LoadQuotingDialectAsync(ResolvedDataSource source, CancellationToken cancellationToken)
        {
            var fallback = ProviderDialects.For(source.Options.Provider);
            if (!UseCatalogApi(source) || source.Executor is not IMetadataProvider provider) return fallback;
            try
            {
                var info = await provider.GetDialectAsync(cancellationToken).ConfigureAwait(false);
                return ProviderDialects.ForOdbc(info.DbmsName, info.Quote?.Prefix, info.Quote?.Suffix);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return fallback;
            }
        }

        /// <summary>True when metadata should come from the driver's catalog API (ODBC SQLTables) instead of INFORMATION_SCHEMA.</summary>
        private static bool UseCatalogApi(ResolvedDataSource source) => source.Options.Metadata.ToLowerInvariant() switch
        {
            "odbc" => true,
            "informationschema" => false,
            _ => source.Options.Provider.Equals("odbc", StringComparison.OrdinalIgnoreCase),
        };
    }
}
