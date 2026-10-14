namespace DbDataSync.Drivers.Abstractions;

public sealed record TableMetadata(string Schema, string Table);

/// <summary><paramref name="IsIdentity"/> is auto-detected from the catalog rather than declared by
/// an operator: a writer that inserts an explicit value into an identity column has to bracket the
/// insert with SET IDENTITY_INSERT, and asking whoever authors a table mapping to remember that is a
/// silent-data-loss trap.</summary>
/// <param name="KeyOrdinal">This column's 1-based position within the primary key, or 0 when the
/// position isn't known. Key order matters — it is the order of the index — so a key is read with
/// <see cref="MappingKey.InKeyOrder"/>, which puts known positions first and leaves unknown ones in column order.</param>
public sealed record ColumnMetadata(
    string Name, string NativeType, bool IsNullable, bool IsPrimaryKey, bool IsIdentity, int KeyOrdinal = 0);
