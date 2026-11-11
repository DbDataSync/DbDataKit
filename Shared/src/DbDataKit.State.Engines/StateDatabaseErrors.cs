using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace DbDataKit.State;

/// <summary>What went wrong with a connection or a statement, as far as the fix is concerned.</summary>
public enum StateErrorKind
{
    /// <summary>Anything else: the message of the provider is all there is.</summary>
    Other,

    /// <summary>The server could not be reached (nothing listens, the name does not resolve, a timeout, TLS refused).</summary>
    Unreachable,

    /// <summary>The server answered and refused the login: a wrong password, an unknown login, a login with no access to the database.</summary>
    LoginRefused,

    /// <summary>The database named in the connection string is not there.</summary>
    DatabaseMissing,

    /// <summary>The login may not do this.</summary>
    PermissionDenied,

    /// <summary>The table or schema is not there.</summary>
    ObjectMissing,
}

/// <summary>Sorts the exceptions of the three providers into <see cref="StateErrorKind"/> by their error codes, so a command can say what to do and not only what the provider said.</summary>
public static class StateDatabaseErrors
{
    public static StateErrorKind Classify(Exception exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case PostgresException pg:
                    return pg.SqlState switch
                    {
                        "28P01" or "28000" => StateErrorKind.LoginRefused,
                        "3D000" => StateErrorKind.DatabaseMissing,
                        "42501" => StateErrorKind.PermissionDenied,
                        "42P01" or "3F000" => StateErrorKind.ObjectMissing,
                        "53300" or "57P03" => StateErrorKind.Unreachable,
                        _ => StateErrorKind.Other,
                    };
                case SqlException sql:
                    return sql.Number switch
                    {
                        18456 or 18452 or 18470 or 18486 or 18487 or 18488 => StateErrorKind.LoginRefused,
                        4060 => StateErrorKind.DatabaseMissing,
                        229 or 230 or 262 or 297 or 300 or 916 => StateErrorKind.PermissionDenied,
                        208 => StateErrorKind.ObjectMissing,
                        -1 or 2 or 53 or 258 or 1225 or 10060 or 10061 or 11001 => StateErrorKind.Unreachable,
                        _ => sql.InnerException is System.Net.Sockets.SocketException ? StateErrorKind.Unreachable : StateErrorKind.Other,
                    };
                case System.Net.Sockets.SocketException or TimeoutException:
                    return StateErrorKind.Unreachable;
                case NpgsqlException when ex.InnerException is null && ex.Message.Contains("Failed to connect", StringComparison.OrdinalIgnoreCase):
                    return StateErrorKind.Unreachable;
                case SqliteException lite:
                    if (lite.SqliteErrorCode == 1 && lite.Message.Contains("no such table", StringComparison.OrdinalIgnoreCase)) return StateErrorKind.ObjectMissing;
                    if (lite.SqliteErrorCode is 8 or 23) return StateErrorKind.PermissionDenied;   // readonly database, not authorized
                    break;
            }
        }

        return StateErrorKind.Other;
    }

    /// <summary>Whether <paramref name="exception"/> is something a provider raised (as opposed to a bug of ours).</summary>
    public static bool IsProviderError(Exception exception) => exception is DbException or TimeoutException or System.Net.Sockets.SocketException || exception.InnerException is DbException;
}
