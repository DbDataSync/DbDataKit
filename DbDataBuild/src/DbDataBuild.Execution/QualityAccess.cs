using System.Data.Common;

namespace DbDataBuild.Execution;

/// <summary>
/// The one way the shared check evaluator (<c>DbDataKit.Quality.Evaluation</c>, plan 23a FQ07) reaches a target: a connection opened with a **read login**, which is the engine's own session-level read-only recipe on top of the login's
/// permissions (<see cref="LoginSettings.OpenAsync"/> for a read login). The evaluator sends one SELECT per check and checks each statement with the shared read-only policy before it goes out; it never sees a write login, and the connection
/// openers of <see cref="LoginSettings"/> stay internal. The door is for the evaluator and for nothing else: every other statement of this tool still goes through <see cref="MutationGate"/> or <see cref="ReadSession"/>.
/// </summary>
public static class QualityAccess
{
    /// <summary>The engines the evaluator has a dialect for. A model on any other engine (Fabric, ODBC, Flight SQL, DuckDB files) has its checks stored but not evaluated, and the run says so.</summary>
    public static bool Supports(string engine) => engine is "sqlserver" or "postgres";

    /// <summary>Opens a read-only connection for the evaluator; a login that is not the read login is refused.</summary>
    public static Func<CancellationToken, Task<DbConnection>> ReadOnlyOpener(LoginSettings read)
    {
        ArgumentNullException.ThrowIfNull(read);
        if (read.Login != Login.Read) throw new ArgumentException("The check evaluator opens connections with the read login only.", nameof(read));
        return ct => read.OpenAsync(ct);
    }
}
