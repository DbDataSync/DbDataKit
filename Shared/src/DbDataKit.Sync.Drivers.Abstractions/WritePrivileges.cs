namespace DbDataSync.Drivers.Abstractions;

/// <summary>
/// The changes a writer makes to the target table, and so the privileges its run login needs there: what a preflight asks the engine for before
/// any pass runs. Declared by the writer beside the statements it issues (<see cref="IChangeWriter.NeedsPrivileges"/>), as it declares the key it
/// needs.
/// </summary>
[Flags]
public enum WritePrivileges
{
    /// <summary>Changes nothing on the target table.</summary>
    None = 0,

    /// <summary>Adds rows (<c>INSERT</c>, a bulk copy, the insert branch of a <c>MERGE</c>).</summary>
    Insert = 1,

    /// <summary>Changes rows in place (<c>UPDATE</c>, the update branch of a <c>MERGE</c>, closing a version).</summary>
    Update = 2,

    /// <summary>Removes rows (<c>DELETE</c>, the delete branch of a <c>MERGE</c>, clearing a scope before a refill).</summary>
    Delete = 4,

    /// <summary>All three: what a writer that does not say is assumed to need.</summary>
    All = Insert | Update | Delete,
}
