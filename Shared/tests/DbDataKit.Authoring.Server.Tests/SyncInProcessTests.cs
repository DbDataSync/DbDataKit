using DbDataKit.Authoring.Server;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Core.Secrets;
using DbDataKit.Sync.Drivers.Abstractions;
using DbDataKit.Sync.Drivers.Generic;
using DbDataKit.Sync.Provisioning;

namespace DbDataKit.Authoring.Server.Tests;

/// <summary>
/// A sync draft read in the Workspace's process through Sync's shared libraries (<c>DbDataKit.Sync.*</c>): the replication and its mapping by Sync's
/// configuration model, the source table's columns by Sync's column reader over the draft's <c>catalog-cache.yaml</c> (<see cref="SyncCatalogColumns"/>),
/// and the mapping's natural key derived by the rule the engine applies when a run starts. The draft maps <c>crm.dbo.customers</c> into
/// <c>dw.public.customers</c>, renaming the key <c>customer_id</c> to <c>id</c>.
/// </summary>
public sealed class SyncInProcessTests : IDisposable
{
    private const string Task = """
        schemaVersion: 2
        name: customers
        scheduling:
          mode: Continuous
          frequencySeconds: 30
        changeProcessing:
          reader:
            kind: MsSqlChangeTracking
          cache:
            kind: StagingTable
        endpoints:
          source:
            connectionName: crm
            database: crm
          target:
            connectionName: dw
            database: dw
        """;

    private const string Mapping = """
        schemaVersion: 1
        name: customers
        sources:
        - schema: dbo
          table: customers
        targets:
        - schema: public
          table: customers
        columnMappings:
        - sourceColumn: customer_id
          targetColumn: id
        - sourceColumn: name
          targetColumn: name
        """;

    private const string Cache = """
        schema: catalog-cache/2
        generated_at: 2026-11-18T10:00:00Z
        connections:
          crm:
            engine: sqlserver
            read: {server: runners-eu, product: runner, channel: dev, at: 2026-11-18T10:00:00Z}
            databases:
              - {name: crm, schemas: [dbo]}
            tables:
              - database: crm
                schema: dbo
                name: customers
                kind: table
                columns:
                  - {name: customer_id, type: int, nullable: false, key: true}
                  - {name: name, type: "nvarchar(200)", nullable: true}
              - database: crm
                schema: dbo
                name: notes
                kind: table
                columns:
                  - {name: text, type: "nvarchar(max)", nullable: true}
        """;

    private readonly string _root = Directory.CreateTempSubdirectory("dbdk-sync-inprocess-").FullName;

    public SyncInProcessTests()
    {
        Write("config/connections/crm.yaml", "schema: connection/1\nname: crm\nengine: sqlserver\naddress:\n  host: crm.example.com\n  database: crm");
        Write("config/connections/dw.yaml", "schema: connection/1\nname: dw\nengine: postgres\naddress:\n  host: dw.example.com\n  database: dw");
        Write("config/replications/customers/task.yaml", Task);
        Write("config/replications/customers/table-mappings/customers.yaml", Mapping);
        Write("catalog-cache.yaml", Cache);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n") + "\n");
    }

    private (TableMappingConfig Mapping, SourceTableRef Source) Load(string mappingName = "customers")
    {
        var repository = ConfigRepository.ReadOnly(Path.Combine(_root, "config"), SecretStore.ForProviders());
        var task = repository.LoadReplicationTask("customers");
        var mapping = repository.LoadTableMapping("customers", mappingName);
        return (mapping, EndpointResolution.ResolveSource(task, mapping.Sources[0]));
    }

    private MappingColumnReader Reader() => new(SyncCatalogColumns.Read(_root) ?? throw new InvalidOperationException("the draft's cache did not read"));

    private static List<ColumnMetadata> AsMetadata(IEnumerable<CachedColumn> columns) =>
        [.. columns.Select(c => new ColumnMetadata(c.Name, c.NativeType, c.IsNullable, c.IsPrimaryKey, c.IsIdentity, c.KeyOrdinal))];

    [Fact]
    public async Task A_mappings_natural_key_is_derived_in_process_from_the_cached_source_key_in_the_targets_names()
    {
        var (mapping, source) = Load();

        var read = await Reader().ReadAsync(source, TestContext.Current.CancellationToken);

        Assert.Null(read.Unavailable);
        Assert.Equal(["customer_id", "name"], read.Columns!.Select(c => c.Name));
        Assert.Equal(["id"], NaturalKeyDerivation.Derive(AsMetadata(read.Columns!), mapping.ColumnMappings));
    }

    [Fact]
    public async Task A_source_table_with_no_key_in_the_cache_derives_no_key()
    {
        Write("config/replications/customers/table-mappings/notes.yaml", Mapping.Replace("customers", "notes").Replace("customer_id", "text"));
        var (mapping, source) = Load("notes");

        var read = await Reader().ReadAsync(source, TestContext.Current.CancellationToken);

        Assert.Equal(["text"], read.Columns!.Select(c => c.Name));
        Assert.Empty(NaturalKeyDerivation.Derive(AsMetadata(read.Columns!), mapping.ColumnMappings));
    }

    [Fact]
    public async Task A_table_or_connection_the_cache_does_not_hold_is_answered_as_missing_or_with_the_reason()
    {
        var reader = Reader();

        var missingTable = await reader.ReadAsync(new TableRef { ConnectionName = "crm", Database = "crm", Schema = "dbo", Table = "orders" }, TestContext.Current.CancellationToken);
        var missingConnection = await reader.ReadAsync(new TableRef { ConnectionName = "erp", Database = "erp", Schema = "dbo", Table = "orders" }, TestContext.Current.CancellationToken);

        Assert.True(missingTable.IsMissingTable);
        Assert.Null(missingConnection.Columns);
        Assert.Contains("'erp' has no entry in catalog-cache.yaml", missingConnection.Unavailable);
    }

    [Fact]
    public void The_libraries_read_in_process_are_the_shared_ones_and_no_assembly_of_DbDataSync_is_loaded()
    {
        Assert.Equal("DbDataKit.Sync.Provisioning", typeof(MappingColumnReader).Assembly.GetName().Name);
        Assert.Equal("DbDataKit.Sync.Core", typeof(ConfigRepository).Assembly.GetName().Name);
        Assert.DoesNotContain(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name!.StartsWith("DbDataSync.", StringComparison.Ordinal));
    }
}
