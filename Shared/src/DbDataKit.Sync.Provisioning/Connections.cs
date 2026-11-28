using System.Data.Common;
using DbDataKit.Sync.Core.Config;
using DbDataKit.Sync.Drivers.Abstractions;

namespace DbDataKit.Sync.Provisioning;

/// <summary>
/// What <c>DriverConnectionFactory</c> does, named so a caller can depend on the behaviour
/// rather than the concrete class — the seam <c>ProvisioningService</c>'s tests use to hand it a
/// counting fake instead of a real network connection (phase 105 §5's "one connection per endpoint"
/// guarantee is otherwise invisible to a test).
/// </summary>
public interface IConnectionFactory
{
    /// <summary>Opens the connection with its <see cref="CredentialRole.Read"/> login: what the server does for itself (browsing, tests, previews, planning). A write is <see cref="OpenAsync(string, CredentialRole, CancellationToken)"/> with <see cref="CredentialRole.Run"/> (plan 28c, E20).</summary>
    Task<(DbConnection Connection, IDriver Driver)> OpenAsync(
        string connectionName, CancellationToken cancellationToken) => OpenAsync(connectionName, CredentialRole.Read, cancellationToken);

    Task<(DbConnection Connection, IDriver Driver)> OpenAsync(
        string connectionName, CredentialRole role, CancellationToken cancellationToken);
}

/// <summary>
/// The one question <c>MappingMetadataService</c> asks of a live database, behind an interface
/// so the cache's own rules can be tested without one.
/// <para>
/// The same seam <c>IChangeCounterSource</c> is: one narrow interface over the piece that does I/O,
/// implemented by the real service and faked in tests, rather than an interface over everything the
/// service can do. Introspection stays <c>MetadataService</c>'s job — this only names the part
/// of it that the cache depends on.
/// </para>
/// </summary>
public interface IColumnCatalog
{
    Task<IReadOnlyList<ColumnMetadata>> ListColumnsAsync(
        string connectionName, string database, string schema, string table, CancellationToken cancellationToken);
}
