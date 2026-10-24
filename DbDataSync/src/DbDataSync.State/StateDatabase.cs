namespace DbDataSync.State;

/// <summary>
/// Sync's state database (architecture/detailed-design.md §3.7), shared by DbDataSync.Api and every DbDataSync.TaskRunner process: the shared
/// <see cref="DbDataKit.State.StateDatabase"/> opened with Sync's schema (<see cref="Migrations"/>). SQLite by default; phase 63 added SQL Server
/// and PostgreSQL as alternatives, and plan 13 (U04) moved the engine, the dialects and the stores for users, sessions, invites and notifications to
/// <c>DbDataKit.State</c>.
/// </summary>
public sealed partial class StateDatabase : DbDataKit.State.StateDatabase
{
    /// <summary>The SQLite path constructor, unchanged from before there was a choice — an existing deployment reaches this and nothing about its database moves.</summary>
    public StateDatabase(string sqliteFilePath) : base(sqliteFilePath, Migrations.Schema)
    {
    }

    public StateDatabase(string engine, string connectionString) : base(engine, connectionString, Migrations.Schema)
    {
    }
}
