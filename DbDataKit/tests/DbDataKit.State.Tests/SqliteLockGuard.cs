using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using System.Runtime.InteropServices;

[assembly: AssemblyFixture(typeof(DbDataKit.State.Tests.SqliteLockGuardFixture))]

namespace DbDataKit.State.Tests;

/// <summary>
/// Fails a test at the exact call that opens a second state connection on a SQLite file while another connection of the same thread still holds a lock on it (an open reader, or a transaction that has
/// read or written). On SQLite (rollback journal) that is a deadlock waiting for the 5 s busy timeout as soon as another process or thread has a writer waiting to commit: the new connection is refused
/// by the writer's pending lock, and the writer waits for the old connection's reader, which waits for the new one (the cause of the stall of <c>DefinitionPublisher.FindPayload</c>, ci-lessons "database is locked").
/// <para>
/// Link this file into a test project (<c>&lt;Compile Include="..\DbDataKit.State.Tests\SqliteLockGuard.cs" Link="SqliteLockGuard.cs" /&gt;</c>) and every test of that assembly runs with the guard on. Nothing is timed and
/// nothing is probed over a second connection: each connection a thread opens is remembered with its stack trace until it is closed, and a new one asks the thread's still-open connections of the same file for their
/// transaction state (<c>sqlite3_txn_state</c>, no lock held is "none"). A violation throws <see cref="SqliteConnectionUnderLockException"/> from the open, naming both call sites, and is also kept so that a
/// catch-all that swallows it is still reported (<see cref="SqliteLockGuardFixture"/>). Connections of other threads are never looked at, so a test that writes concurrently is not a false positive; the price is that
/// a nesting that hops threads between the two opens (an <c>await</c>) is not seen.
/// </para>
/// </summary>
internal static class SqliteLockGuard
{
    [ThreadStatic] private static List<(SqliteConnection Connection, StackTrace Opened)>? t_open;
    [ThreadStatic] private static int t_allowed;

    private static readonly List<string> Violations = [];

    [ModuleInitializer]
    internal static void Install() => StateDatabase.AnyConnectionOpened = OnOpened;

    /// <summary>For a test whose point is a second connection under a held lock (the SQLite file-lock tests): the guard looks away on this thread until the result is disposed.</summary>
    internal static IDisposable Allow()
    {
        t_allowed++;
        return new Allowed();
    }

    private sealed class Allowed : IDisposable
    {
        private bool _done;

        public void Dispose()
        {
            if (_done) return;
            _done = true;
            t_allowed--;
        }
    }

    /// <summary>The frames of the code under test and of the test, without the runners'.</summary>
    private static string Trace(StackTrace trace) =>
        string.Join(Environment.NewLine, trace.ToString().Split('\n').Select(l => l.TrimEnd('\r')).Where(l => !l.Contains(" at Xunit.") && !l.Contains(" at System.") && !l.Contains("SqliteLockGuard.")));

    /// <summary>Forgets what was found: for the test that makes a violation on purpose.</summary>
    internal static void Forget() { lock (Violations) Violations.Clear(); }

    internal static IReadOnlyList<string> Found
    {
        get { lock (Violations) return [.. Violations]; }
    }

    [DllImport("e_sqlite3", EntryPoint = "sqlite3_txn_state")]
    private static extern int sqlite3_txn_state(IntPtr db, IntPtr schema);

    /// <summary>0 no lock held, 1 a read transaction (an open reader), 2 a write transaction. The native entry point is not in SQLitePCL's <c>raw</c>.</summary>
    private static int TxnState(SqliteConnection connection) => sqlite3_txn_state(connection.Handle!.DangerousGetHandle(), IntPtr.Zero);

    private static void OnOpened(System.Data.Common.DbConnection connection)
    {
        if (connection is not SqliteConnection sqlite) return;
        var open = t_open ??= [];
        (SqliteConnection Connection, StackTrace Opened)[] others;
        lock (open) others = [.. open];
        foreach (var (outer, opened) in others)
        {
            if (t_allowed > 0 || outer.State != System.Data.ConnectionState.Open || !string.Equals(outer.DataSource, sqlite.DataSource, StringComparison.Ordinal)) continue;
            var state = TxnState(outer);
            if (state == 0) continue;
            var message = $"A state connection was opened on '{sqlite.DataSource}' while an earlier connection of this thread holds a {(state == 2 ? "write" : "read")} lock on it (an open reader or transaction). On SQLite that deadlocks with a writer waiting to commit until the busy timeout (docs/ci-lessons.md, FindPayload). "
                + $"Materialize and close the reader (or commit) before calling another store.{Environment.NewLine}--- opened now at:{Environment.NewLine}{Trace(new StackTrace(1, true))}{Environment.NewLine}--- the earlier connection was opened at:{Environment.NewLine}{Trace(opened)}";
            lock (Violations) Violations.Add(message);
            throw new SqliteConnectionUnderLockException(message);
        }

        lock (open) open.Add((sqlite, new StackTrace(1, true)));
        sqlite.StateChange += (_, e) =>
        {
            if (e.CurrentState == System.Data.ConnectionState.Closed) lock (open) open.RemoveAll(x => ReferenceEquals(x.Connection, sqlite));
        };
    }
}

public sealed class SqliteConnectionUnderLockException(string message) : InvalidOperationException(message);

/// <summary>Reports, when the test assembly ends, a violation that a test's own catch-all swallowed (the throw already failed every test that let it through).</summary>
public sealed class SqliteLockGuardFixture : IDisposable
{
    public void Dispose()
    {
        var found = SqliteLockGuard.Found;
        if (found.Count > 0) throw new InvalidOperationException($"{found.Count} state connection(s) were opened under an open reader or transaction:{Environment.NewLine}{string.Join(Environment.NewLine + Environment.NewLine, found)}");
    }
}
