using System.Text.RegularExpressions;
using DbDataKit.Connections;
using DbDataKit.Connections.Odbc;
using DbDataKit.Connections.Postgres;
using DbDataKit.Connections.SqlServer;
using DbDataKit.Dialect;

namespace DbDataBuild.Execution;

/// <summary>
/// What went wrong with a driver, described without the driver's message (which can quote data, a path or a user name): the exception's type and an error number, and for the failures a person can
/// fix on the machine a sentence that says what is missing. The classification is the shared <see cref="FailureClassifier"/>'s, which never keeps a driver message; this adds only Build's wording.
/// </summary>
public static partial class Failures
{
    private static readonly FailureClassifier Classifier =
        new([new OdbcFailureMapper(), new SqlServerFailureMapper(), new PostgresFailureMapper(), new CatalogFailureMapper()]);

    // a number is what the engines hand over: SQL Server's error number, a five-character SQLSTATE (PostgreSQL's, or an ODBC driver's before the native code); the classifier's other codes are type names
    [GeneratedRegex(@"^(?<number>-?\d{1,10}|[0-9A-Z]{5})(/.*)?$")]
    private static partial Regex NumberCode { get; }

    /// <summary>` (error 1234)` for a database exception (SQL Server's number, PostgreSQL's SQLSTATE, the SQLSTATE of an ODBC driver), else nothing.</summary>
    public static string Number(Exception ex)
    {
        var code = Classifier.Classify(ex, EngineId.Ansi).Code;
        return code is not null && NumberCode.Match(code) is { Success: true } match && !(ex is TimeoutException) ? $" (error {match.Groups["number"].Value})" : "";
    }

    /// <summary>The failure in words that quote nothing: the missing prerequisite when that is it, a Flight SQL failure named by its kind, else the type and the error number.</summary>
    public static string Describe(Exception ex) => OdbcPrerequisite.Describe(ex) ?? FlightSqlFailure.Describe(ex) ?? $"{ex.GetType().Name}{Number(ex)}";

    /// <summary>Build's own catalog exception (the ODBC driver said a catalog function failed): only the SQLSTATE is the code.</summary>
    private sealed class CatalogFailureMapper : IFailureMapper
    {
        public ConnectionFailure? Map(Exception exception, EngineId engine) =>
            exception is OdbcCatalogException catalog && catalog.SqlState is { Length: 5 } state && state.All(char.IsAsciiLetterOrDigit)
                ? new ConnectionFailure(SqlStateMap.Classify(state) ?? ConnectionFailureKind.Other, state.ToUpperInvariant(), engine)
                : null;
    }
}
