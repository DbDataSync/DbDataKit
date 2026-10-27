using System.Data.Common;

namespace DbDataSync.TestSupport;

/// <summary>
/// Where the test engines are, read from the <c>DBDATASYNC_TEST_*</c> variables that <c>testenv/testenv.sh env dbdatasync</c> prints, so
/// the places that need the port or the password on their own (a <c>ConnectionInput</c> with a <c>Port</c>, a secret preset for a spawned
/// worker) say the same thing as the connection string the same test uses. Linked into every test project that needs it (see each csproj's
/// <c>Shared\TestEngines.cs</c> item), like <c>MsSqlScratch</c>.
/// <para>
/// The defaults are the values the old <c>DbDataSync/docker-compose.yml</c> had (password <c>DbDataSync_Test_Pw1</c>, ports 14330, 14331,
/// 15432, 13306), kept so that a variable that is not set behaves as before. The shared <c>testenv/</c> engines use another password
/// (<c>DbKit_Test_Pw1</c>) and Sync's PostgreSQL is on 15433, so run the suites after <c>eval "$(testenv/testenv.sh env dbdatasync)"</c>.
/// </para>
/// </summary>
internal static class TestEngines
{
    /// <summary>The first SQL Server (<c>DBDATASYNC_TEST_MSSQL_SERVER</c>, or the source's variable): port 14330, SQL Server Agent on.</summary>
    public static Engine MsSql { get; } = From(
        "Data Source=localhost,14330;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True",
        "DBDATASYNC_TEST_MSSQL_SERVER", "DBDATASYNC_TEST_MSSQL_SOURCE_SERVER");

    /// <summary>The second SQL Server (<c>DBDATASYNC_TEST_MSSQL_TARGET_SERVER</c>): port 14331.</summary>
    public static Engine MsSqlTarget { get; } = From(
        "Data Source=localhost,14331;User ID=sa;Password=DbDataSync_Test_Pw1;TrustServerCertificate=True",
        "DBDATASYNC_TEST_MSSQL_TARGET_SERVER");

    /// <summary>The PostgreSQL with logical decoding (<c>DBDATASYNC_TEST_POSTGRES_SERVER</c>).</summary>
    public static Engine Postgres { get; } = From(
        "Host=localhost;Port=15432;Username=dbdatasync;Password=DbDataSync_Test_Pw1;Database=postgres",
        "DBDATASYNC_TEST_POSTGRES_SERVER");

    /// <summary>MySQL (<c>DBDATASYNC_TEST_MYSQL_SERVER</c>), as root.</summary>
    public static Engine MySql { get; } = From(
        "Server=localhost;Port=13306;UserID=root;Password=DbDataSync_Test_Pw1;AllowUserVariables=true;",
        "DBDATASYNC_TEST_MYSQL_SERVER");

    private static Engine From(string defaultConnectionString, params string[] variables)
    {
        var connectionString = variables
            .Select(Environment.GetEnvironmentVariable)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? defaultConnectionString;
        return new Engine(connectionString);
    }

    /// <summary>One engine's connection string and the parts of it a test needs separately.</summary>
    internal sealed class Engine
    {
        private readonly DbConnectionStringBuilder _builder;

        public Engine(string connectionString)
        {
            ConnectionString = connectionString;
            _builder = new DbConnectionStringBuilder { ConnectionString = connectionString };
            var address = Get("Data Source", "Server", "Host", "Address", "Addr", "Network Address") ?? "localhost";
            var comma = address.IndexOf(',');
            var port = comma >= 0 ? address[(comma + 1)..] : Get("Port");
            Host = comma >= 0 ? address[..comma] : address;
            Port = int.TryParse(port, out var parsed) ? parsed : 0;
            User = Get("User ID", "UserID", "User Id", "Username", "User", "UID") ?? "";
            Password = Get("Password", "PWD") ?? "";
        }

        public string ConnectionString { get; }
        public string Host { get; }
        public int Port { get; }
        public string User { get; }
        public string Password { get; }

        private string? Get(params string[] keys)
        {
            foreach (var key in keys)
                if (_builder.TryGetValue(key, out var value) && value is string text && text.Length > 0)
                    return text;
            return null;
        }
    }
}
